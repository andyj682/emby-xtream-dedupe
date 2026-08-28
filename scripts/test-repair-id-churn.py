#!/usr/bin/env python3
"""End-to-end test for repair-id-churn.py against a synthetic churn event.

Builds a config XML, a pre-event snapshot, and a post-event catalogue in a temp dir,
then drives the real script with only the HTTP fetch stubbed. The cases that matter are
the ones where a repair must NOT happen: a title that genuinely left the catalogue, and
a title the user has since reviewed and deliberately kept.

Run: python3 scripts/test-repair-id-churn.py
"""

import importlib.util
import json
import os
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import xtream_catalogue as xc  # noqa: E402


def load_script(name):
    path = os.path.join(HERE, name)
    spec = importlib.util.spec_from_file_location(name.replace("-", "_").replace(".py", ""), path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


# ---------------------------------------------------------------------------
# Fixture: one re-ingest event.
#
#   movie 100 "Alpha"   tmdb 501  excluded      -> renumbered to 900   EXPECT re-point
#   movie 101 "Beta"    tmdb 502  excluded      -> gone from catalogue EXPECT no re-point
#   movie 102 "Gamma"   tmdb 503  excluded      -> renumbered to 902, and the user has
#                                                  since reviewed+kept 902
#                                                  EXPECT skipped (do not undo their call)
#   movie 103 "Delta"   tmdb 504  reviewed only -> renumbered to 903   EXPECT re-point
#   movie 104 "Epsilon" no tmdb   excluded      -> renumbered to 904, same name
#                                                  EXPECT re-point via name
#   series 200 "Show A"           excluded      -> renumbered to 800 and 801 (two
#                                                  categories) EXPECT both re-pointed
#   movie 105 "Zeta"    tmdb 505  excluded      -> still live as 105   (not dead at all)
# ---------------------------------------------------------------------------

SNAPSHOT_ROWS = [
    ("movie", 100, 501, "Alpha"),
    ("movie", 101, 502, "Beta"),
    ("movie", 102, 503, "Gamma"),
    ("movie", 103, 504, "Delta"),
    ("movie", 104, 0, "Epsilon"),
    ("movie", 105, 505, "Zeta"),
    ("series", 200, 0, "Show A"),
]

# (id, tmdb, name, category) — fetch_catalogue returns the category it was first seen in.
LIVE_MOVIES = [
    (900, 501, "Alpha", 1),        # Alpha, renumbered
    (902, 503, "Gamma", 1),        # Gamma, renumbered
    (903, 504, "Delta", 1),        # Delta, renumbered
    (904, 0, "Epsilon", 1),        # Epsilon, renumbered, no tmdb
    (105, 505, "Zeta", 1),         # unchanged
    (910, 999, "Something New", 2),  # genuinely new
]

LIVE_SERIES = [
    (800, 0, "Show A", 1),         # Show A, renumbered, category 1
    (801, 0, "Show A", 2),         # Show A, renumbered, category 2
    (850, 0, "Show B", 1),         # unrelated
]

EXCLUDED_VOD = [100, 101, 102, 104, 105]
REVIEWED_VOD = [103, 902]          # 902 = the user's post-churn decision to keep Gamma
EXCLUDED_SERIES = [200]
REVIEWED_SERIES = []


def build_config(path):
    root = ET.Element("PluginConfiguration")
    ET.SubElement(root, "BaseUrl").text = "http://fake-xtream"
    ET.SubElement(root, "Username").text = "user"
    ET.SubElement(root, "Password").text = "pass"
    for element, values in (("ExcludedVodStreamIds", EXCLUDED_VOD),
                            ("ExcludedSeriesIds", EXCLUDED_SERIES)):
        node = ET.SubElement(root, element)
        for value in values:
            ET.SubElement(node, "int").text = str(value)
    ET.SubElement(root, "ReviewedVodStreamIdsJson").text = json.dumps(REVIEWED_VOD)
    ET.SubElement(root, "ReviewedSeriesIdsJson").text = json.dumps(REVIEWED_SERIES)
    ET.SubElement(root, "SmartSkipExisting").text = "true"  # an untouched field, to prove round-trip
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)


def check(label, condition):
    print("  %s %s" % ("PASS" if condition else "FAIL", label))
    return bool(condition)


def main():
    repair = load_script("repair-id-churn.py")
    ok = True

    with tempfile.TemporaryDirectory() as tmp:
        config_path = os.path.join(tmp, "Emby.Xtream.Plugin.xml")
        snapshot_path = os.path.join(tmp, "catalogue-ids-2026-08-01.tsv")
        candidate_path = os.path.join(tmp, "candidate.xml")
        additions_path = os.path.join(tmp, "additions.txt")

        build_config(config_path)
        xc.write_snapshot(snapshot_path, SNAPSHOT_ROWS)

        original_fetch = xc.fetch_catalogue
        xc.fetch_catalogue = lambda base, user, pw, kind, category_ids=None, timeout=300: (
            list(LIVE_MOVIES) if kind == "movie" else list(LIVE_SERIES))
        try:
            rc = repair.main([
                "--snapshot", snapshot_path,
                "--config-glob", config_path,
                "--write", candidate_path,
                "--additions", additions_path,
                "--prune-resolved",
            ])
        finally:
            xc.fetch_catalogue = original_fetch

        print("\n--- assertions ---")
        ok &= check("exit code 0", rc == 0)
        ok &= check("candidate XML written", os.path.exists(candidate_path))

        result = ET.parse(candidate_path).getroot()
        excluded_vod = xc.read_store(result, "ExcludedVodStreamIds", "int-array")
        reviewed_vod = xc.read_store(result, "ReviewedVodStreamIdsJson", "json-array")
        excluded_series = xc.read_store(result, "ExcludedSeriesIds", "int-array")

        ok &= check("Alpha re-pointed 100 -> 900 (tmdb)", 900 in excluded_vod)
        ok &= check("Epsilon re-pointed 104 -> 904 (name, no tmdb)", 904 in excluded_vod)
        ok &= check("Beta NOT re-pointed (title left the catalogue)",
                    not any(i in excluded_vod for i in (901, 910)))
        ok &= check("Gamma NOT re-excluded (user reviewed and kept 902)", 902 not in excluded_vod)
        ok &= check("Delta re-pointed 103 -> 903 in the REVIEWED store", 903 in reviewed_vod)
        ok &= check("Zeta untouched, still excluded (never dead)", 105 in excluded_vod)
        ok &= check("Show A re-pointed to BOTH new ids 800 and 801",
                    800 in excluded_series and 801 in excluded_series)

        ok &= check("--prune-resolved dropped re-pointed dead id 100", 100 not in excluded_vod)
        ok &= check("--prune-resolved kept unresolvable dead id 101 (a later snapshot may cover it)",
                    101 in excluded_vod)
        ok &= check("--prune-resolved kept 102 (not re-pointed, so not resolved)", 102 in excluded_vod)

        untouched = result.find("SmartSkipExisting")
        ok &= check("unrelated config field survived the round-trip",
                    untouched is not None and untouched.text == "true")

        with open(additions_path, "r", encoding="utf-8") as handle:
            additions = handle.read()
        ok &= check("additions list mentions 900", "900" in additions)

        # Refusing to overwrite the live config is the one guard whose failure is destructive.
        try:
            xc.fetch_catalogue = lambda base, user, pw, kind, category_ids=None, timeout=300: (
                list(LIVE_MOVIES) if kind == "movie" else list(LIVE_SERIES))
            repair.main(["--snapshot", snapshot_path, "--config-glob", config_path,
                         "--write", config_path])
            ok &= check("refuses to write over the live config", False)
        except SystemExit as exc:
            ok &= check("refuses to write over the live config", "Refusing" in str(exc))
        finally:
            xc.fetch_catalogue = original_fetch

        # An unparseable reviewed store must abort, never be read as empty.
        broken_path = os.path.join(tmp, "broken.xml")
        build_config(broken_path)
        broken = ET.parse(broken_path)
        broken.getroot().find("ReviewedVodStreamIdsJson").text = "[1,2,3"
        broken.write(broken_path, encoding="utf-8", xml_declaration=True)
        try:
            xc.read_store(ET.parse(broken_path).getroot(), "ReviewedVodStreamIdsJson", "json-array")
            ok &= check("aborts on an unparseable reviewed store", False)
        except SystemExit as exc:
            ok &= check("aborts on an unparseable reviewed store", "does not parse" in str(exc))

    print("\n%s" % ("ALL PASSED" if ok else "FAILURES ABOVE"))
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
