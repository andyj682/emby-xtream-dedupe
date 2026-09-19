#!/usr/bin/env python3
"""Diff two catalogue snapshots: tell an id REASSIGNMENT from a title REMOVAL.

When decisions detach en masse, two very different things look identical from the plugin's
side: the provider renumbered its catalogue (the titles are still there under new ids), or
the titles are genuinely gone. Only a snapshot taken before the event can separate them,
and this is the tool that does it.

It also answers the question that identifies the *mechanism*. If ids moved, the shape of
the movement says how:

  * One dominant offset, covering a contiguous run of old ids, means the rows were DELETED
    AND RE-INSERTED IN THE SAME ORDER into a sequence that had advanced. That is a bulk
    delete/recreate on the proxy side, not the provider reissuing its own identifiers.
  * Many scattered offsets mean something did the renumbering per-title.

That distinction matters because the two have different fixes and different blast radii,
and because a delete/recreate can be triggered by a source answering *successfully but
short* — a failure mode with no ratio guard on either side of this integration.

WHAT IT CANNOT TELL YOU
  It establishes that rows were recreated. It does NOT establish *who* recreated them —
  an unguarded cleanup, a user-triggered re-ingest and a deliberate account refresh all
  leave the same trace. Read the proxy's own logs for that. Do not infer the cause from
  the offset alone.

Movies are matched on TMDB id, which survives a renumber. Series carry no TMDB id on the
list payload, so they match on name — the same key the plugin's own series grouping uses,
and it fails in the one case worth stating: if the provider changed its NAMING convention
at the same time it renumbered, neither key resolves and every title reads as removed.

Read-only. Two files, no config, no provider calls, nothing written.

⚠️ The report quotes real catalogue titles. Fine locally; do not paste it into an issue,
a commit message or anything else public.

Usage:
  python3 scripts/diff-catalogue-ids.py --before BEFORE.tsv --after AFTER.tsv
  python3 scripts/diff-catalogue-ids.py --before B.tsv --after A.tsv --kind movie --samples 20
"""

import argparse
import os
import sys
from collections import Counter

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import xtream_catalogue as xc  # noqa: E402

KINDS = ("movie", "series")

# How many of the largest offset groups to describe. More than a handful stops being a
# summary; if the real answer needs ten, the answer is "scattered" and that is already clear
# from the top few.
TOP_OFFSETS = 5


def identity_index(snapshot, kind):
    """{identity key -> lowest id}, plus the rows that could not be keyed.

    Lowest id wins a collision, matching the plugin's own re-point target selection, so the
    answer cannot flip between runs over the same pair of files.
    """
    best = {}
    names = {}
    collisions = 0
    unkeyable = 0

    for (row_kind, item_id), (tmdb, name) in snapshot.items():
        if row_kind != kind:
            continue

        if kind == "movie":
            key = tmdb
        else:
            key = (name or "").strip().lower() or None

        if key is None or key == "":
            unkeyable += 1
            continue

        if key in best:
            collisions += 1
            if item_id >= best[key]:
                continue

        best[key] = item_id
        names[key] = name

    return best, names, collisions, unkeyable


def contiguity(old_ids):
    """How densely a set of old ids fills the range it spans.

    A bulk re-insert leaves its source rows in one solid run; a per-title change leaves them
    scattered through the id space. Returned as (count, span) so the caller can report both
    numbers rather than a ratio that hides which one moved.
    """
    lo, hi = min(old_ids), max(old_ids)
    return len(old_ids), hi - lo + 1


def describe_kind(kind, before, after, samples):
    b_index, b_names, b_collisions, b_unkeyable = identity_index(before, kind)
    a_index, _, a_collisions, a_unkeyable = identity_index(after, kind)

    print("")
    print("=" * 78)
    print("%s" % kind.upper())
    print("=" * 78)

    matched_on = "TMDB id" if kind == "movie" else "name"
    print("  keyed on %s: %d before, %d after" % (matched_on, len(b_index), len(a_index)))
    if b_unkeyable or a_unkeyable:
        # Reported rather than swallowed: a large unkeyable count means the diff is blind to
        # that slice, and a blind spot reported as zero is worse than one reported as itself.
        print("  NOT KEYABLE (no %s, invisible to this diff): %d before, %d after"
              % (matched_on, b_unkeyable, a_unkeyable))
    if b_collisions or a_collisions:
        print("  duplicate keys (lowest id used): %d before, %d after" % (b_collisions, a_collisions))

    if not b_index:
        print("  nothing to compare")
        return

    moves = []
    stable = 0
    gone = []
    for key, old_id in b_index.items():
        new_id = a_index.get(key)
        if new_id is None:
            gone.append((old_id, key))
        elif new_id == old_id:
            stable += 1
        else:
            moves.append((old_id, new_id, key))

    arrived = len(set(a_index) - set(b_index))

    print("")
    print("  unchanged id  : %d" % stable)
    print("  REASSIGNED    : %d   <- decisions on these detached; recoverable" % len(moves))
    print("  gone          : %d   <- absent from the later snapshot entirely" % len(gone))
    print("  new           : %d" % arrived)

    if not moves:
        print("")
        print("  No ids were reassigned. A drop in decisions is NOT churn on this evidence.")
        return

    moves.sort(key=lambda m: m[0])
    offsets = Counter(new - old for old, new, _ in moves)

    print("")
    print("  OFFSET DISTRIBUTION (new id - old id)")
    for offset, count in offsets.most_common(TOP_OFFSETS):
        group = [old for old, new, _ in moves if new - old == offset]
        count_in_run, span = contiguity(group)
        solid = "contiguous" if count_in_run == span else "%d ids across a span of %d" % (count_in_run, span)
        print("    %+d : %d title(s)  old ids %d..%d  (%s)"
              % (offset, count, min(group), max(group), solid))
    if len(offsets) > TOP_OFFSETS:
        print("    ... and %d further distinct offset(s)" % (len(offsets) - TOP_OFFSETS))

    dominant, dominant_count = offsets.most_common(1)[0]
    print("")
    if len(offsets) == 1:
        print("  VERDICT: every reassigned id moved by the same offset (%+d)." % dominant)
        print("           Consistent with rows deleted and re-inserted IN THE SAME ORDER.")
        print("           This does not say WHO deleted them - read the proxy's logs for that.")
    elif dominant_count * 2 > len(moves):
        print("  VERDICT: %d of %d moves (%.0f%%) share one offset (%+d), the rest differ."
              % (dominant_count, len(moves), 100.0 * dominant_count / len(moves), dominant))
        print("           Consistent with one bulk re-insert plus unrelated churn around it.")
    else:
        print("  VERDICT: %d distinct offsets, none dominant." % len(offsets))
        print("           NOT a single bulk re-insert - the ids moved individually.")

    # The ends, because a sample taken from one end of a sorted list can show a constant
    # offset that does not hold across the range. This is the check for that.
    print("")
    print("  ENDS OF THE RANGE (the sorted-sample trap: a constant offset among the lowest")
    print("  ids proves nothing about the highest)")
    edge = min(samples, len(moves))
    for label, rows in (("lowest ", moves[:edge]), ("highest", moves[-edge:])):
        seen = sorted({new - old for old, new, _ in rows})
        shown = ", ".join("%+d" % o for o in seen[:5])
        print("    %s %d by old id: offset(s) %s%s"
              % (label, len(rows), shown, " ..." if len(seen) > 5 else ""))

    print("")
    print("  SAMPLE RE-POINTS (old -> new, title)")
    for old_id, new_id, key in moves[:samples]:
        print("    %-9d -> %-9d %+d  %s" % (old_id, new_id, new_id - old_id, b_names.get(key, "")))

    if gone:
        print("")
        print("  SAMPLE GONE (absent from the later snapshot - NOT recoverable by re-pointing)")
        for old_id, key in sorted(gone)[:samples]:
            print("    %-9d %s" % (old_id, b_names.get(key, "")))


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--before", required=True, help="snapshot taken BEFORE the event")
    parser.add_argument("--after", required=True, help="snapshot taken AFTER it")
    parser.add_argument("--kind", choices=KINDS, help="limit to one kind (default: both)")
    parser.add_argument("--samples", type=int, default=12, help="rows per sample list (default 12)")
    args = parser.parse_args()

    before = xc.read_snapshot(args.before)
    after = xc.read_snapshot(args.after)

    print("before : %s" % args.before)
    print("after  : %s" % args.after)

    for kind in (KINDS if args.kind is None else (args.kind,)):
        describe_kind(kind, before, after, max(1, args.samples))

    print("")
    print("Read-only - nothing was written.")


if __name__ == "__main__":
    main()
