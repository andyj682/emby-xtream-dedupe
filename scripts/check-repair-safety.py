#!/usr/bin/env python3
"""Gate before installing a repair-id-churn candidate: would any PROPOSED EXCLUSION
land on a title that is currently on disk?

repair-id-churn.py already refuses to re-exclude an id you have since reviewed-and-kept.
That guard reads the REVIEWED SET. It cannot see titles that are on disk but were never
recorded as reviewed — anything written before RequireReviewBeforeSync existed. Such a
title would be excluded by the candidate, and RemoveExcludedContent deletes folders with
NO ratio guard.

This checks the other half: the ids actually embedded in the .strm files on disk.

Read-only. Exit 0 = safe to install, exit 1 = collisions found, do not install.

Usage:
  python3 check-repair-safety.py --additions /out/additions.txt --library /library/Movies
"""

import argparse
import os
import re
import sys

# Folder-name suffix the plugin appends: "Title [tmdbid=603]" / "[tvdbid=1234]".
ID_SUFFIX = re.compile(r"\s*\[(?:tmdb|tvdb)id=\d+\]\s*$", re.IGNORECASE)

# {base}/movie/{user}/{pass}/{StreamId}.{ext}
XC_ID = re.compile(r"/movie/[^/]*/[^/]*/(\d+)\.[A-Za-z0-9]+\s*$")
# {dispatcharr}/proxy/vod/movie/{uuid}?stream_id={id}
PROXY_ID = re.compile(r"[?&]stream_id=(\d+)")


def read_additions(path, store="ExcludedVodStreamIds"):
    """Parse the block for one store out of additions.txt.

    Format is a '# <Element> (<kind>) — N additions' header, then one id per line,
    then a blank line before the next header.
    """
    ids, in_block = set(), False
    with open(path, "r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if line.startswith("#"):
                in_block = line[1:].lstrip().startswith(store)
                continue
            if not line:
                continue
            if in_block and line.isdigit():
                ids.add(int(line))
    return ids


def read_ondisk_ids(library):
    """Map stream id -> a sample .strm path, for every movie .strm under `library`."""
    found = {}
    unparsed = 0
    for root, _dirs, files in os.walk(library):
        for name in files:
            if not name.endswith(".strm"):
                continue
            path = os.path.join(root, name)
            try:
                with open(path, "r", encoding="utf-8", errors="replace") as handle:
                    url = handle.read().strip()
            except OSError:
                unparsed += 1
                continue
            if not url:
                unparsed += 1
                continue
            match = PROXY_ID.search(url) or XC_ID.search(url)
            if match:
                found.setdefault(int(match.group(1)), path)
            else:
                unparsed += 1
    return found, unparsed


def normalize(name):
    """Aggressive fold for NAME comparison: lowercase, alphanumerics only.

    RemoveExcludedContent compares SanitizeFileName(title) against on-disk folder names
    with the id suffix stripped, and the plugin may also have applied
    ContentNameCleaner.CleanContentName first (which depends on ContentRemoveTerms and
    cannot be reproduced here). So this deliberately OVER-matches: a gate should raise
    false alarms, never miss.
    """
    return re.sub(r"[^a-z0-9]", "", (name or "").lower())


def read_snapshot_names(path, kind="movie"):
    """id -> title, from a catalogue-ids-*.tsv (kind / id / tmdb / name)."""
    names = {}
    with open(path, "r", encoding="utf-8") as handle:
        for line in handle:
            parts = line.rstrip("\n").split("\t")
            if len(parts) < 4 or parts[0] != kind:
                continue
            try:
                names[int(parts[1])] = parts[3]
            except ValueError:
                continue
    return names


def read_ondisk_folders(library):
    """normalized folder name -> actual path, for every title folder under `library`."""
    folders = {}
    for entry in sorted(os.listdir(library)):
        full = os.path.join(library, entry)
        if not os.path.isdir(full):
            continue
        folders.setdefault(normalize(ID_SUFFIX.sub("", entry)), full)
    return folders


def main(argv):
    parser = argparse.ArgumentParser(
        description="Refuse a repair candidate that would exclude an on-disk title.")
    parser.add_argument("--additions", required=True, help="additions.txt from repair-id-churn.py")
    parser.add_argument("--library", required=True, help="path to the Movies STRM tree")
    parser.add_argument("--store", default="ExcludedVodStreamIds",
                        help="which store's additions to check (default: ExcludedVodStreamIds)")
    parser.add_argument("--snapshot", metavar="PATH",
                        help="a CURRENT catalogue-ids-*.tsv; enables the name-collision check "
                             "against RemoveExcludedContent, which matches on NAME not id")
    args = parser.parse_args(argv)

    proposed = read_additions(args.additions, args.store)
    ondisk, unparsed = read_ondisk_ids(args.library)

    print("proposed %s additions : %d" % (args.store, len(proposed)))
    print("stream ids on disk        : %d  (%d .strm unreadable/unrecognised)"
          % (len(ondisk), unparsed))

    failed = False

    # --- Check 1: stream id collision (CleanupOrphans / writtenPaths axis) -------------
    collisions = sorted(proposed & set(ondisk))
    print()
    print("[1] stream id collisions")
    if not collisions:
        print("    SAFE — no proposed exclusion matches a stream id on disk.")
    else:
        failed = True
        print("    *** %d COLLISION(S) ***" % len(collisions))
        for sid in collisions[:25]:
            print("      %-10d %s" % (sid, ondisk[sid]))
        if len(collisions) > 25:
            print("      ... and %d more" % (len(collisions) - 25))

    # --- Check 2: NAME collision (RemoveExcludedContent axis) -------------------------
    # This is the one that matters most: RemoveExcludedContent is NOT gated by
    # CleanupOrphans and deletes the folder with no ratio guard.
    print()
    print("[2] folder-name collisions (RemoveExcludedContent)")
    if not args.snapshot:
        failed = True
        print("    NOT CHECKED — pass --snapshot with a CURRENT catalogue snapshot.")
        print("    RemoveExcludedContent matches on NAME, so check 1 alone does not cover it.")
    else:
        names = read_snapshot_names(args.snapshot)
        folders = read_ondisk_folders(args.library)
        hits = []
        for sid in sorted(proposed):
            title = names.get(sid)
            if not title:
                continue
            key = normalize(title)
            if key and key in folders:
                hits.append((sid, title, folders[key]))
        print("    proposed titles resolved from snapshot : %d of %d"
              % (sum(1 for s in proposed if s in names), len(proposed)))
        print("    title folders on disk                  : %d" % len(folders))
        if not hits:
            print("    SAFE — no proposed exclusion's name matches an on-disk folder.")
        else:
            failed = True
            print("    *** %d NAME COLLISION(S) ***" % len(hits))
            for sid, title, path in hits[:25]:
                print("      %-10d %-45s %s" % (sid, title[:45], path))
            if len(hits) > 25:
                print("      ... and %d more" % (len(hits) - 25))

    print()
    print("=" * 70)
    if failed:
        print("*** DO NOT INSTALL / DO NOT SYNC until the above is resolved. ***")
        return 1
    print("SAFE on both axes — no on-disk folder can be deleted by these exclusions.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
