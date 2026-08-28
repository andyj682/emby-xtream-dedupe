#!/usr/bin/env python3
"""Find a cross-listed series and emit a minimal emby-test config that exercises it.

Testing the collapse-group exclusion propagation (ADR-016) needs a show that appears
under two or more SeriesIds with the same name, with only one of those ids on the
blocklist. Constructing that by hand is awkward for one reason: you need to know which
*categories* carry the show, and neither the config nor a catalogue snapshot records
that — only the per-category list calls reveal it.

So this scans a few categories, finds names carrying more than one SeriesId, and can
write a purpose-built config for the test rig. That is deliberately better than copying
the live config: a handful of shows syncs in minutes and the whole resulting folder tree
fits on one screen, so over-exclusion is obvious rather than buried in a diff of
thousands of directories.

The generated config pins the variables that would otherwise muddy the result —
single-folder mode, name cleaning off, SmartSkipExisting off (always fetch),
CleanupOrphans off (so only the exclusion path can delete anything).

Read-only against the provider: get_series_categories plus one get_series per scanned
category. No get_series_info, so Dispatcharr's gated episode refresh is never triggered.

Usage:
  python3 scripts/find-crosslisted-series.py [--categories N | --category-ids 1,2,3]
                                             [--write-config PATH] [--strm-path PATH]

On the NAS:
  docker run --rm --network container:emby --memory=256m \\
    -v /path/to/emby/config:/cfg:ro -v "$PWD/scripts":/scripts:ro -v /tmp:/out \\
    python:3-alpine python3 /scripts/find-crosslisted-series.py --categories 8 \\
      --write-config /out/emby-test-config.xml
"""

import argparse
import json
import os
import sys
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import xtream_catalogue as xc  # noqa: E402


def fetch_series_categories(base, user, password, timeout=120):
    url = base + "/player_api.php?" + urllib.parse.urlencode({
        "username": user, "password": password, "action": "get_series_categories"})
    with urllib.request.urlopen(url, timeout=timeout) as response:
        payload = json.load(response) or []
    out = []
    for entry in payload:
        try:
            out.append((int(entry.get("category_id")), str(entry.get("category_name") or "")))
        except (TypeError, ValueError):
            continue
    return out


def fetch_series_in_category(base, user, password, category_id, timeout=120):
    url = base + "/player_api.php?" + urllib.parse.urlencode({
        "username": user, "password": password, "action": "get_series",
        "category_id": category_id})
    with urllib.request.urlopen(url, timeout=timeout) as response:
        payload = json.load(response) or []
    out = []
    for entry in payload:
        try:
            series_id = int(entry.get("series_id") or 0)
        except (TypeError, ValueError):
            continue
        if series_id > 0:
            out.append((series_id, str(entry.get("name") or "")))
    return out


def find_candidates(per_category):
    """per_category: {category_id: [(series_id, name)]} -> candidates sorted by copy count.

    Matches on the RAW name, not the cleaned one. That is deliberately conservative: the
    plugin groups on the *cleaned* name, so anything matching raw will certainly group,
    whatever the live name-cleaning settings are.
    """
    by_name = {}
    for category_id, rows in per_category.items():
        for series_id, name in rows:
            key = xc.normalise_name(name)
            if not key:
                continue
            entry = by_name.setdefault(key, {"display": name, "ids": {}, "categories": set()})
            entry["ids"][series_id] = category_id
            entry["categories"].add(category_id)

    candidates = [e for e in by_name.values() if len(e["ids"]) > 1]
    # Most copies first; then prefer a candidate spanning more than one category, since
    # that is the actual Path-A shape (a second category carrying its own SeriesId) rather
    # than two ids inside one category — same code path, less faithful reproduction. Then
    # fewest categories, to keep the test sync small.
    candidates.sort(key=lambda e: (
        -len(e["ids"]),
        0 if len(e["categories"]) > 1 else 1,
        len(e["categories"]),
        e["display"]))
    return candidates


def pick_controls(per_category, exclude_ids, only_categories, wanted=6):
    """Titles with exactly one SeriesId, to prove normal syncing still works.

    Single-id titles are deliberate: they never enter a collapse group, so if one of them
    fails to sync the cause cannot be the propagation under test.

    Two constraints learned the hard way. They must come from the categories the generated
    config actually SELECTS, or they are never fetched and prove nothing. And take several:
    a provider can carry a series with no episodes at all, which writes no folder and is a
    perfectly valid outcome — one such pick and the control tells you nothing.
    """
    by_name = {}
    for cid, rows in per_category.items():
        if cid not in only_categories:
            continue
        for sid, name in rows:
            key = xc.normalise_name(name)
            if key:
                by_name.setdefault(key, {})[sid] = name
    controls = []
    for key in sorted(by_name):
        ids = by_name[key]
        if len(ids) == 1:
            sid, name = next(iter(ids.items()))
            if sid not in exclude_ids:
                controls.append((sid, name))
        if len(controls) >= wanted:
            break
    return controls


def build_test_config(source_root, candidate, strm_path, per_category=None):
    base, user, password = xc.credentials(source_root)
    root = ET.Element("PluginConfiguration")

    def add(tag, value):
        ET.SubElement(root, tag).text = str(value)

    add("BaseUrl", base)
    add("Username", user)
    add("Password", password)
    add("SyncSeries", "true")
    add("SyncMovies", "false")
    add("StrmLibraryPath", strm_path)
    add("SeriesFolderMode", "single")
    # Pinned so the test measures propagation and nothing else.
    add("EnableContentNameCleaning", "false")
    add("SmartSkipExisting", "false")
    add("CleanupOrphans", "false")
    add("EnableNfoFiles", "false")
    add("EnableSeriesIdFolderNaming", "false")
    add("EnableSeriesMetadataLookup", "false")
    add("RefreshDispatcharrEpisodes", "false")
    add("SyncParallelism", "2")
    # Carry the naming versions over from the live config. Omitting them defaults to 0,
    # which trips "STRM naming version upgraded (0 -> 1); resetting sync timestamps" and
    # puts a spurious full-resync line in the middle of the test's log.
    for tag in ("StrmNamingVersion", "EpisodeFilenameMigrationVersion"):
        value = xc.text(source_root, tag)
        if value:
            add(tag, value)

    categories = ET.SubElement(root, "SelectedSeriesCategoryIds")
    for category_id in sorted(candidate["categories"]):
        ET.SubElement(categories, "int").text = str(category_id)

    # Exactly one of the show's ids — the defect condition: some but not all.
    cand_ids = sorted(candidate["ids"])
    excluded_id = cand_ids[0]

    # Everything else in the scanned categories is excluded too, so the run processes a
    # handful of series instead of every title in those categories. Without this the test
    # sync is thousands of get_series_info calls; with it, seconds.
    controls = []
    keep = set(cand_ids[1:])
    if per_category:
        controls = pick_controls(per_category, set(cand_ids), set(candidate["categories"]))
        keep |= {sid for sid, _n in controls}
        all_ids = {sid for rows in per_category.values() for sid, _n in rows}
        to_exclude = sorted(all_ids - keep)
    else:
        to_exclude = [excluded_id]

    excluded = ET.SubElement(root, "ExcludedSeriesIds")
    for value in to_exclude:
        ET.SubElement(excluded, "int").text = str(value)

    return root, excluded_id, cand_ids, controls, len(to_exclude)


def main(argv):
    parser = argparse.ArgumentParser(
        description="Find a cross-listed series and emit a minimal test config.")
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--categories", type=int, default=6,
                       help="how many series categories to scan (default 6)")
    group.add_argument("--category-ids", help="comma-separated category ids to scan instead")
    group.add_argument("--selected", action="store_true",
                       help="scan every category in SelectedSeriesCategoryIds (one call each)")
    parser.add_argument("--partial", action="store_true",
                       help="report titles where SOME BUT NOT ALL ids are excluded — the condition "
                            "the de-dup view's heal notice reports, and what the collapse-group "
                            "propagation fixes. Implies --selected unless a scope is given.")
    parser.add_argument("--write-config", metavar="PATH",
                        help="emit a minimal emby-test config XML for the top candidate")
    parser.add_argument("--strm-path", default="/config/xtream",
                        help="StrmLibraryPath for the generated config (default /config/xtream)")
    parser.add_argument("--samples", type=int, default=10,
                        help="how many titles to list in detail (default 10)")
    parser.add_argument("--config-glob", help="override the config XML search pattern")
    args = parser.parse_args(argv)

    config_path = xc.find_config(args.config_glob)
    source_root = xc.load_config(config_path).getroot()
    base, user, password = xc.credentials(source_root)
    print("config : %s" % config_path)
    print("base   : %s\n" % base)

    selected = [int(i.text) for i in source_root.findall("SelectedSeriesCategoryIds/int") if i.text]

    if args.category_ids:
        targets = [int(x) for x in args.category_ids.split(",") if x.strip()]
        names = {}
    elif args.selected or args.partial:
        if not selected:
            raise SystemExit("SelectedSeriesCategoryIds is empty (= all categories); pass --category-ids instead")
        categories = fetch_series_categories(base, user, password)
        names = dict(categories)
        targets = selected
        print("scanning all %d selected series categories" % len(targets))
    else:
        categories = fetch_series_categories(base, user, password)
        print("provider reports %d series categories; scanning the first %d"
              % (len(categories), min(args.categories, len(categories))))
        targets = [cid for cid, _ in categories[:args.categories]]
        names = dict(categories)

    per_category = {}
    for category_id in targets:
        rows = fetch_series_in_category(base, user, password, category_id)
        per_category[category_id] = rows
        print("  category %-8d %-45s %5d series" % (
            category_id, names.get(category_id, "")[:45], len(rows)))

    candidates = find_candidates(per_category)
    total = sum(len(r) for r in per_category.values())
    distinct_ids = {sid for rows in per_category.values() for sid, _n in rows}
    distinct_names = {xc.normalise_name(n) for rows in per_category.values() for _s, n in rows if n.strip()}
    print("\n%d series rows across the scanned categories" % total)
    print("  %d DISTINCT SeriesIds" % len(distinct_ids))
    print("  %d distinct names" % len(distinct_names))
    print("  %d name(s) carrying more than one SeriesId" % len(candidates))
    print("\nCompare the distinct-SeriesId count against a catalogue-wide `action=get_series`.")
    print("If it is materially higher, the catalogue-wide call under-reports and any snapshot")
    print("built from it undercounts live series ids — which would inflate 'dead' id figures.")

    if args.partial:
        excluded = xc.read_store(source_root, "ExcludedSeriesIds", "int-array")
        print("\n%s\nPARTIAL EXCLUSIONS — some ids blocklisted, some not\n%s" % ("=" * 78, "=" * 78))
        print("ExcludedSeriesIds holds %d ids\n" % len(excluded))
        partial = []
        for entry in candidates:
            ids = entry["ids"]
            hits = [sid for sid in ids if sid in excluded]
            if hits and len(hits) != len(ids):
                partial.append((entry, hits))
        print("%d title(s) are partially excluded right now.\n" % len(partial))
        print("Each one costs a wasted get_series_info per sync today (the un-excluded sibling is")
        print("fetched and written, then RemoveExcludedContent deletes the folder again), and is")
        print("what the de-dup view asks you to save. The propagation fix catches exactly these.\n")
        for entry, hits in partial[:args.samples * 2]:
            print("  %s" % entry["display"][:66])
            for sid, cid in sorted(entry["ids"].items()):
                print("      id=%-8d cat=%-8d %-10s %s"
                      % (sid, cid, "EXCLUDED" if sid in excluded else "not excl.",
                         names.get(cid, "")[:34]))
        if len(partial) > args.samples * 2:
            print("\n  ... and %d more" % (len(partial) - args.samples * 2))
        return 0

    if not candidates:
        print("\nNo cross-listed series here. Scan more categories (--categories 20) or name")
        print("specific ones (--category-ids ...). Cross-listing is per-provider, so some")
        print("category sets genuinely have none.")
        return 1

    print("\ntop candidates:")
    for entry in candidates[:10]:
        pairs = ", ".join("%d(cat %d)" % (sid, cid) for sid, cid in sorted(entry["ids"].items()))
        print("  %-45s %d copies: %s" % (entry["display"][:45], len(entry["ids"]), pairs))

    if not args.write_config:
        print("\nRe-run with --write-config PATH to emit a ready-to-use emby-test config.")
        return 0

    top = candidates[0]
    root, excluded_id, cand_ids, controls, n_excluded = build_test_config(
        source_root, top, args.strm_path, per_category)
    ET.ElementTree(root).write(args.write_config, encoding="utf-8", xml_declaration=True)

    print("\n" + "=" * 78)
    print("wrote %s" % args.write_config)
    print("  test show      : %s" % top["display"])
    print("  its SeriesIds  : %s" % cand_ids)
    print("  EXCLUDED       : %d  (its sibling(s) %s left un-excluded on purpose)"
          % (excluded_id, cand_ids[1:]))
    print("  categories     : %s" % sorted(top["categories"]))
    print("  control shows  : %s" % ", ".join("%d %r" % (s, n[:32]) for s, n in controls))
    print("  also excluded  : %d other series, so the run stays small" % n_excluded)
    print("  StrmLibraryPath: %s" % args.strm_path)
    print("""
EXPECTED ON THE NEW BUILD (one sync, no 1.3.0 baseline needed):
  * log: 'N of those are cross-listed copies of a show already on the blocklist ...'
         with N >= 1  <-- the propagation firing. Its ABSENCE is the failure signal.
  * log: NO get_series_info call for %s or %s
  * Shows/ contains ONLY the control show folder(s) above
  * Failed = 0
Run 1.3.0 only if that log line is missing, to check the test condition existed at all —
on 1.3.0 you should instead see a get_series_info for %s.""" % (
        excluded_id, cand_ids[1:], cand_ids[1:]))
    print("""
Then, on emby-test:
  1. docker stop emby-test
  2. copy this over emby-test's plugin config, named exactly like the live one
     (/path/to/emby-test/config/plugins/configurations/<same name>.xml)
  3. deploy the 1.3.0 DLL first, docker start emby-test, run the series sync.
  4. deploy the new DLL, restart, sync again.
  5. Do not open the plugin config page at any point — the de-dup view's heal would
     mask the very behaviour being tested.

WHAT TO COMPARE — read the LOG, not the disk.
In single-folder mode the old behaviour is not "the show reappears": the un-excluded
sibling is fetched and written, and then RemoveExcludedContent deletes the folder again
(the write loop completes before that pass runs). So the show is correctly absent either
way, and an empty Shows tree proves nothing.

The signal is the wasted fetch. On 1.3.0 the log contains a get_series_info call for the
UN-excluded sibling id of '%s'; on the new build it does not, and instead reports
  'N of those are cross-listed copies of a show already on the blocklist ...'
Grep the emby-test log for 'get_series_info' and for the sibling id to see the difference.

Multiple/Custom folder mode is the case where the old behaviour is a real bug rather than
waste: the write lands in the new category's folder while the deletion targets the excluded
one's, so the show genuinely persists. This generated config uses single-folder mode.""" % top["display"])
    print("\nWith one show and %d categories this syncs in minutes, and the whole Shows tree"
          % len(top["categories"]))
    print("is small enough to read directly — anything else missing is a regression.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
