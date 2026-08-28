#!/usr/bin/env python3
"""Split a batch of new catalogue arrivals into "already decided" and "genuinely new".

When the provider adds content, the movie de-dup view shows every new StreamId as a fresh
unreviewed row — including new streams for films the user already excluded or already
reviewed and kept. ``AggregateVodByStreamId`` groups on StreamId, so a second stream for
the same film is a separate row and the earlier decision does not reach it.

That is the movie-side equivalent of the series Path-A quirk, and it is the thing TMDB
keying would fix. This quantifies it: given two snapshots either side of an arrival batch,
how much of the new review workload is re-deciding titles that were already decided?

Answers three questions:
  * how many arrivals are new streams for a title already on the blocklist
    (TMDB keying would have excluded them automatically, no review needed)
  * how many are new streams for a title already reviewed and kept
    (TMDB keying would have marked them reviewed, so they would not surface as a worklist)
  * how many carry a TMDB id never seen before — genuinely new films that do warrant review

Read-only: two snapshot files and the config XML. No provider calls at all.

Usage:
  python3 scripts/analyse-new-arrivals.py --before BEFORE.tsv --after AFTER.tsv
"""

import argparse
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import xtream_catalogue as xc  # noqa: E402

FOLDER_TMDB = re.compile(r"\[tmdbid=(\d+)\]")


def ids_of_kind(snapshot, kind):
    return {item_id for (k, item_id) in snapshot if k == kind}


def library_tmdb_ids(library_path):
    """TMDB ids recorded in folder names under a STRM library tree.

    Every folder the sync has ever written for a kept title carries [tmdbid=N], so this is
    a record of past *keep* decisions that long outlives the provider ids those decisions
    were stored against. It is the only evidence available for an id that died before the
    earliest snapshot — which is most of them.
    """
    found = set()
    if not library_path:
        return found
    for root, dirs, _files in os.walk(library_path):
        for name in dirs:
            match = FOLDER_TMDB.search(name)
            if match:
                found.add(int(match.group(1)))
    return found


def main(argv):
    parser = argparse.ArgumentParser(
        description="Split new catalogue arrivals into already-decided vs genuinely new.")
    parser.add_argument("--before", required=True, help="snapshot taken before the arrivals")
    parser.add_argument("--after", required=True, help="snapshot taken after")
    parser.add_argument("--config-glob", help="override the config XML search pattern")
    parser.add_argument("--library", help="STRM library tree; folder [tmdbid=N] names are read as "
                                          "evidence of a past keep decision")
    parser.add_argument("--samples", type=int, default=10)
    args = parser.parse_args(argv)

    config_path = xc.find_config(args.config_glob)
    root = xc.load_config(config_path).getroot()
    stores = xc.read_all_stores(root)

    before = xc.read_snapshot(args.before)
    after = xc.read_snapshot(args.after)
    print("config : %s" % config_path)
    print("before : %s" % args.before)
    print("after  : %s\n" % args.after)

    # Resolve a provider id to its TMDB id using whichever snapshot knows it. A stored id
    # may be long dead, so the older snapshot is often the only place it appears.
    tmdb_of = {}
    for source in (before, after):
        for (kind, item_id), (tmdb, _name) in source.items():
            if tmdb and (kind, item_id) not in tmdb_of:
                tmdb_of[(kind, item_id)] = tmdb
    name_of = {key: value[1] for key, value in after.items()}

    lib_tmdb = library_tmdb_ids(args.library)
    if args.library:
        print("library: %s  (%d distinct TMDB ids in folder names)\n" % (args.library, len(lib_tmdb)))

    for kind, excluded_store, reviewed_store in (
            ("movie", "ExcludedVodStreamIds", "ReviewedVodStreamIdsJson"),
            ("series", "ExcludedSeriesIds", "ReviewedSeriesIdsJson")):

        before_ids = ids_of_kind(before, kind)
        after_ids = ids_of_kind(after, kind)
        arrived = sorted(after_ids - before_ids)
        departed = before_ids - after_ids

        print("=" * 78)
        print("%s: %d before -> %d after   (+%d arrived, -%d departed)"
              % (kind.upper(), len(before_ids), len(after_ids), len(arrived), len(departed)))

        if not arrived:
            print("  no new arrivals in this window\n")
            continue

        # TMDB ids the user has already made a call on, either way.
        excluded_tmdb = {tmdb_of[(kind, i)] for i in stores[excluded_store] if (kind, i) in tmdb_of}
        reviewed_tmdb = {tmdb_of[(kind, i)] for i in stores[reviewed_store] if (kind, i) in tmdb_of}

        dup_excluded, dup_reviewed, in_library, genuinely_new, no_tmdb = [], [], [], [], []
        for item_id in arrived:
            tmdb = after.get((kind, item_id), (0, ""))[0]
            if not tmdb:
                no_tmdb.append(item_id)
            elif tmdb in excluded_tmdb:
                dup_excluded.append(item_id)
            elif tmdb in reviewed_tmdb:
                dup_reviewed.append(item_id)
            elif tmdb in lib_tmdb:
                # Not resolvable from either store — the id it was decided against died
                # before the earliest snapshot — but the film is on disk, so it was kept.
                in_library.append(item_id)
            else:
                genuinely_new.append(item_id)

        total = len(arrived)

        def line(label, bucket):
            pct = 100.0 * len(bucket) / total if total else 0.0
            print("  %-52s %6d  (%4.1f%%)" % (label, len(bucket), pct))

        line("new stream for a title you ALREADY EXCLUDED", dup_excluded)
        line("new stream for a title you already reviewed+kept", dup_reviewed)
        if lib_tmdb:
            line("already ON DISK (folder [tmdbid=]) — a re-addition", in_library)
        line("genuinely new title (TMDB id never seen)", genuinely_new)
        line("no usable TMDB id — cannot tell", no_tmdb)

        avoidable = len(dup_excluded) + len(dup_reviewed) + len(in_library)
        pct = 100.0 * avoidable / total if total else 0.0
        print("  %-52s %6d  (%4.1f%%)" % ("--> arrivals you have already decided about", avoidable, pct))

        for label, bucket in (("already excluded", dup_excluded),
                              ("already reviewed+kept", dup_reviewed),
                              ("already on disk", in_library),
                              ("genuinely new", genuinely_new)):
            if not bucket:
                continue
            print("\n  sample — %s:" % label)
            for item_id in bucket[:args.samples]:
                tmdb = after.get((kind, item_id), (0, ""))[0]
                print("      id=%-8d tmdb=%-8s %s" % (item_id, tmdb or "-", name_of.get((kind, item_id), "")[:52]))
        print()

    print("=" * 78)
    print("Series carry no TMDB id on the list payload, so their arrivals cannot be")
    print("classified here - that is the same measured 0% coverage that ruled TMDB keying")
    print("out for series exclusion (ADR-016). Series arrivals are covered instead by the")
    print("collapse-group propagation, which matches on name.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
