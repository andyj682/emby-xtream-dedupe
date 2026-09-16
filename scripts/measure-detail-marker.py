#!/usr/bin/env python3
"""Measure whether the "detail marker" can tell harvested titles from unharvested ones.

ADR-F006 proposes calling ``get_vod_info`` once per movie the sync wants, to trigger the
proxy's detailed refresh. The open design question is how to decide *which* titles still
need the call, without keeping our own record of what we have already asked for — that
record goes stale invisibly when relation rows are deleted and recreated.

The proposed answer keys on the absence of stored detail, which a client can see for free:
``get_vod_streams`` emits ``director``, ``cast`` and ``release_date`` from the movie row's
custom properties, and those are written by the proxy's detail refresh. Empty means never
refreshed.

**That only works if the fields discriminate, and this measures whether they do.** The
failure mode is a provider that never supplies them: every one of its titles would read as
"needs detail" forever, and the design would quietly degrade into calling the whole wanted
set on every sync — the recurring cost it exists to avoid.

Read-only. One ``get_vod_streams`` list call per selected category, which is exactly what
the sync already does every run. Never calls ``get_vod_info`` — that would trigger the very
refresh being measured and destroy the measurement.

Prints no titles, only counts and name prefixes, so its output is safe to paste.

Usage:
  python3 scripts/measure-detail-marker.py
  python3 scripts/measure-detail-marker.py --fields director,cast,release_date
"""

import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import xtream_catalogue as xc  # noqa: E402

DEFAULT_FIELDS = ["director", "cast", "release_date"]


def populated(entry, field):
    value = entry.get(field)
    if value is None:
        return False
    return str(value).strip() not in ("", "0")


def name_prefix(name):
    """The provider/section marker a listing name starts with, e.g. 'EN -'.

    Providers label their listings with a short prefix, so this groups the catalogue the way
    the data is actually sourced without needing provider information the XC API does not
    expose. Returns a bucket name, never a title.
    """
    text = str(name or "").strip()
    for sep in (" - ", "- ", "| ", " |"):
        if sep in text[:14]:
            return text.split(sep, 1)[0].strip()[:16] or "(none)"
    return "(no prefix)"


def main(argv):
    parser = argparse.ArgumentParser(
        description="Measure whether the ADR-F006 detail marker discriminates on real data.")
    parser.add_argument("--config-glob", help="override the config XML search pattern")
    parser.add_argument("--fields", default=",".join(DEFAULT_FIELDS),
                        help="comma-separated marker fields (default: %s)" % ",".join(DEFAULT_FIELDS))
    parser.add_argument("--prefixes", type=int, default=12,
                        help="how many name prefixes to break down (default 12)")
    args = parser.parse_args(argv)

    fields = [f.strip() for f in args.fields.split(",") if f.strip()]

    config_path = xc.find_config(args.config_glob)
    root = xc.load_config(config_path).getroot()
    base, user, password = xc.credentials(root)
    stores = xc.read_all_stores(root)

    categories = xc.read_store(root, "SelectedVodCategoryIds", "int-array")
    gate_on = (xc.text(root, "RequireReviewBeforeSync", "false") or "").lower() == "true"

    print("config     : %s" % config_path)
    print("categories : %s" % (len(categories) if categories else "all (none selected)"))
    print("review gate: %s" % ("ON" if gate_on else "OFF"))
    print("marker     : %s\n" % ", ".join(fields))

    # Only the marker fields and the name: a whole catalogue of full entries is a few hundred
    # MB, mostly plot text, which is enough to get a memory-capped container killed.
    entries = xc.fetch_raw(base, user, password, "movie", categories or None,
                           keep=fields + ["name"])
    excluded = set(stores["ExcludedVodStreamIds"])
    reviewed = set(stores["ReviewedVodStreamIdsJson"] or [])

    included = {i: e for i, e in entries.items() if i not in excluded}
    # The gate also exempts titles already on disk, which needs the library tree to resolve;
    # this is the lower bound on the wanted set, and the included set is the upper bound.
    wanted = {i: e for i, e in included.items() if i in reviewed} if gate_on else dict(included)

    print("catalogue  : %d movies" % len(entries))
    print("included   : %d  (catalogue minus live exclusions)" % len(included))
    print("wanted     : %d  %s\n" % (
        len(wanted), "(reviewed and included)" if gate_on else "(= included; gate is off)"))

    for label, scope in (("WANTED SET", wanted), ("INCLUDED SET", included)):
        if not scope:
            continue
        total = len(scope)
        print("=" * 74)
        print("%s — %d titles" % (label, total))

        for field in fields:
            n = sum(1 for e in scope.values() if populated(e, field))
            print("  %-16s populated on %6d  (%5.1f%%)" % (field, n, 100.0 * n / total))

        # The distribution is the real answer. A marker that works is bimodal: titles either
        # have the detail block or they do not. A flat "everything has 0 fields" means the
        # provider never supplies them and the marker cannot discriminate at all.
        buckets = {}
        for e in scope.values():
            k = sum(1 for f in fields if populated(e, f))
            buckets[k] = buckets.get(k, 0) + 1
        print("  fields present per title:")
        for k in range(len(fields) + 1):
            n = buckets.get(k, 0)
            bar = "#" * int(40.0 * n / total)
            print("    %d of %d  %6d  (%5.1f%%)  %s" % (k, len(fields), n, 100.0 * n / total, bar))

        would_call = buckets.get(0, 0)
        print("  --> would be called on the FIRST run : %6d  (%5.1f%%)"
              % (would_call, 100.0 * would_call / total))
        print("  --> would be skipped as already held : %6d  (%5.1f%%)"
              % (total - would_call, 100.0 * (total - would_call) / total))

        if would_call == total:
            print("  !! MARKER DOES NOT DISCRIMINATE — every title reads as unharvested, so the")
            print("     design would call the whole set every sync. Pick different fields.")
        elif would_call == 0:
            print("  !! MARKER IS SATURATED — no title reads as unharvested, so nothing would ever")
            print("     be called. Check whether detail has already been fetched for everything.")
        print()

    # Per-prefix breakdown: the failure this is really looking for is one provider that never
    # populates the fields, which an overall percentage would average away.
    scope = wanted or included
    if scope and args.prefixes:
        by_prefix = {}
        for e in scope.values():
            p = name_prefix(e.get("name"))
            hit = any(populated(e, f) for f in fields)
            counts = by_prefix.setdefault(p, [0, 0])
            counts[0] += 1
            counts[1] += 1 if hit else 0
        print("=" * 74)
        print("BY NAME PREFIX (top %d by size) — a provider that never populates the marker" % args.prefixes)
        print("is the failure an overall percentage would hide\n")
        print("  %-18s %8s %10s %8s" % ("prefix", "titles", "with detail", "rate"))
        ranked = sorted(by_prefix.items(), key=lambda kv: -kv[1][0])[:args.prefixes]
        for prefix, (n, hit) in ranked:
            print("  %-18s %8d %10d %7.1f%%" % (prefix[:18], n, hit, 100.0 * hit / n))
        zero = [p for p, (n, hit) in by_prefix.items() if n >= 25 and hit == 0]
        if zero:
            print("\n  !! %d prefix(es) with >=25 titles and ZERO detail — these would be called" % len(zero))
            print("     on every run under the proposed design: %s" % ", ".join(sorted(zero)[:8]))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
