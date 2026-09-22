#!/usr/bin/env python3
"""Build a Jellyfin movie tree from the loose video files at the root of the AniLibria archive.

/media/anilibria/Fairy_Tail_Dragon_Cry_[AniLibria_TV]_[BDRip_1080p].mkv
/media/anilibria/Fairy_Tail_Dragon_Cry_[AniLibria]_[BDRip_1080p_HEVC].mkv
  ->
/srv/anilibria-movies/Fairy Tail Dragon Cry/Fairy Tail Dragon Cry - BDRip 1080p.mkv
/srv/anilibria-movies/Fairy Tail Dragon Cry/Fairy Tail Dragon Cry - BDRip 1080p HEVC.mkv

Files named "<folder> - <label>" in one folder are shown by Jellyfin as one movie with versions.
The plugin follows the symlink back to the torrent name to find the release.
"""
import os
import re
import sys

SRC = sys.argv[1] if len(sys.argv) > 1 else "/media/anilibria"
DST = sys.argv[2] if len(sys.argv) > 2 else "/srv/anilibria-movies"
VIDEO = re.compile(r"\.(mkv|mp4|avi|m4v)$", re.I)
GROUP = re.compile(r"^anili(bria|berty)", re.I)


def split(name):
    stem, ext = os.path.splitext(name)
    stem = stem.replace("_", " ")
    tags = [t.strip() for t in re.findall(r"\[([^\]]*)\]", stem)]
    title = re.split(r"\s-?\s*\[?\s*anili(?:bria|berty)", stem, flags=re.I)[0]
    title = re.sub(r"\[[^\]]*\]|\([^)]*\)", " ", title)
    title = re.sub(r"\s+", " ", title).strip(" -.")
    label = " ".join(t for t in tags if t and not GROUP.match(t)) or "default"
    return title or stem.strip(), label, ext.lower()


def main():
    wanted = {}
    for name in sorted(os.listdir(SRC)):
        path = os.path.join(SRC, name)
        if not VIDEO.search(name) or not os.path.isfile(path):
            continue
        title, label, ext = split(name)
        title = title.replace("/", " ")
        link = os.path.join(DST, title, f"{title} - {label}{ext}")
        n = 2
        while link in wanted:
            link = os.path.join(DST, title, f"{title} - {label} {n}{ext}")
            n += 1
        wanted[link] = path

    created = removed = 0
    for link, target in wanted.items():
        if os.path.islink(link) and os.readlink(link) == target:
            continue
        os.makedirs(os.path.dirname(link), exist_ok=True)
        if os.path.lexists(link):
            os.remove(link)
        os.symlink(target, link)
        created += 1

    for root, dirs, files in os.walk(DST, topdown=False):
        for f in files:
            p = os.path.join(root, f)
            if os.path.islink(p) and p not in wanted:
                os.remove(p)
                removed += 1
        if root != DST and not os.listdir(root):
            os.rmdir(root)

    print(f"movies: {len(wanted)} files, {created} links created, {removed} removed")


if __name__ == "__main__":
    main()
