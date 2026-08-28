#!/usr/bin/env python3
"""Find movie .strm files whose stream id no longer exists in the catalogue.

A title can leave the provider's catalogue and come back later under a different
StreamId. The folder and filename derive from the cleaned title plus [tmdbid=N], so they
do not change — which means the .strm on disk keeps pointing at the old, dead id and
playback fails, silently and indefinitely:

  * orphan cleanup does not touch it. The file exists and the plugin owns it; nothing
    about it looks orphaned.
  * the delta sync may skip it. The path exists, so unless the provider stamps a fresh
    ``added`` on re-addition, ``SmartSkipExisting`` short-circuits before the URL is
    rewritten.

So the breakage accumulates with no counter, no log line and no symptom until someone
presses play. This audit finds it.

Dead links are split by whether they are recoverable, using the [tmdbid=N] in the folder
name — a durable on-disk record of identity that predates any snapshot:

  REPAIRABLE  the title is back in the catalogue under a new id. A sync should rewrite
              the .strm. (If it does not, the provider kept the old ``added`` timestamp
              and the delta skip is hiding it — force with SmartSkipExisting off.)
  ORPHANED    nothing live carries that TMDB id. The content is genuinely gone and the
              folder is dead weight.

Reads a catalogue snapshot rather than calling the provider, so it costs nothing and can
be run as often as you like. Series .strm files carry *episode* ids, which snapshots do
not record, so they are counted and skipped rather than guessed at.

Usage:
  python3 scripts/audit-strm-links.py --library /path/to/xtream/Movies \\
      --snapshot /out/catalogue-ids-YYYY-MM-DD.tsv [--samples 15]
"""

import argparse
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import xtream_catalogue as xc  # noqa: E402

# .../proxy/vod/movie/<uuid>?stream_id=<id>   (Dispatcharr multi-version form)
PROXY_ID = re.compile(r"[?&]stream_id=(\d+)")
# .../movie/<user>/<pass>/<id>.<ext>          (direct XC form)
PATH_ID = re.compile(r"/(\d+)\.[A-Za-z0-9]+\s*$")
FOLDER_TMDB = re.compile(r"\[tmdbid=(\d+)\]")


def stream_id_from_url(url):
    match = PROXY_ID.search(url)
    if match:
        return int(match.group(1))
    match = PATH_ID.search(url)
    if match:
        return int(match.group(1))
    return 0


def read_url(path):
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as handle:
            for line in handle:
                line = line.strip()
                if line:
                    return line
    except OSError:
        return None
    return ""


def main(argv):
    parser = argparse.ArgumentParser(
        description="Audit movie .strm files for stream ids that no longer exist.")
    parser.add_argument("--library", required=True, help="path to the Movies STRM tree")
    parser.add_argument("--snapshot", required=True, help="a catalogue-ids-*.tsv snapshot")
    parser.add_argument("--samples", type=int, default=15)
    parser.add_argument("--list-orphans", metavar="PATH",
                        help="write the orphaned folder paths to a file (does not delete anything)")
    args = parser.parse_args(argv)

    if not os.path.isdir(args.library):
        raise SystemExit("Not a directory: %s" % args.library)

    snapshot = xc.read_snapshot(args.snapshot)
    live_ids = {item_id for (kind, item_id) in snapshot if kind == "movie"}
    live_tmdb = {tmdb for (kind, _i), (tmdb, _n) in snapshot.items() if kind == "movie" and tmdb}
    print("library  : %s" % args.library)
    print("snapshot : %s  (%d live movie ids, %d distinct TMDB ids)\n"
          % (args.snapshot, len(live_ids), len(live_tmdb)))

    total = 0
    ok, dead_repairable, dead_orphaned, dead_unknown = [], [], [], []
    empty, unparsed, series_skipped = [], [], []

    for root, _dirs, files in os.walk(args.library):
        for name in files:
            if not name.lower().endswith(".strm"):
                continue
            total += 1
            path = os.path.join(root, name)
            url = read_url(path)

            if url is None or url == "":
                empty.append(path)
                continue
            if "/series/" in url:
                series_skipped.append(path)
                continue

            stream_id = stream_id_from_url(url)
            if not stream_id:
                unparsed.append(path)
                continue
            if stream_id in live_ids:
                ok.append(path)
                continue

            # Dead. Recoverable only if something live carries the same TMDB identity,
            # which the folder name records.
            match = FOLDER_TMDB.search(os.path.basename(root)) or FOLDER_TMDB.search(name)
            if not match:
                dead_unknown.append((path, stream_id))
            elif int(match.group(1)) in live_tmdb:
                dead_repairable.append((path, stream_id, int(match.group(1))))
            else:
                dead_orphaned.append((path, stream_id, int(match.group(1))))

    def line(label, count):
        pct = 100.0 * count / total if total else 0.0
        print("  %-50s %7d  (%4.1f%%)" % (label, count, pct))

    print("scanned %d .strm file(s)\n" % total)
    line("stream id still live", len(ok))
    line("DEAD id, title back under a new id (repairable)", len(dead_repairable))
    line("DEAD id, nothing live with that TMDB id (orphaned)", len(dead_orphaned))
    line("DEAD id, no [tmdbid=] in the folder name", len(dead_unknown))
    line("empty or unreadable file", len(empty))
    line("URL not recognised", len(unparsed))
    line("series .strm (episode ids — not checkable here)", len(series_skipped))

    for label, bucket in (("repairable", dead_repairable), ("orphaned", dead_orphaned)):
        if not bucket:
            continue
        print("\n  sample — %s:" % label)
        for entry in bucket[:args.samples]:
            path, stream_id = entry[0], entry[1]
            print("      dead id=%-8d %s" % (stream_id, os.path.basename(os.path.dirname(path))[:60]))

    if empty:
        print("\n  sample — empty/unreadable:")
        for path in empty[:args.samples]:
            print("      %s" % os.path.basename(os.path.dirname(path))[:70])

    if args.list_orphans and dead_orphaned:
        folders = sorted({os.path.dirname(p) for p, _s, _t in dead_orphaned})
        with open(args.list_orphans, "w", encoding="utf-8") as handle:
            for folder in folders:
                handle.write(folder + "\n")
        print("\n%d orphaned folder path(s) written to %s (nothing deleted)"
              % (len(folders), args.list_orphans))

    print("\n" + "=" * 78)
    if dead_repairable:
        print("The repairable ones should fix themselves on the next movie sync. If they do")
        print("not, the provider reused the old 'added' timestamp and SmartSkipExisting is")
        print("short-circuiting before the URL is rewritten — run once with it off.")
    if dead_orphaned:
        print("The orphaned ones are content the provider no longer carries. Enabling")
        print("CleanupOrphans would remove them, subject to OrphanSafetyThreshold.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
