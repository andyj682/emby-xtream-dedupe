#!/usr/bin/env python3
"""Translate the plugin's series ids for Dispatcharr 0.33, where every series id changes.

Up to 0.32, Dispatcharr's XC `get_series` listed each show once per provider entry and
category (an `M3USeriesRelation`), and `series_id` was that ENTRY's id. From 0.33 it is the
SHOW's id (`Series.id`). Both are ordinary database sequences, so the two number ranges
overlap: after the upgrade an id the plugin stored can belong to a completely unrelated show.

The plugin keys three stores on that id, and an untranslated config is not merely stale:
  - ExcludedSeriesIds: a show you keep whose NEW id equals an OLD excluded id is excluded,
    is not written, and the excluded-content pass deletes its folder. That pass ignores the
    orphan safety threshold by design (an exclusion is a deliberate choice), so nothing
    stops it.
  - ReviewedSeriesIdsJson and SeriesEpisodeHashesJson: an unreviewed show whose new id
    collides with an old reviewed id, or with an old stored episode list, gets past the
    review gate and is written.

Dispatcharr's database holds the exact translation: each old id is an `M3USeriesRelation`
row and its `series_id` column is the new id. 0.33's only VOD migration adds a language
column and changes uniqueness rules, so the table reads the same before and after the
upgrade and can be exported in advance.

REPORT BY DEFAULT. Without --write it changes nothing and reports what an UNTRANSLATED
first sync on 0.33 would do to this config, then what the translation would produce.

Export the mapping (read-only; numbers only, no titles, no credentials):

  docker exec dispatcharr python manage.py shell -v 0 -c "from apps.vod.models import M3USeriesRelation as R; print('relation_id,series_id,category_id,active'); [print(f'{a},{b},{c or 0},{int(bool(d))}') for a,b,c,d in R.objects.values_list('id','series_id','category_id','m3u_account__is_active')]" > series-id-map.csv

A plain JSON object of old id -> new id is accepted too. Without the category and active
columns, the risk counts cover every show Dispatcharr knows rather than only the ones in
your selected categories, so they are an upper bound; the translation itself is identical.

Usage:
  python3 scripts/migrate-series-ids.py --map series-id-map.csv [--config-glob PATTERN]
      [--compare-map earlier-map.csv] [--write translated.xml]

On the NAS (no network needed; reads two files):
  docker run --rm -v /etc/localtime:/etc/localtime:ro \\
    -v /path/to/emby/config:/cfg:ro -v "$PWD/scripts":/scripts:ro -v "$PWD":/work \\
    python:3-alpine python3 /scripts/migrate-series-ids.py --map /work/series-id-map.csv \\
      --config-glob "/cfg/plugins/configurations/*Xtream*.xml" [--write /work/translated.xml]

RUN IT ONCE, ON THE PRE-UPGRADE CONFIG. Translating an already-translated config treats new
ids as old ones and scrambles every store. The script keeps a ledger of what it has written
(beside the --map file) and refuses a config it produced, but Emby rewrites the file on
its next save, after which the ledger cannot recognise it. Translate the backup you took
with Emby stopped, not whatever is live later.

Install, as for any hand-edited config:
  docker stop emby
  cp translated.xml /path/to/emby/config/plugins/configurations/<the same filename>.xml
  docker start emby
"""

import argparse
import csv
import hashlib
import json
import os
import sys
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import xtream_catalogue as xc  # noqa: E402

LEDGER_NAME = "series-id-migration-ledger.txt"
SAMPLE = 10


class Mapping(object):
    """old relation id -> new series id, plus what is known about where each entry lives."""

    def __init__(self):
        self.new_for = {}           # relation id -> series id
        self.category_for = {}      # relation id -> category id (only when exported)
        self.active_for = {}        # relation id -> bool (only when exported)
        self.has_placement = False  # category + active columns were present

    def visible_series(self, selected_categories):
        """Series ids a sync can see: an active provider entry in a selected category.

        Empty selection means ALL categories, matching the plugin. Without placement data
        every series counts, which makes the risk figures an upper bound.
        """
        if not self.has_placement:
            return set(self.new_for.values()), False
        selected = set(selected_categories)
        out = set()
        for rel, series in self.new_for.items():
            if not self.active_for.get(rel, False):
                continue
            if selected and self.category_for.get(rel) not in selected:
                continue
            out.add(series)
        return out, True


def load_mapping(path):
    mapping = Mapping()
    with open(path, encoding="utf-8-sig") as handle:
        head = handle.read(1)
        handle.seek(0)
        if head in "{[":
            data = json.load(handle)
            pairs = data.items() if isinstance(data, dict) else data
            for old, new in pairs:
                _add(mapping, int(old), int(new), path)
            return mapping

        rows = list(csv.reader(handle))
    # Skip anything printed before the data. Django 5.2's `manage.py shell` announces its
    # auto-imports on stdout ("62 objects imported automatically …"), which lands in the
    # redirected file ahead of the header; -v 0 silences it, but a file exported without
    # it should still read.
    for start, row in enumerate(rows):
        cells = [c.strip().lower() for c in row]
        if "relation_id" in cells or (len(cells) >= 2 and all(c.isdigit() for c in cells[:2])):
            rows = rows[start:]
            break
    else:
        rows = []
    if not rows:
        raise SystemExit("%s has no relation_id,series_id data" % path)
    header = [c.strip().lower() for c in rows[0]]
    if header and not header[0].lstrip("-").isdigit():
        rows = rows[1:]
        columns = header
    else:
        columns = ["relation_id", "series_id", "category_id", "active"][:len(rows[0])]
    try:
        i_old, i_new = columns.index("relation_id"), columns.index("series_id")
    except ValueError:
        raise SystemExit("%s needs relation_id and series_id columns (has: %s)" % (path, ", ".join(columns)))
    i_cat = columns.index("category_id") if "category_id" in columns else None
    i_act = columns.index("active") if "active" in columns else None
    mapping.has_placement = i_cat is not None and i_act is not None
    for n, row in enumerate(rows, 2):
        if not row or not "".join(row).strip():
            continue
        try:
            old, new = int(row[i_old]), int(row[i_new])
        except (ValueError, IndexError):
            raise SystemExit("%s line %d does not parse: %r" % (path, n, row))
        _add(mapping, old, new, path)
        if mapping.has_placement:
            mapping.category_for[old] = int(row[i_cat] or 0)
            mapping.active_for[old] = row[i_act].strip() in ("1", "true", "True")
    return mapping


def _add(mapping, old, new, path):
    previous = mapping.new_for.get(old)
    if previous is not None and previous != new:
        # A relation id is a primary key; two answers means the file is not what it claims.
        raise SystemExit("%s maps relation %d to both %d and %d" % (path, old, previous, new))
    mapping.new_for[old] = new


def read_hashes(root):
    raw = xc.text(root, "SeriesEpisodeHashesJson").strip()
    if not raw:
        return {}
    try:
        parsed = json.loads(raw)
    except ValueError as exc:
        raise SystemExit("SeriesEpisodeHashesJson does not parse (%s). Refusing to continue." % exc)
    if not isinstance(parsed, dict):
        raise SystemExit("SeriesEpisodeHashesJson is not a JSON object")
    out = {}
    for key, value in parsed.items():
        try:
            out[int(key)] = value
        except ValueError:
            continue
    return out


def selected_categories(root):
    return [int(i.text) for i in root.findall("SelectedSeriesCategoryIds/int") if i.text]


class Translation(object):
    def __init__(self):
        self.excluded = set()
        self.reviewed = set()
        self.hashes = {}
        self.dropped = {"excluded": 0, "reviewed": 0, "hashes": 0}
        self.conflicts = []         # (new id, excluded old ids, kept old ids)
        self.hash_disagreements = 0


def translate(mapping, old_excluded, old_reviewed, old_hashes):
    """Each store is rebuilt from nothing: an old id either maps or is dropped, never kept.

    An old id left in a store untranslated is precisely the collision this exists to
    remove, so there is no "keep it in case" path.
    """
    t = Translation()
    sources = {}  # new id -> list of old ids that map to it, across all three stores

    for old in old_excluded:
        new = mapping.new_for.get(old)
        if new is None:
            t.dropped["excluded"] += 1
            continue
        t.excluded.add(new)
        sources.setdefault(new, set()).add(old)

    for old in old_reviewed:
        new = mapping.new_for.get(old)
        if new is None:
            t.dropped["reviewed"] += 1
            continue
        t.reviewed.add(new)
        sources.setdefault(new, set()).add(old)

    for old in sorted(old_hashes):
        new = mapping.new_for.get(old)
        if new is None:
            t.dropped["hashes"] += 1
            continue
        if new in t.hashes and t.hashes[new] != old_hashes[old]:
            # Only a collapse representative carries a stored list, so two entries of one
            # show both holding one is unusual. Either is safe: the list only decides
            # whether episodes get rewritten, and a mismatch costs one rewrite.
            t.hash_disagreements += 1
            continue
        t.hashes[new] = old_hashes[old]
        sources.setdefault(new, set()).add(old)

    # A show is excluded when ANY of its old entries was — the same rule as the plugin's
    # collapse-group propagation (ADR-F001), which should already have made every entry
    # of a show agree. Should is not does: if a NON-excluded old entry shows the show was
    # kept (reviewed, or synced and holding a stored episode list), excluding it would
    # delete a folder in use. Deletion cannot be undone and a wrongly kept show can, so a
    # conflict resolves toward keeping, and is reported.
    for new in sorted(t.excluded):
        olds = sources.get(new, set())
        excluded_olds = sorted(o for o in olds if o in old_excluded)
        kept_olds = sorted(o for o in olds
                           if o not in old_excluded and (o in old_reviewed or o in old_hashes))
        if excluded_olds and kept_olds:
            t.conflicts.append((new, excluded_olds, kept_olds))
    for new, _, _ in t.conflicts:
        t.excluded.discard(new)
        t.reviewed.add(new)
    return t


def untranslated_risk(t, old_excluded, old_reviewed, old_hashes, visible):
    """What the FIRST 0.33 sync would do if this config were left as it is.

    Measured on series a sync can see. "Kept" means the translation keeps it and it was on
    disk or reviewed; that is the population the deletion path can hurt.
    """
    kept = {s for s in visible if s not in t.excluded and (s in t.hashes or s in t.reviewed)}
    deleted = sorted(s for s in kept if s in old_excluded)

    # Written by an untranslated sync with the gate on: not excluded under the OLD numbers,
    # and either reviewed or holding a stored list under them. Leaked = written that way
    # although the translation would not write it — which covers both a show that should be
    # held AND an excluded show coming back under a number that happens to be reviewed.
    # (The gate's third marker, a folder of the same name on disk, is not visible from here;
    # it can only exempt shows already on disk, so it adds no leaks among excluded shows,
    # whose folders the exclusion pass already removed.)
    def written_untranslated(s):
        return s not in old_excluded and (s in old_reviewed or s in old_hashes)

    def written_translated(s):
        return s not in t.excluded and (s in t.reviewed or s in t.hashes)

    leaked = sorted(s for s in visible if written_untranslated(s) and not written_translated(s))

    # Excluded after translation, but its number is not in the old list, so it comes back:
    # leaked into the library when its number also collides with a reviewed or stored-list
    # id (counted above), otherwise held by the gate when it is on.
    returning = sorted(s for s in visible if s in t.excluded and s not in old_excluded)
    return kept, deleted, leaked, returning


def compare_maps(a, b, label_a, label_b):
    both = set(a.new_for) & set(b.new_for)
    changed = sorted(r for r in both if a.new_for[r] != b.new_for[r])
    print("\nMapping comparison: %s vs %s" % (label_a, label_b))
    print("  relations in both        : %d" % len(both))
    print("  in both, different show  : %d   <- expected 0" % len(changed))
    print("  only in %-17s: %d   (entries removed since)" % (label_a, len(set(a.new_for) - both)))
    print("  only in %-17s: %d   (entries added since)" % (label_b, len(set(b.new_for) - both)))
    for r in changed[:SAMPLE]:
        print("    relation %d: %d -> %d" % (r, a.new_for[r], b.new_for[r]))
    return len(changed)


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 16), b""):
            h.update(chunk)
    return h.hexdigest()


def write_translated(tree, t, path):
    root = tree.getroot()
    ET.register_namespace("xsd", "http://www.w3.org/2001/XMLSchema")
    ET.register_namespace("xsi", "http://www.w3.org/2001/XMLSchema-instance")

    def element(name):
        el = root.find(name)
        return el if el is not None else ET.SubElement(root, name)

    excluded = element("ExcludedSeriesIds")
    for node in list(excluded):
        excluded.remove(node)
    excluded.text = None
    for value in sorted(t.excluded):
        ET.SubElement(excluded, "int").text = str(value)

    element("ReviewedSeriesIdsJson").text = json.dumps(sorted(t.reviewed))
    element("SeriesEpisodeHashesJson").text = json.dumps(
        {str(k): t.hashes[k] for k in sorted(t.hashes)})

    tree.write(path, encoding="utf-8", xml_declaration=True)


def sample(ids):
    shown = ", ".join(str(i) for i in ids[:SAMPLE])
    return shown + (", ..." if len(ids) > SAMPLE else "")


def main(argv):
    parser = argparse.ArgumentParser(description="Translate plugin series ids for Dispatcharr 0.33.")
    parser.add_argument("--map", required=True, help="relation id -> series id export (CSV or JSON)")
    parser.add_argument("--compare-map", metavar="PATH",
                        help="an earlier export; reports any relation that now maps elsewhere")
    parser.add_argument("--write", metavar="PATH", help="write the translated config here")
    parser.add_argument("--ledger", metavar="PATH",
                        help="ledger of written outputs (default: beside --map)")
    parser.add_argument("--config-glob", help="override the config XML search pattern")
    args = parser.parse_args(argv)

    config_path = xc.find_config(args.config_glob)
    tree = xc.load_config(config_path)
    root = tree.getroot()

    # Beside the mapping, not the output or the config: the config is usually mounted
    # read-only, and the mapping is the one file every run of a changeover passes.
    ledger = args.ledger or os.path.join(os.path.dirname(os.path.abspath(args.map)), LEDGER_NAME)
    config_sha = sha256(config_path)
    if os.path.exists(ledger):
        with open(ledger, encoding="utf-8") as handle:
            if any(line.split()[0] == config_sha for line in handle if line.strip()):
                raise SystemExit(
                    "%s is a config this script already translated (see %s). Translating it again "
                    "would treat the new ids as old ones and scramble every store." % (config_path, ledger))

    mapping = load_mapping(args.map)
    old_excluded = xc.read_store(root, "ExcludedSeriesIds", "int-array")
    old_reviewed = xc.read_store(root, "ReviewedSeriesIdsJson", "json-array")
    old_hashes = read_hashes(root)
    visible, scoped = mapping.visible_series(selected_categories(root))

    print("config  : %s" % config_path)
    print("mapping : %s  (%d relations -> %d shows%s)" % (
        args.map, len(mapping.new_for), len(set(mapping.new_for.values())),
        "" if mapping.has_placement else "; no category/active columns"))
    print("visible : %d shows a sync can see (%s)" % (
        len(visible), "active entries in your selected series categories" if scoped
        else "UPPER BOUND: every show, the export had no placement columns"))

    if args.compare_map:
        if compare_maps(load_mapping(args.compare_map), mapping, "earlier", "this") and args.write:
            raise SystemExit("\nThe mapping moved between exports. Not writing anything; find out why first.")

    # Signal for "this already looks translated": the share of stored ids that are live OLD
    # relation ids. Before translation that is almost all of them, less dead entries. It is a
    # hint, not proof — the ranges overlap, which is the whole problem.
    stored = old_excluded | old_reviewed | set(old_hashes)
    as_relation = sum(1 for i in stored if i in mapping.new_for)
    print("\nstored series ids: %d, of which %d are relation ids Dispatcharr still has" % (len(stored), as_relation))

    t = translate(mapping, old_excluded, old_reviewed, old_hashes)
    kept, deleted, leaked, returning = untranslated_risk(t, old_excluded, old_reviewed, old_hashes, visible)

    print("\n== If this config were NOT translated, the first sync on 0.33 would ==")
    print("  delete folders of shows you keep : %d of %d kept, visible shows" % (len(deleted), len(kept)))
    if deleted:
        print("      new ids: %s" % sample(deleted))
    gate_on = xc.text(root, "RequireReviewBeforeSync").strip().lower() == "true"
    print("  let unreviewed shows through     : %d%s" % (
        len(leaked), "" if gate_on else "  (\"review before sync\" is off, so nothing is held anyway)"))
    if leaked:
        print("      new ids: %s" % sample(leaked))
    returning_leaked = len(set(returning) & set(leaked))
    print("  bring back shows you excluded    : %d  (%d of them written, counted above; the rest held%s)" % (
        len(returning), returning_leaked, "" if gate_on else " only if \"review before sync\" were on"))

    print("\n== Translation ==")
    print("  %-24s %7s %9s %8s" % ("store", "before", "after", "dropped"))
    print("  %-24s %7d %9d %8d" % ("ExcludedSeriesIds", len(old_excluded), len(t.excluded), t.dropped["excluded"]))
    print("  %-24s %7d %9d %8d" % ("ReviewedSeriesIdsJson", len(old_reviewed), len(t.reviewed), t.dropped["reviewed"]))
    print("  %-24s %7d %9d %8d" % ("SeriesEpisodeHashesJson", len(old_hashes), len(t.hashes), t.dropped["hashes"]))
    print("  Fewer ids after is expected: several old entries of one show become one id, and")
    print("  dropped ids are entries Dispatcharr no longer has.")
    print("  conflicts resolved toward keeping: %d   <- expected 0" % len(t.conflicts))
    for new, excl, kept_olds in t.conflicts[:SAMPLE]:
        print("      show %d: excluded via %s, kept via %s" % (new, excl, kept_olds))
    if t.hash_disagreements:
        print("  shows with two different stored episode lists: %d (first kept; costs one rewrite)"
              % t.hash_disagreements)

    if not args.write:
        print("\nReport only — nothing written. Re-run with --write PATH to emit the translated config.")
        return 0

    if os.path.abspath(args.write) == os.path.abspath(config_path):
        raise SystemExit("Refusing to write over the input config. Choose a different --write path.")

    write_translated(tree, t, args.write)
    with open(ledger, "a", encoding="utf-8") as handle:
        handle.write("%s  %s  translated from %s\n" % (sha256(args.write), args.write, config_sha))
    print("\nTranslated config written to %s (recorded in %s)." % (args.write, ledger))
    print("Before installing: diff it against the original — only the three series stores may differ.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
