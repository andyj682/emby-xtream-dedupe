#!/usr/bin/env python3
"""Re-point exclusions and reviewed-checkpoints after the provider reassigns IDs.

The plugin stores decisions as bare provider IDs. When a source is re-ingested those IDs
are reassigned in bulk, and every reassigned ID silently detaches its decision: the title
returns un-excluded and unreviewed, with nothing in any log to explain it. Recovering by
hand means re-reviewing thousands of titles.

This does it mechanically. Given a snapshot taken *before* the event (see
``catalogue-snapshot.py``), it resolves each dead ID back to the title it referred to,
finds that title's new ID in the current catalogue, and proposes adding it to the same
store. Movies resolve on TMDB ID, which survives renames; series have no TMDB ID on the
list payload, so they resolve on name — the same fidelity the de-dup view's own grouping
uses.

DRY RUN BY DEFAULT. It never touches the live config. ``--write`` emits a *candidate* XML
to a path you choose, for you to diff and install yourself:

    docker stop emby
    cp candidate.xml /path/to/emby/config/plugins/configurations/<name>.xml
    docker start emby

Emby holds the config in memory and rewrites the file on its next save, so it must be
stopped for a hand-installed config to survive.

WHAT THIS NEEDS, AND WHAT IT DOES NOT
  It reads the *live* config, not a backup. That works because dead ids are never pruned:
  the config still records which ids you decided about, and the snapshot supplies the only
  missing piece — which title each id used to be. A config backup is not required.

  A backup is not redundant, though: it answers a different failure. Renumbering leaves the
  ids in the config (this script's case). A config *wipe* removes them, and then no snapshot
  can help — only a backup can.

  ORDER MATTERS: never bulk-prune dead ids before running this. Pruning deletes exactly the
  ids this re-points, and the recovery goes with them. Repair first; `--prune-resolved`
  then prunes the ones it successfully re-pointed, in the same pass.

Usage:
  python3 scripts/repair-id-churn.py --snapshot /out/catalogue-ids-2026-08-26.tsv \\
      [--write /out/candidate.xml] [--additions /out/additions.txt] \\
      [--tmdb-only] [--prune-resolved]

On the NAS:
  docker run --rm --network container:emby --memory=512m --cpus=1 \\
    -v /path/to/emby/config:/cfg:ro \\
    -v "$HOME/xtream-snapshots":/out \\
    -v "$PWD/scripts":/scripts:ro \\
    python:3-alpine python3 /scripts/repair-id-churn.py --snapshot /out/catalogue-ids-YYYY-MM-DD.tsv
"""

import argparse
import json
import os
import sys
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import xtream_catalogue as xc  # noqa: E402


class StoreResult(object):
    def __init__(self, element, kind):
        self.element = element
        self.kind = kind
        self.stored = 0
        self.dead = 0
        self.additions = set()
        self.resolved_by_tmdb = 0
        self.resolved_by_name = 0
        self.resolved_dead_ids = set()
        self.skipped_already_present = 0
        self.skipped_user_decided = 0
        self.unresolved_no_snapshot = 0
        self.unresolved_title_gone = 0
        self.ambiguous = []
        self.samples = []


def build_live_index(rows, tmdb_only):
    """identity -> sorted list of current IDs carrying it."""
    index = {}
    for item_id, tmdb, name in ((r[0], r[1], r[2]) for r in rows):
        ident = xc.identity(tmdb, name, tmdb_only)
        if ident is None:
            continue
        index.setdefault(ident, []).append(item_id)
    for ids in index.values():
        ids.sort()
    return index


def repair_store(element, kind, stores, live_ids, live_index, snapshot, live_names, tmdb_only):
    result = StoreResult(element, kind)
    stored = stores[element]
    result.stored = len(stored)

    excluded_store = xc.EXCLUDED_STORE_FOR[kind]
    reviewed_store = xc.REVIEWED_STORE_FOR[kind]
    is_exclusion = element == excluded_store

    for dead_id in sorted(stored - live_ids[kind]):
        result.dead += 1

        record = snapshot.get((kind, dead_id))
        if record is None:
            result.unresolved_no_snapshot += 1
            continue

        old_tmdb, old_name = record
        ident = xc.identity(old_tmdb, old_name, tmdb_only)
        if ident is None:
            result.unresolved_no_snapshot += 1
            continue

        new_ids = live_index.get(ident)
        if not new_ids:
            # The title is genuinely gone from the catalogue, not renumbered. The dead ID
            # is correctly dead and there is nothing to re-point.
            result.unresolved_title_gone += 1
            continue

        if ident[0] == "tmdb":
            result.resolved_by_tmdb += 1
        else:
            result.resolved_by_name += 1

        if len(new_ids) > 1 and kind == "movie":
            # Movies share one StreamId across categories, so this should not happen.
            # Surface it rather than quietly fanning the decision out.
            result.ambiguous.append((dead_id, old_name, list(new_ids)))

        repointed = False
        for new_id in new_ids:
            if new_id in stored:
                result.skipped_already_present += 1
                repointed = True
                continue

            # The user may have made a fresh decision about the new copy since the churn.
            # A reviewed-but-not-excluded new ID means they looked at it and chose to keep
            # it, which a blind re-exclusion would silently undo.
            if is_exclusion and new_id in stores[reviewed_store] and new_id not in stores[excluded_store]:
                result.skipped_user_decided += 1
                continue

            result.additions.add(new_id)
            repointed = True
            if len(result.samples) < 12:
                result.samples.append((dead_id, new_id, ident[0], live_names[kind].get(new_id, old_name)))

        if repointed:
            result.resolved_dead_ids.add(dead_id)

    return result


def print_report(results, tmdb_only):
    print("=" * 78)
    print("REPAIR REPORT%s" % ("  (TMDB matching only, name fallback disabled)" if tmdb_only else ""))
    print("=" * 78)
    for r in results:
        print("\n%s  [%s]" % (r.element, r.kind))
        print("  stored=%d  dead=%d" % (r.stored, r.dead))
        print("  resolved:    %6d via TMDB id, %6d via name" % (r.resolved_by_tmdb, r.resolved_by_name))
        print("  PROPOSED ADDITIONS: %d new id(s)" % len(r.additions))
        print("  skipped:     %6d already in this store" % r.skipped_already_present)
        print("               %6d you have since reviewed and kept (not re-excluded)" % r.skipped_user_decided)
        print("  unresolved:  %6d not covered by the snapshot (predate it)" % r.unresolved_no_snapshot)
        print("               %6d title genuinely gone from the catalogue" % r.unresolved_title_gone)
        if r.ambiguous:
            print("  ⚠ %d movie identit(ies) mapped to several current ids — review these:" % len(r.ambiguous))
            for dead_id, name, ids in r.ambiguous[:5]:
                print("      old=%-8d %-45s -> %s" % (dead_id, name[:45], ids))
        if r.samples:
            print("  sample re-points (old -> new, matched on, current title):")
            for dead_id, new_id, how, name in r.samples:
                print("      %-8d -> %-8d  %-5s  %s" % (dead_id, new_id, how, name[:50]))


def write_candidate(tree, results, path, prune_resolved):
    root = tree.getroot()
    # Emit the prefixes .NET's XmlSerializer uses, so the round-trip stays close to what
    # Emby wrote and the diff is readable.
    ET.register_namespace("xsd", "http://www.w3.org/2001/XMLSchema")
    ET.register_namespace("xsi", "http://www.w3.org/2001/XMLSchema-instance")

    storage_for = {element: storage for element, _, storage in xc.ID_STORES}
    changed = []

    for r in results:
        if not r.additions and not (prune_resolved and r.resolved_dead_ids):
            continue
        storage = storage_for[r.element]
        element = root.find(r.element)

        if storage == "int-array":
            if element is None:
                element = ET.SubElement(root, r.element)
            existing = []
            for node in list(element.findall("int")):
                try:
                    value = int((node.text or "").strip())
                except ValueError:
                    continue
                if prune_resolved and value in r.resolved_dead_ids:
                    element.remove(node)
                    continue
                existing.append(value)
            for value in sorted(r.additions):
                ET.SubElement(element, "int").text = str(value)
            changed.append((r.element, len(existing), len(r.additions)))
        else:
            if element is None:
                element = ET.SubElement(root, r.element)
            current = xc.read_store(root, r.element, storage)
            if prune_resolved:
                current -= r.resolved_dead_ids
            element.text = json.dumps(sorted(current | r.additions))
            changed.append((r.element, len(current), len(r.additions)))

    tree.write(path, encoding="utf-8", xml_declaration=True)
    return changed


def write_additions(results, path):
    with open(path, "w", encoding="utf-8") as handle:
        for r in results:
            handle.write("# %s (%s) — %d additions\n" % (r.element, r.kind, len(r.additions)))
            for value in sorted(r.additions):
                handle.write("%d\n" % value)
            handle.write("\n")


def main(argv):
    parser = argparse.ArgumentParser(
        description="Re-point plugin exclusions/reviewed sets after provider ID churn.")
    parser.add_argument("--snapshot", required=True,
                        help="catalogue-ids-*.tsv taken BEFORE the churn event")
    parser.add_argument("--write", metavar="PATH",
                        help="emit a candidate config XML here (never modifies the live config)")
    parser.add_argument("--additions", metavar="PATH",
                        help="also emit the proposed ids as a plain list, for hand-editing")
    parser.add_argument("--tmdb-only", action="store_true",
                        help="do not fall back to name matching (skips all series)")
    parser.add_argument("--prune-resolved", action="store_true",
                        help="in the candidate XML, drop dead ids that were successfully re-pointed")
    parser.add_argument("--config-glob", help="override the config XML search pattern")
    args = parser.parse_args(argv)

    config_path = xc.find_config(args.config_glob)
    tree = xc.load_config(config_path)
    root = tree.getroot()
    base, user, password = xc.credentials(root)
    stores = xc.read_all_stores(root)

    print("config   : %s" % config_path)
    print("snapshot : %s" % args.snapshot)
    print("base     : %s\n" % base)

    snapshot = xc.read_snapshot(args.snapshot)

    selected = {
        "movie": [int(i.text) for i in root.findall("SelectedVodCategoryIds/int") if i.text],
        "series": [int(i.text) for i in root.findall("SelectedSeriesCategoryIds/int") if i.text],
    }

    live_rows, live_ids, live_index, live_names = {}, {}, {}, {}
    for kind in ("movie", "series"):
        # Per-category, matching the snapshot writer. Catalogue-wide undercounts series ids
        # badly (see xtream_catalogue.fetch_catalogue), and undercounting "live" here would
        # make this script call live ids dead and propose re-points for them.
        rows = xc.fetch_catalogue(base, user, password, kind,
                                  category_ids=selected[kind] or None)
        live_rows[kind] = rows
        live_ids[kind] = {r[0] for r in rows}
        live_index[kind] = build_live_index(rows, args.tmdb_only)
        live_names[kind] = {r[0]: r[2] for r in rows}
        snap_count = sum(1 for k, _ in snapshot if k == kind)
        print("%-7s: %6d in catalogue now, %6d in snapshot" % (kind, len(rows), snap_count))

    results = [
        repair_store(element, kind, stores, live_ids, live_index[kind], snapshot, live_names, args.tmdb_only)
        for element, kind, _ in xc.ID_STORES
    ]

    print_report(results, args.tmdb_only)

    total = sum(len(r.additions) for r in results)
    resolved = sum(len(r.resolved_dead_ids) for r in results)
    prunable = resolved if args.prune_resolved else 0

    print("\n" + "=" * 78)
    if total == 0 and prunable == 0:
        if resolved:
            # Every dead id resolves to an id that is ALREADY stored, which means the repair
            # has been applied and this is a second pass. Reporting "nothing to repair" here
            # was wrong twice over: there is something left to do, and the exit that followed
            # it made --prune-resolved --write silently produce no candidate at all.
            print("Nothing to add — all %d resolved dead id(s) are already stored, so the repair "
                  "has already been applied." % resolved)
            print("Re-run with --prune-resolved to drop those superseded ids.")
        else:
            print("Nothing to repair — no dead id resolved to a title that is back under a new id.")
        return 0

    if total:
        print("%d proposed addition(s) across %d store(s)."
              % (total, sum(1 for r in results if r.additions)))
    if prunable:
        print("%d superseded dead id(s) will be pruned from the candidate." % prunable)

    if args.additions:
        write_additions(results, args.additions)
        print("Plain id lists written to %s" % args.additions)

    if not args.write:
        print("\nDry run — nothing written. Re-run with --write PATH to emit a candidate XML.")
        return 0

    if os.path.abspath(args.write) == os.path.abspath(config_path):
        raise SystemExit("Refusing to write over the live config. Choose a different --write path.")

    changed = write_candidate(tree, results, args.write, args.prune_resolved)
    print("\nCandidate written to %s" % args.write)
    for element, kept, added in changed:
        print("  %-26s %d kept + %d added" % (element, kept, added))
    print("\nBefore installing it:")
    print("  1. diff it against %s and satisfy yourself the only changes are id lists." % config_path)
    print("  2. docker stop emby   (Emby rewrites the file from memory otherwise)")
    print("  3. copy the candidate over the original filename")
    print("  4. docker start emby, then reload the config page with the cache disabled")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
