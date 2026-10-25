#!/usr/bin/env python3
"""End-to-end test for migrate-series-ids.py against a synthetic 0.32 -> 0.33 changeover.

The fixture is built so OLD relation ids and NEW series ids overlap, which is the property
that makes an untranslated config dangerous rather than merely stale. Every show below is
there to pin one behaviour, and the report's risk figures are asserted exactly: a script
whose job is to say "this many shows would lose their folder" is worthless if that number
is not the right one.

Run: python3 scripts/test-migrate-series-ids.py
"""

import contextlib
import importlib.util
import io
import json
import os
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)


def load_script(name):
    path = os.path.join(HERE, name)
    spec = importlib.util.spec_from_file_location(name.replace("-", "_").replace(".py", ""), path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


m = load_script("migrate-series-ids.py")

# ---------------------------------------------------------------------------
# relation id -> (series id, category, active)        selected categories: 1, 2
#
#   show K  kept      relations 10 (hash, reviewed), 11 (reviewed)  -> series 3
#                     3 is also an OLD excluded relation id (show X)  EXPECT deletion risk
#   show X  excluded  relations 3, 4                                  -> series 7
#                     7 is not an old excluded id                     EXPECT "returning"
#   show U  unreviewed relation 12                                    -> series 5
#                     5 is an OLD reviewed relation id (show R)       EXPECT leak risk
#   show R  reviewed  relation 5                                      -> series 9
#   show C  conflict  relations 20 (excluded), 21 (reviewed)          -> series 30
#                                                                     EXPECT kept + reported
#   show H  two stored lists: relations 50, 51 (different hashes)     -> series 60
#   show Y  excluded  relation 23                                     -> series 11
#                     11 is an OLD reviewed relation id (show K)      EXPECT returns AND leaks
#   show Q  in an unselected category: relation 41 (cat 9)            -> series 41  not visible
#   show I  inactive provider: relation 42 (cat 1, inactive)          -> series 42  not visible
#   dead    excluded 99, reviewed 98, hash 97: not in the mapping     EXPECT dropped
# ---------------------------------------------------------------------------

MAP_ROWS = [
    (10, 3, 1, 1), (11, 3, 2, 1),
    (3, 7, 1, 1), (4, 7, 2, 1),
    (12, 5, 1, 1),
    (5, 9, 1, 1),
    (20, 30, 1, 1), (21, 30, 2, 1),
    (50, 60, 1, 1), (51, 60, 1, 1),
    (41, 41, 9, 1),
    (42, 42, 1, 0),
    (23, 11, 1, 1),
]

OLD_EXCLUDED = [3, 4, 20, 23, 99]
OLD_REVIEWED = [10, 11, 5, 21, 98]
OLD_HASHES = {"10": "hash-k", "50": "hash-h1", "51": "hash-h2", "97": "hash-dead"}

CONFIG = """<?xml version="1.0" encoding="utf-8"?>
<PluginConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <BaseUrl>http://example.invalid</BaseUrl>
  <RequireReviewBeforeSync>true</RequireReviewBeforeSync>
  <ExcludedVodStreamIds><int>111</int><int>222</int></ExcludedVodStreamIds>
  <ReviewedVodStreamIdsJson>[111,333]</ReviewedVodStreamIdsJson>
  <SelectedSeriesCategoryIds><int>1</int><int>2</int></SelectedSeriesCategoryIds>
  <ExcludedSeriesIds>%s</ExcludedSeriesIds>
  <ReviewedSeriesIdsJson>%s</ReviewedSeriesIdsJson>
  <SeriesEpisodeHashesJson>%s</SeriesEpisodeHashesJson>
  <LastSeriesSyncTimestamp>1700000000</LastSeriesSyncTimestamp>
</PluginConfiguration>
"""


def write_fixture(tmp, reviewed_json=None, rows=MAP_ROWS, as_json=False):
    cfg = os.path.join(tmp, "Emby.Xtream.Plugin.xml")
    with open(cfg, "w", encoding="utf-8") as handle:
        handle.write(CONFIG % (
            "".join("<int>%d</int>" % i for i in OLD_EXCLUDED),
            reviewed_json if reviewed_json is not None else json.dumps(OLD_REVIEWED),
            json.dumps(OLD_HASHES).replace("&", "&amp;")))
    mp = os.path.join(tmp, "series-id-map." + ("json" if as_json else "csv"))
    with open(mp, "w", encoding="utf-8") as handle:
        if as_json:
            json.dump({str(r[0]): r[1] for r in rows}, handle)
        else:
            handle.write("relation_id,series_id,category_id,active\n")
            for r in rows:
                handle.write("%d,%d,%d,%d\n" % r)
    return cfg, mp


def run(args):
    out = io.StringIO()
    code = None
    with contextlib.redirect_stdout(out):
        try:
            code = m.main(args)
        except SystemExit as exc:
            code = exc.code
    return code, out.getvalue()


def stores(path):
    root = ET.parse(path).getroot()
    excluded = sorted(int(i.text) for i in root.findall("ExcludedSeriesIds/int"))
    reviewed = sorted(json.loads(root.find("ReviewedSeriesIdsJson").text))
    hashes = json.loads(root.find("SeriesEpisodeHashesJson").text)
    return root, excluded, reviewed, hashes


FAILURES = []


def check(label, condition, detail=""):
    print(("  ok   " if condition else "  FAIL ") + label + (("  -- " + detail) if detail and not condition else ""))
    if not condition:
        FAILURES.append(label)


def line_value(output, prefix):
    for line in output.splitlines():
        if line.strip().startswith(prefix):
            return line.split(":", 1)[1].split()[0]
    return None


def test_report_mode():
    print("report mode")
    with tempfile.TemporaryDirectory() as tmp:
        cfg, mp = write_fixture(tmp)
        before = open(cfg, "rb").read()
        code, out = run(["--map", mp, "--config-glob", cfg])
        check("exits 0", code == 0, out)
        check("config untouched", open(cfg, "rb").read() == before)
        check("no ledger written", not os.path.exists(os.path.join(tmp, m.LEDGER_NAME)))
        check("visible counts only selected, active entries (3,5,7,9,11,30,60)",
              "visible : 7 shows" in out, out)
        check("deletion risk is exactly show K (new id 3)",
              line_value(out, "delete folders of shows you keep") == "1" and "new ids: 3\n" in out, out)
        check("leak risk is U (should be held) and Y (excluded, its number is reviewed): 5, 11",
              line_value(out, "let unreviewed shows through") == "2" and "new ids: 5, 11\n" in out, out)
        check("two excluded shows would return (X and Y)",
              line_value(out, "bring back shows you excluded") == "2", out)
        check("one conflict reported (show C)", line_value(out, "conflicts resolved toward keeping") == "1", out)
        check("one hash disagreement reported", "two different stored episode lists: 1" in out, out)


def test_write_mode():
    print("write mode")
    with tempfile.TemporaryDirectory() as tmp:
        cfg, mp = write_fixture(tmp)
        dst = os.path.join(tmp, "translated.xml")
        code, out = run(["--map", mp, "--config-glob", cfg, "--write", dst])
        check("exits 0", code == 0, out)
        root, excluded, reviewed, hashes = stores(dst)
        check("excluded is X and Y; conflict show C NOT excluded", excluded == [7, 11], str(excluded))
        check("reviewed carries K, R and the conflict show", reviewed == [3, 9, 30], str(reviewed))
        check("stored lists move to new ids; first of H's two kept",
              hashes == {"3": "hash-k", "60": "hash-h1"}, str(hashes))
        check("dead ids dropped and counted",
              "ExcludedSeriesIds              5         2        1" in out
              and "ReviewedSeriesIdsJson          5         3        1" in out
              and "SeriesEpisodeHashesJson        4         2        1" in out, out)
        check("movie stores untouched",
              [i.text for i in root.findall("ExcludedVodStreamIds/int")] == ["111", "222"]
              and root.find("ReviewedVodStreamIdsJson").text == "[111,333]")
        check("other settings untouched",
              root.find("LastSeriesSyncTimestamp").text == "1700000000"
              and root.find("BaseUrl").text == "http://example.invalid")
        check("ledger records the output", os.path.exists(os.path.join(tmp, m.LEDGER_NAME)))

        code, out = run(["--map", mp, "--config-glob", dst])
        check("refuses to translate its own output", code not in (0, None) and "already translated" in str(code), str(code))

        code, out = run(["--map", mp, "--config-glob", cfg, "--write", cfg])
        check("refuses to write over the input", code not in (0, None) and "Refusing" in str(code), str(code))


def test_json_mapping():
    print("json mapping")
    with tempfile.TemporaryDirectory() as tmp:
        cfg, mp = write_fixture(tmp, as_json=True)
        dst = os.path.join(tmp, "translated.xml")
        code, out = run(["--map", mp, "--config-glob", cfg, "--write", dst])
        check("exits 0", code == 0, out)
        _, excluded, reviewed, hashes = stores(dst)
        check("same translation as the CSV", excluded == [7, 11] and reviewed == [3, 9, 30]
              and hashes == {"3": "hash-k", "60": "hash-h1"})
        check("says the risk figures are an upper bound", "UPPER BOUND" in out, out)
        check("unscoped visibility includes the unselected and inactive shows",
              "visible : 9 shows" in out, out)


def test_compare_maps():
    print("compare maps")
    with tempfile.TemporaryDirectory() as tmp:
        cfg, mp = write_fixture(tmp)
        same = os.path.join(tmp, "same.csv")
        with open(same, "w", encoding="utf-8") as handle:
            handle.write(open(mp, encoding="utf-8").read())
        code, out = run(["--map", mp, "--compare-map", same, "--config-glob", cfg])
        check("identical maps: 0 moved", line_value(out, "in both, different show") == "0", out)

        moved = os.path.join(tmp, "moved.csv")
        rows = [(r[0], 999 if r[0] == 10 else r[1], r[2], r[3]) for r in MAP_ROWS]
        with open(moved, "w", encoding="utf-8") as handle:
            handle.write("relation_id,series_id,category_id,active\n")
            for r in rows:
                handle.write("%d,%d,%d,%d\n" % r)
        dst = os.path.join(tmp, "translated.xml")
        code, out = run(["--map", moved, "--compare-map", mp, "--config-glob", cfg, "--write", dst])
        check("a moved relation is reported", line_value(out, "in both, different show") == "1", out)
        check("and blocks writing", code not in (0, None) and not os.path.exists(dst), str(code))


def test_unreadable_store():
    print("unreadable store")
    with tempfile.TemporaryDirectory() as tmp:
        cfg, mp = write_fixture(tmp, reviewed_json="[1,2,")
        code, out = run(["--map", mp, "--config-glob", cfg, "--write", os.path.join(tmp, "t.xml")])
        check("refuses rather than treating it as empty",
              code not in (0, None) and "does not parse" in str(code), str(code))
        check("writes nothing", not os.path.exists(os.path.join(tmp, "t.xml")))


def test_risk_out():
    print("risk-out files")
    with tempfile.TemporaryDirectory() as tmp:
        cfg, mp = write_fixture(tmp)
        names = os.path.join(tmp, "names.csv")
        with open(names, "w", encoding="utf-8") as handle:
            handle.write("62 objects imported automatically\nseries_id,name\n3,\"Show K, the kept one\"\n5,Show U\n")
        out = os.path.join(tmp, "at-risk.tsv")
        code, text = run(["--map", mp, "--config-glob", cfg, "--risk-out", out, "--names", names])
        check("exits 0", code == 0, text)
        with open(out, encoding="utf-8") as handle:
            rows = [line.rstrip("\n").split("\t") for line in handle][1:]
        check("one row per (risk, show): delete 3; leak 5, 11; return 7, 11",
              [(r[0], r[1]) for r in rows] == [("delete", "3"), ("leak", "5"), ("leak", "11"),
                                               ("return", "7"), ("return", "11")], str(rows))
        by = {(r[0], r[1]): r for r in rows}
        check("names joined, commas in a name survive",
              by[("delete", "3")][2] == "Show K, the kept one" and by[("leak", "5")][2] == "Show U"
              and by[("leak", "11")][2] == "", str(rows))
        check("categories are the selected ones the show is visible in",
              by[("delete", "3")][3] == "1,2" and by[("leak", "11")][3] == "1", str(rows))
        check("old ids listed", by[("return", "7")][4] == "3,4", str(rows))
        with open(out + ".by-category.tsv", encoding="utf-8") as handle:
            summary = [line.rstrip("\n").split("\t") for line in handle][1:]
        # cat 2: shows 3, 7, 30 -> kept 3, 30; delete 3; return 7; all three also in cat 1
        # cat 1: shows 3, 5, 7, 9, 11, 30, 60 -> kept 3, 9, 30, 60; delete 3; leak 5, 11;
        #        return 7, 11; cross-listed 3, 7, 30 (shared with cat 2)
        check("summary counts, most deletions then smallest first",
              summary == [["2", "3", "2", "1", "0", "1", "3", "1", "3"],
                          ["1", "7", "4", "1", "2", "2", "3", "2", "3"]], str(summary))


def test_django_banner():
    print("django shell banner before the header")
    with tempfile.TemporaryDirectory() as tmp:
        cfg, mp = write_fixture(tmp)
        body = open(mp, encoding="utf-8").read()
        with open(mp, "w", encoding="utf-8") as handle:
            handle.write("62 objects imported automatically (use -v 2 for details).\n\n" + body)
        code, out = run(["--map", mp, "--config-glob", cfg])
        check("reads past the banner", code == 0 and "visible : 7 shows" in out, str(code) + out)


def test_conflicting_map():
    print("malformed mapping")
    with tempfile.TemporaryDirectory() as tmp:
        cfg, mp = write_fixture(tmp, rows=MAP_ROWS + [(10, 4, 1, 1)])
        code, out = run(["--map", mp, "--config-glob", cfg])
        check("a relation mapped to two shows is rejected",
              code not in (0, None) and "both" in str(code), str(code))


if __name__ == "__main__":
    test_report_mode()
    test_write_mode()
    test_json_mapping()
    test_compare_maps()
    test_unreadable_store()
    test_risk_out()
    test_django_banner()
    test_conflicting_map()
    print("\n%s" % ("ALL PASSED" if not FAILURES else "%d FAILED: %s" % (len(FAILURES), FAILURES)))
    sys.exit(1 if FAILURES else 0)
