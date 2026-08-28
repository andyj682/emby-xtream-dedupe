#!/usr/bin/env python3
"""Record which provider ID was which title, today.

The plugin stores exclusions and reviewed-checkpoints as bare provider IDs. When the
provider (or Dispatcharr) re-ingests a source it reassigns those IDs in bulk, and every
reassigned ID silently detaches the decision attached to it: the title comes back
un-excluded and unreviewed. Recovering from that means knowing what a now-dead ID used
to refer to — and the config records an ID and nothing else, so that information exists
nowhere unless it was written down beforehand.

This is that writing-down. Run it periodically; ``repair-id-churn.py`` consumes the
output after an event. It only helps forward: an ID that is already dead, with no
snapshot covering it, is unrecoverable.

Read-only against the config and the provider. List calls only, no get_series_info.

Fetches PER-CATEGORY whenever the config has a category selection, because that is what
the plugin does and, for series, catalogue-wide sees only about half the SeriesIds that
exist (measured: 9,981 catalogue-wide vs 21,252 across 94 selected categories). That
costs one call per category. `--catalogue-wide` forces the old fast-but-undercounting
behaviour; do not use it for snapshots that repair-id-churn.py will consume.

Usage:
  python3 scripts/catalogue-snapshot.py [--out DIR] [--keep N] [--date LABEL] [--force]
                                        [--catalogue-wide]

``--date`` sets the filename label, so a second snapshot on the same day can be taken
without clobbering the first (``--date 2026-08-27-post``). Overwriting an existing
snapshot requires ``--force``, because a pre-event snapshot is the only record of what a
now-dead id used to be.

On the NAS, mounting the config read-only and an output directory read-write:
  docker run --rm --network container:emby --memory=512m --cpus=1 \
    -v /path/to/emby/config:/cfg:ro \
    -v "$HOME/xtream-snapshots":/out \
    -v "$PWD/scripts":/scripts:ro \
    python:3-alpine python3 /scripts/catalogue-snapshot.py --out /out
"""

import datetime
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import xtream_catalogue as xc  # noqa: E402


def parse_args(argv):
    out_dir, keep, stamp, force, catalogue_wide = "/out", 12, None, False, False
    i = 0
    while i < len(argv):
        arg = argv[i]
        if arg == "--out" and i + 1 < len(argv):
            out_dir = argv[i + 1]; i += 2
        elif arg == "--keep" and i + 1 < len(argv):
            keep = int(argv[i + 1]); i += 2
        elif arg == "--date" and i + 1 < len(argv):
            stamp = argv[i + 1]; i += 2
        elif arg == "--force":
            force = True; i += 1
        elif arg == "--catalogue-wide":
            catalogue_wide = True; i += 1
        elif arg in ("-h", "--help"):
            print(__doc__); raise SystemExit(0)
        else:
            raise SystemExit("Unrecognised argument: %s" % arg)
    return out_dir, keep, stamp, force, catalogue_wide


def prune(out_dir, keep):
    """Keep the newest N snapshots. Mirrors the rolling config-backup routine."""
    if keep <= 0:
        return []
    names = sorted(
        (n for n in os.listdir(out_dir) if n.startswith("catalogue-ids-") and n.endswith(".tsv")),
        reverse=True)
    removed = []
    for name in names[keep:]:
        try:
            os.remove(os.path.join(out_dir, name))
            removed.append(name)
        except OSError:
            pass
    return removed


def main(argv):
    out_dir, keep, stamp, force, catalogue_wide = parse_args(argv)
    if not os.path.isdir(out_dir):
        raise SystemExit("Output directory does not exist: %s (mount it read-write)" % out_dir)

    config_path = xc.find_config()
    root = xc.load_config(config_path).getroot()
    base, user, password = xc.credentials(root)
    print("config : %s" % config_path)
    print("base   : %s" % base)

    selected = {
        "movie": [int(i.text) for i in root.findall("SelectedVodCategoryIds/int") if i.text],
        "series": [int(i.text) for i in root.findall("SelectedSeriesCategoryIds/int") if i.text],
    }

    rows = []
    for kind in ("movie", "series"):
        scope = None if catalogue_wide else (selected[kind] or None)
        if scope:
            print("%-7s: fetching %d selected categories..." % (kind, len(scope)))
        fetched = xc.fetch_catalogue(base, user, password, kind, category_ids=scope)
        with_tmdb = sum(1 for r in fetched if r[1])
        pct = (100.0 * with_tmdb / len(fetched)) if fetched else 0.0
        print("%-7s: %6d distinct ids, %6d with a usable TMDB id (%.0f%%)%s"
              % (kind, len(fetched), with_tmdb, pct,
                 "" if scope else "   [catalogue-wide]"))
        rows.extend((kind, r[0], r[1], r[2], r[3]) for r in fetched)

    # Store sizes go in the log, not the file — the rolling config backups already hold
    # the authoritative copy, and the counts make a drop visible historically.
    stores = xc.read_all_stores(root)
    live_ids = {k: {r[1] for r in rows if r[0] == k} for k in ("movie", "series")}
    print()
    for element, kind, _ in xc.ID_STORES:
        stored = stores[element]
        dead = stored - live_ids[kind]
        pct = (100.0 * len(dead) / len(stored)) if stored else 0.0
        print("%-26s stored=%-7d dead=%-7d (%.1f%%)" % (element, len(stored), len(dead), pct))

    stamp = stamp or datetime.date.today().isoformat()
    path = os.path.join(out_dir, "catalogue-ids-%s.tsv" % stamp)
    # A pre-event snapshot is irreplaceable: it is the only record of what a now-dead id
    # used to be. Re-running on the same calendar day must never silently clobber it.
    if os.path.exists(path) and not force:
        raise SystemExit(
            "Refusing to overwrite the existing snapshot %s.\n"
            "If it predates a churn event it is the ONLY thing that can recover those ids.\n"
            "Use --date to write under a different name (e.g. --date %s-post), or --force."
            % (path, stamp))
    written = xc.write_snapshot(path, rows)
    print("\nwrote %d rows to %s" % (written, path))

    removed = prune(out_dir, keep)
    if removed:
        print("pruned %d old snapshot(s): %s" % (len(removed), ", ".join(removed)))

    print("\nContains titles, not credentials — but keep it local alongside the config backups.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
