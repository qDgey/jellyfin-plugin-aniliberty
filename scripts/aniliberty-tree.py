#!/usr/bin/env python3
"""Build Jellyfin-friendly symlink trees from the AniLiberty torrent archive.

Series: every torrent folder of a release (AVC, HEVC, 720p...) is merged into one series folder, and each
episode's copies are named as versions of the same episode, so Jellyfin shows one episode with a version picker:

/media/aniliberty/86 - Eighty Six - AniLibria.TV [WEBRip 1080p]/86_Eighty_Six_[01]_[AniLibria_TV]_[WEBRip_1080p].mkv
/media/aniliberty/86 - Eighty Six - AniLibria.TV [WEBRip 1080p HEVC]/86_Eighty_Six_[01]_..._HEVC].mkv
  ->
/srv/aniliberty-series/86 - Eighty Six/Season 01/86 - Eighty Six S01E01 - WEBRip 1080p.mkv
/srv/aniliberty-series/86 - Eighty Six/Season 01/86 - Eighty Six S01E01 - WEBRip 1080p HEVC.mkv

Movies: loose video files at the archive root, grouped the same way:
/srv/aniliberty-movies/<Title>/<Title> - <version>.mkv

Files whose episode number can't be read keep their original name (no version merging for them).
The metadata plugin follows the symlinks back to the torrent folder/file name to identify the release.
"""
import os
import re
import sys

# Paths can be overridden with environment variables (see aniliberty-tree.service).
SRC = os.environ.get("ANILIBERTY_SOURCE", "/media/aniliberty")
SERIES = os.environ.get("ANILIBERTY_SERIES", "/srv/aniliberty-series")
MOVIES = os.environ.get("ANILIBERTY_MOVIES", "/srv/aniliberty-movies")
VIDEO = re.compile(r"\.(mkv|mp4|avi|m4v)$", re.I)
GROUP_TAG = re.compile(r"^anili(bria|berty)", re.I)
BRACKET_EPISODE = re.compile(r"\[(\d{1,4})(?:v\d)?(?:[ _]END)?\]", re.I)


def split(name):
    """Torrent/file name → (title, version label)."""
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


def listdir(path):
    try:
        return sorted(os.listdir(path))
    except OSError:
        return []


def plan():
    wanted = {}

    def put(link, target):
        base, ext = os.path.splitext(link)
        n = 2
        while link in wanted and wanted[link] != target:
            link = f"{base} {n}{ext}"
            n += 1
        wanted[link] = target

    for name in listdir(SRC):
        path = os.path.join(SRC, name)
        if os.path.isdir(path):
            title, label = split(name)
            season = os.path.join(SERIES, title, "Season 01")
            for f in listdir(path):
                if not VIDEO.search(f):
                    continue
                target = os.path.join(path, f)
                ext = os.path.splitext(f)[1].lower()
                num = episode_number(f)
                if num is None:
                    put(os.path.join(season, f), target)
                else:
                    put(os.path.join(season, f"{title} S01E{num:02d} - {label}{ext}"), target)
        elif VIDEO.search(name) and os.path.isfile(path):
            title, label = split(name)
            ext = os.path.splitext(name)[1].lower()
            put(os.path.join(MOVIES, title, f"{title} - {label}{ext}"), path)
    return wanted


def sync(wanted, root):
    created = removed = 0
    for link, target in wanted.items():
        if not link.startswith(root + "/"):
            continue
        if os.path.islink(link) and os.readlink(link) == target:
            continue
        os.makedirs(os.path.dirname(link), exist_ok=True)
        if os.path.lexists(link):
            os.remove(link)
        os.symlink(target, link)
        created += 1
    for dirpath, _, files in os.walk(root, topdown=False):
        for f in files:
            p = os.path.join(dirpath, f)
            if os.path.islink(p) and p not in wanted:
                os.remove(p)
                removed += 1
        if dirpath != root and not os.listdir(dirpath):
            os.rmdir(dirpath)
    return created, removed


def main():
    if not os.path.isdir(SRC) or not listdir(SRC):
        # Archive not mounted: don't wipe the trees (Jellyfin would drop every item).
        print(f"{SRC} is empty or missing, skipping", file=sys.stderr)
        return 1
    wanted = plan()
    for root in (SERIES, MOVIES):
        os.makedirs(root, exist_ok=True)
        c, r = sync(wanted, root)
        n = sum(1 for k in wanted if k.startswith(root + "/"))
        print(f"{root}: {n} files, {c} links created, {r} removed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
