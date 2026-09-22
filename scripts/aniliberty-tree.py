#!/usr/bin/env python3
"""Build Jellyfin-friendly symlink trees from an AniLiberty (AniLibria) torrent archive.

Series: every torrent folder of a release (AVC, HEVC, 720p...) is merged into one series folder, and the copies
of each episode are named as versions of one episode, so Jellyfin shows one episode with a version picker:

  <source>/86 - Eighty Six - AniLibria.TV [WEBRip 1080p]/86_Eighty_Six_[01]_[AniLibria_TV]_[WEBRip_1080p].mkv
  <source>/86 - Eighty Six - AniLibria.TV [WEBRip 1080p HEVC]/86_Eighty_Six_[01]_..._HEVC].mkv
    ->
  <series>/86 - Eighty Six/Season 01/86 - Eighty Six S01E01 - WEBRip 1080p.mkv
  <series>/86 - Eighty Six/Season 01/86 - Eighty Six S01E01 - WEBRip 1080p HEVC.mkv

Movies: single-file torrents (a loose file, or a folder holding one file without an episode number):

  <movies>/<Title>/<Title> - <version>.mkv

The archive layout is discovered, not assumed. Each source is walked recursively:
  * a folder that directly contains videos (and no other folders) is a torrent folder: one release;
  * season folders inside a release ("Season 2", "Сезон 2", "S02") are that release's seasons;
  * any other folder (the source itself, years, "Movies", "Ongoing", ...) is a grouping folder:
    it is descended into, and loose videos in it are single-file torrents (movies);
  * hidden folders, extras/samples and names matching the exclude pattern are skipped.

Configuration: command-line options, or environment variables (the systemd unit reads them from
/etc/aniliberty-tree.conf):
  ANILIBERTY_SOURCES    archive roots, separated by ":" (ANILIBERTY_SOURCE is accepted too)
  ANILIBERTY_SERIES     series tree   (default /srv/aniliberty-series)
  ANILIBERTY_MOVIES     movie tree    (default /srv/aniliberty-movies)
  ANILIBERTY_MAX_DEPTH  how many grouping levels to descend under each source (default 4)
  ANILIBERTY_EXCLUDE    regex of folder/file names to skip (optional)

The metadata plugin follows the symlinks back to torrent folder/file names to identify the release.
"""
import argparse
import os
import re
import sys
from collections import Counter

VIDEO = re.compile(r"\.(mkv|mp4|avi|m4v|ts|webm)$", re.I)
GROUP_TAG = re.compile(r"^anili(bria|berty)", re.I)
BRACKET_EPISODE = re.compile(r"\[(\d{1,4})(?:v\d)?(?:[ _]END)?\]", re.I)
SEASON_DIR = re.compile(r"^(?:season|сезон|s)[ ._-]?(\d{1,2})$", re.I)
SKIP_DIR = re.compile(
    r"^(?:[.@]|#recycle$|lost\+found$)"
    r"|^(?:extras?|bonus(?:es)?|бонусы|samples?|featurettes?|trailers?|ncop|nced|creditless|scans?)$",
    re.I,
)
SAMPLE_FILE = re.compile(r"(?:^|[ ._-])sample(?:[ ._-]|$)", re.I)


def split(name):
    """Torrent/file name -> (title, version label)."""
    stem = VIDEO.sub("", name).replace("_", " ")
    tags = [t.strip() for t in re.findall(r"\[([^\]]*)\]", stem)]
    title = re.split(r"\s-?\s*\[?\s*anili(?:bria|berty)", stem, flags=re.I)[0]
    title = re.sub(r"\[[^\]]*\]|\([^)]*\)", " ", title)
    title = re.sub(r"\s+", " ", title).strip(" -.").replace("/", " ")
    label = " ".join(t for t in tags if t and not GROUP_TAG.match(t) and not re.fullmatch(r"\d{1,4}", t))
    return title or stem.strip(), label or "default"


def episode_number(filename):
    m = BRACKET_EPISODE.search(VIDEO.sub("", filename))
    return int(m.group(1)) if m else None


def scan(path):
    """-> (subfolders, video files) of a folder; an unreadable folder is empty."""
    dirs, videos = [], []
    try:
        with os.scandir(path) as it:
            for e in it:
                try:
                    if e.is_dir():
                        dirs.append(e.name)
                    elif VIDEO.search(e.name) and e.is_file() and not SAMPLE_FILE.search(VIDEO.sub("", e.name)):
                        videos.append(e.name)
                except OSError:
                    pass
    except OSError:
        pass
    return sorted(dirs), sorted(videos)


class Planner:
    def __init__(self, series_root, movies_root, max_depth, exclude):
        self.series_root = series_root
        self.movies_root = movies_root
        self.max_depth = max_depth
        self.exclude = exclude
        self.wanted = {}
        self.stats = Counter()

    def skip(self, name):
        return bool(SKIP_DIR.search(name) or (self.exclude and self.exclude.search(name)))

    def put(self, link, target):
        base, ext = os.path.splitext(link)
        n = 2
        while link in self.wanted and self.wanted[link] != target:
            link = f"{base} {n}{ext}"
            n += 1
        self.wanted[link] = target

    def movie(self, folder, filename, torrent_name):
        title, label = split(torrent_name)
        ext = os.path.splitext(filename)[1].lower()
        self.put(os.path.join(self.movies_root, title, f"{title} - {label}{ext}"), os.path.join(folder, filename))
        self.stats["movies"] += 1

    def episodes(self, folder, torrent_name, videos, season):
        title, label = split(torrent_name)
        season_dir = os.path.join(self.series_root, title, f"Season {season:02d}")
        for f in videos:
            num = episode_number(f)
            if num is None:
                # Unreadable numbering: keep the original name, Jellyfin parses it itself (no version merging).
                self.put(os.path.join(season_dir, f), os.path.join(folder, f))
                self.stats["episodes kept as is"] += 1
            else:
                ext = os.path.splitext(f)[1].lower()
                self.put(os.path.join(season_dir, f"{title} S{season:02d}E{num:02d} - {label}{ext}"), os.path.join(folder, f))
                self.stats["episodes"] += 1

    def release(self, folder, dirs, videos):
        """A torrent folder. Its season subfolders (if any) are its seasons."""
        name = os.path.basename(folder)
        if not dirs and len(videos) == 1 and episode_number(videos[0]) is None:
            # Single-file torrent packed in a folder: a movie; the folder carries the torrent name.
            self.movie(folder, videos[0], name)
            return
        self.stats["releases"] += 1
        self.episodes(folder, name, videos, 1)
        for d in dirs:
            m = SEASON_DIR.match(d)
            _, season_videos = scan(os.path.join(folder, d))
            self.episodes(os.path.join(folder, d), name, season_videos, int(m.group(1)))

    def walk(self, folder, depth):
        """A grouping folder: loose videos are movies, subfolders are releases or more grouping folders."""
        dirs, videos = scan(folder)
        for f in videos:
            self.movie(folder, f, f)
        for d in dirs:
            if self.skip(d):
                self.stats["skipped"] += 1
                continue
            sub = os.path.join(folder, d)
            sub_dirs, sub_videos = scan(sub)
            sub_dirs = [x for x in sub_dirs if not self.skip(x)]
            if sub_videos and all(SEASON_DIR.match(x) for x in sub_dirs):
                self.release(sub, sub_dirs, sub_videos)
            elif not sub_videos and sub_dirs and all(SEASON_DIR.match(x) for x in sub_dirs):
                # "Title/Season 1/…", "Title/Season 2/…" without files at the top.
                self.release(sub, sub_dirs, [])
            elif depth < self.max_depth:
                self.walk(sub, depth + 1)
            else:
                self.stats["too deep"] += 1


def sync(wanted, root, dry_run):
    created = removed = 0
    for link, target in wanted.items():
        if not link.startswith(root + os.sep):
            continue
        if os.path.islink(link) and os.readlink(link) == target:
            continue
        created += 1
        if dry_run:
            continue
        os.makedirs(os.path.dirname(link), exist_ok=True)
        if os.path.lexists(link):
            os.remove(link)
        os.symlink(target, link)
    if os.path.isdir(root):
        for dirpath, _, files in os.walk(root, topdown=False):
            for f in files:
                p = os.path.join(dirpath, f)
                if os.path.islink(p) and p not in wanted:
                    removed += 1
                    if not dry_run:
                        os.remove(p)
            if not dry_run and dirpath != root and not os.listdir(dirpath):
                os.rmdir(dirpath)
    return created, removed


def main():
    env = os.environ.get
    ap = argparse.ArgumentParser(description="Build Jellyfin symlink trees from an AniLiberty torrent archive.")
    ap.add_argument("--source", action="append", help="archive root (repeatable); default: $ANILIBERTY_SOURCES")
    ap.add_argument("--series", default=env("ANILIBERTY_SERIES", "/srv/aniliberty-series"))
    ap.add_argument("--movies", default=env("ANILIBERTY_MOVIES", "/srv/aniliberty-movies"))
    ap.add_argument("--max-depth", type=int, default=int(env("ANILIBERTY_MAX_DEPTH", "4")))
    ap.add_argument("--exclude", default=env("ANILIBERTY_EXCLUDE", ""), help="regex of folder/file names to skip")
    ap.add_argument("--dry-run", action="store_true", help="only report what would change")
    ap.add_argument("--list", type=int, default=0, metavar="N", help="also print N sample links")
    args = ap.parse_args()

    raw = args.source or (env("ANILIBERTY_SOURCES") or env("ANILIBERTY_SOURCE") or "/media/aniliberty").split(":")
    sources = [os.path.abspath(s) for s in raw if s]
    for s in sources:
        if not os.path.isdir(s) or not os.listdir(s):
            # An unmounted source must not wipe its part of the trees (Jellyfin would drop the items).
            print(f"source {s} is empty or missing, nothing changed", file=sys.stderr)
            return 1

    exclude = re.compile(args.exclude, re.I) if args.exclude else None
    planner = Planner(os.path.abspath(args.series), os.path.abspath(args.movies), args.max_depth, exclude)
    for s in sources:
        planner.walk(s, 1)

    if args.list:
        links = sorted(planner.wanted)
        for link in links[:: max(1, len(links) // args.list)][: args.list]:
            print(f"  {link}\n    -> {planner.wanted[link]}")

    print("found: " + (", ".join(f"{v} {k}" for k, v in sorted(planner.stats.items())) or "nothing"))
    for root in (planner.series_root, planner.movies_root):
        if not args.dry_run:
            os.makedirs(root, exist_ok=True)
        c, r = sync(planner.wanted, root, args.dry_run)
        n = sum(1 for k in planner.wanted if k.startswith(root + os.sep))
        if args.dry_run:
            print(f"{root}: {n} links, would create/update {c}, would remove {r}")
        else:
            print(f"{root}: {n} links, created/updated {c}, removed {r}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
