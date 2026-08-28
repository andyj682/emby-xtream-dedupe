#!/usr/bin/env python3
"""Shared plumbing for the catalogue snapshot and ID-churn repair scripts.

Both scripts need the same three things: the plugin's Xtream credentials out of Emby's
config XML, a read-only catalogue fetch, and a stable on-disk record of "which provider
ID was which title, at a point in time". Keeping that here means the snapshot writer and
the repair reader cannot drift apart on the format.

Why this exists at all: the plugin stores exclusions and reviewed-checkpoints as bare
provider IDs, and the provider reassigns those IDs in bulk when a source is re-ingested.
Every reassigned ID silently detaches the decision attached to it — the title reappears
as un-excluded and unreviewed, with nothing in any log to say why. A dated snapshot is
the only way to recover what a now-dead ID used to refer to, because the config records
an ID and nothing else.

Read-only by construction: nothing here writes to the config, and the only provider calls
are the two list endpoints. ``get_series_info`` is deliberately absent — it triggers
Dispatcharr's gated episode refresh as a side effect, so it has no place in a diagnostic.
"""

import glob
import json
import os
import re
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET

DEFAULT_CONFIG_GLOB = "/cfg/plugins/configurations/*Xtream*.xml"

SNAPSHOT_HEADER = "#kind\tid\ttmdb\tname"

# (config element, which catalogue it indexes, how it is stored in the XML)
ID_STORES = (
    ("ExcludedVodStreamIds", "movie", "int-array"),
    ("ReviewedVodStreamIdsJson", "movie", "json-array"),
    ("ExcludedSeriesIds", "series", "int-array"),
    ("ReviewedSeriesIdsJson", "series", "json-array"),
)

# Which store holds the "I have looked at this" mark for each catalogue. The repair uses
# it as evidence of a decision made *after* the churn, so it does not undo one.
REVIEWED_STORE_FOR = {"movie": "ReviewedVodStreamIdsJson", "series": "ReviewedSeriesIdsJson"}
EXCLUDED_STORE_FOR = {"movie": "ExcludedVodStreamIds", "series": "ExcludedSeriesIds"}

CATALOGUE_ACTION = {"movie": "get_vod_streams", "series": "get_series"}
CATALOGUE_ID_FIELD = {"movie": "stream_id", "series": "series_id"}
# Movies carry the TMDB ID on the list payload; series do NOT (measured 0 of 9,979 — it
# only appears on the get_series_info detail payload). Series therefore fall back to
# name matching, which is the same fidelity the de-dup view's own grouping uses.
CATALOGUE_TMDB_FIELD = {"movie": "tmdb_id", "series": "tmdb"}

_WHITESPACE = re.compile(r"\s+")


def find_config(path_glob=None):
    """Locate the plugin config XML. Env override: XTREAM_CONFIG_GLOB."""
    pattern = path_glob or os.environ.get("XTREAM_CONFIG_GLOB") or DEFAULT_CONFIG_GLOB
    matches = sorted(glob.glob(pattern))
    if not matches:
        raise SystemExit("No plugin config XML matched: %s" % pattern)
    return matches[0]


def load_config(path):
    return ET.parse(path)


def text(root, tag, default=""):
    el = root.find(tag)
    return el.text if el is not None and el.text else default


def credentials(root):
    """The Xtream (XC) credentials — NOT DispatcharrUrl/User/Pass, which are its native API.

    XC_BASE overrides BaseUrl, for when the stored hostname is only resolvable on a
    docker network this container is not attached to.
    """
    base = (os.environ.get("XC_BASE") or text(root, "BaseUrl")).rstrip("/")
    if not base:
        raise SystemExit("BaseUrl is empty in the config and XC_BASE is not set")
    return base, text(root, "Username"), text(root, "Password")


def parse_tmdb(raw):
    """Positive integer or 0, mirroring the plugin's own tolerance for a dirty field."""
    try:
        value = int(str(raw or "").strip())
    except (TypeError, ValueError):
        return 0
    return value if value > 0 else 0


def read_store(root, element, storage):
    """Read one ID store.

    Deliberately raises on an unparseable JSON store rather than returning an empty set.
    Treating "I could not read this" as "this is empty" is exactly the failure mode that
    can silently discard tens of thousands of decisions, and a repair script must never
    propose changes on the strength of a store it failed to read.
    """
    if storage == "int-array":
        out = set()
        for node in root.findall(element + "/int"):
            try:
                out.add(int((node.text or "").strip()))
            except ValueError:
                continue
        return out

    raw = text(root, element).strip()
    if not raw:
        return set()
    try:
        parsed = json.loads(raw)
    except ValueError as exc:
        raise SystemExit(
            "%s does not parse as JSON (%s). Refusing to continue — repairing against a "
            "store we cannot read could discard every decision in it." % (element, exc))
    if not isinstance(parsed, list):
        raise SystemExit("%s is not a JSON array" % element)
    out = set()
    for item in parsed:
        try:
            out.add(int(item))
        except (TypeError, ValueError):
            continue
    return out


def read_all_stores(root):
    return {element: read_store(root, element, storage) for element, _, storage in ID_STORES}


def fetch_catalogue(base, user, password, kind, timeout=300):
    """One list call. Returns [(id, tmdb, name)] with ids coerced to int."""
    url = base + "/player_api.php?" + urllib.parse.urlencode({
        "username": user, "password": password, "action": CATALOGUE_ACTION[kind]})
    with urllib.request.urlopen(url, timeout=timeout) as response:
        payload = json.load(response) or []

    id_field = CATALOGUE_ID_FIELD[kind]
    tmdb_field = CATALOGUE_TMDB_FIELD[kind]
    rows = []
    for entry in payload:
        try:
            item_id = int(entry.get(id_field) or 0)
        except (TypeError, ValueError):
            continue
        if item_id <= 0:
            continue
        rows.append((item_id, parse_tmdb(entry.get(tmdb_field)), str(entry.get("name") or "")))
    return rows


def normalise_name(name):
    return _WHITESPACE.sub(" ", str(name or "")).strip().casefold()


def identity(tmdb, name, tmdb_only=False):
    """A provider-ID-independent handle on "which title is this".

    TMDB ID when there is one — it survives renames, which is the whole point. Name
    otherwise, which is all series ever have on the list payload, and all that the ~5%
    of movies without a TMDB ID have. Returns None when neither is usable.
    """
    if tmdb:
        return ("tmdb", tmdb)
    if tmdb_only:
        return None
    key = normalise_name(name)
    return ("name", key) if key else None


def write_snapshot(path, rows):
    """rows: iterable of (kind, id, tmdb, name). Tabs and newlines stripped from names."""
    written = 0
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(SNAPSHOT_HEADER + "\n")
        for kind, item_id, tmdb, name in rows:
            clean = str(name or "").replace("\t", " ").replace("\r", " ").replace("\n", " ")
            handle.write("%s\t%d\t%s\t%s\n" % (kind, item_id, tmdb or "", clean))
            written += 1
    return written


def read_snapshot(path):
    """Returns {(kind, id): (tmdb, name)}."""
    out = {}
    with open(path, "r", encoding="utf-8") as handle:
        for line in handle:
            if not line.strip() or line.startswith("#"):
                continue
            parts = line.rstrip("\n").split("\t")
            if len(parts) < 4:
                continue
            kind, raw_id, raw_tmdb, name = parts[0], parts[1], parts[2], parts[3]
            try:
                item_id = int(raw_id)
            except ValueError:
                continue
            out[(kind, item_id)] = (parse_tmdb(raw_tmdb), name)
    if not out:
        raise SystemExit("Snapshot %s contained no usable rows" % path)
    return out
