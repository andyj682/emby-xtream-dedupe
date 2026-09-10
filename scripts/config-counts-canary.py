#!/usr/bin/env python3
"""Count the four decision stores in an Emby Xtream plugin config XML.

A backup only helps if you notice in time. This prints the four counts so a sudden drop
is visible, and appends a one-line record so the history is visible too.

It mirrors StrmSyncService.DeserializeIdSet's contract exactly, because the difference
matters:

  absent / empty   -> 0            (legitimate: the plugin reads this as an empty set)
  valid JSON array -> the count
  anything else    -> UNPARSEABLE  (the plugin reads this as NULL, not as empty)

Reporting a broken field as 0 would be the whole bug: "empty" and "unreadable" look
identical in a number, and they are opposite conditions. UNPARSEABLE is the alarm.

Works on the live config, the emby-test rig's config, or a repair candidate — it is just
a path. Read-only.

Usage:
  python3 config-counts-canary.py /cfg/plugins/configurations/Emby.Xtream.Plugin.xml
  python3 config-counts-canary.py <path> --log /out/xtream-config-counts.log
"""

import argparse
import datetime
import glob
import json
import os
import sys
import xml.etree.ElementTree as ET

INT_STORES = ["ExcludedVodStreamIds", "ExcludedSeriesIds"]
JSON_STORES = ["ReviewedVodStreamIdsJson", "ReviewedSeriesIdsJson"]


def count_int_store(root, name):
    """int[] stores serialize as <Name><int>1</int>...</Name>. Absent element == empty."""
    element = root.find(name)
    if element is None:
        return 0, "absent"
    return len(element.findall("int")), "ok"


def count_json_store(root, name):
    """Mirror DeserializeIdSet: empty -> 0, array -> len, anything else -> UNPARSEABLE."""
    element = root.find(name)
    if element is None:
        return 0, "absent"
    raw = element.text
    if raw is None or not raw.strip():
        return 0, "empty"
    try:
        parsed = json.loads(raw)
    except (ValueError, TypeError):
        return None, "UNPARSEABLE"
    if isinstance(parsed, list):
        return len(parsed), "ok"
    if isinstance(parsed, dict):
        # The plugin deserializes List<long>; an object would come back null.
        return None, "UNPARSEABLE (object, expected array)"
    return None, "UNPARSEABLE (%s)" % type(parsed).__name__


def main(argv):
    parser = argparse.ArgumentParser(description="Count the four Xtream plugin decision stores.")
    parser.add_argument("config", nargs="?", help="path to the plugin config XML (or a glob)")
    parser.add_argument("--log", metavar="PATH", help="append a one-line record here")
    args = parser.parse_args(argv)

    pattern = args.config or "/cfg/plugins/configurations/*Xtream*.xml"
    matches = sorted(glob.glob(pattern)) if any(c in pattern for c in "*?[") else [pattern]
    if not matches or not os.path.exists(matches[0]):
        print("no config found at: %s" % pattern, file=sys.stderr)
        return 2
    path = matches[0]

    root = ET.parse(path).getroot()

    results, alarm = [], False
    for name in INT_STORES + JSON_STORES:
        if name in INT_STORES:
            count, status = count_int_store(root, name)
        else:
            count, status = count_json_store(root, name)
        if count is None:
            alarm = True
        results.append((name, count, status))

    print("config: %s\n" % path)
    for name, count, status in results:
        shown = "UNPARSEABLE" if count is None else str(count)
        note = "" if status == "ok" else "   <- %s" % status
        print("  %-26s %10s%s" % (name, shown, note))

    # Full field names, local time, single-spaced — this format is load-bearing. An existing
    # history file may already hold years of these lines, and the whole value of the log is the
    # trend, so changing the labels would split it into two series that cannot be compared at
    # exactly the moment someone needs to look back. Deliberately no path: the scheduled run is
    # always the live config, and a varying trailing field makes the log harder to scan.
    stamp = datetime.datetime.now().strftime("%Y-%m-%d %H:%M")
    line = "%s %s" % (
        stamp,
        " ".join("%s=%s" % (n, "UNPARSEABLE" if c is None else c) for n, c, _ in results),
    )
    print("\n%s" % line)

    if args.log:
        with open(args.log, "a", encoding="utf-8") as handle:
            handle.write(line + "\n")
        print("appended to %s" % args.log)

    if alarm:
        print("\n*** A STORE IS UNPARSEABLE — the plugin reads that as null, not empty. ***")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
