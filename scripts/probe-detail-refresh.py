#!/usr/bin/env python3
"""Measure what a real ``get_vod_info`` call actually changes, on a small sample.

ADR-F006 wants to decide which movies still need a detail call by looking for the absence
of stored detail in the list payload. ``measure-detail-marker.py`` shows that almost nothing
currently carries that detail — but it cannot say whether a call would *change* that, and
that is the number the design turns on:

  * if most titles gain ``director``/``cast`` after a call, the marker works: the first run
    is expensive, every run after it is nearly free.
  * if they do not, the marker can never flip for those titles, they would be re-called on
    every sync forever, and the design has to fall back to publishing the wanted set instead.

It also separates the two reasons a title might not flip, which matters because only one of
them is fatal:

  * **the provider supplied nothing** — the detail response itself carries no director or
    cast, so there is nothing to store and the marker is structurally blind for that title.
  * **the provider supplied it but the list payload still does not show it** — a
    proxy-side question, not a provider one.

⚠️ **THIS SCRIPT MUTATES.** ``get_vod_info`` triggers the proxy's detail refresh, which
writes to its database and can merge duplicate movie rows (that is what ADR-F004 stage 3
exists to absorb). That is the whole point — it performs exactly what the feature would —
but it is the only script here that is not read-only, so it refuses to run without
``--confirm`` and samples a small number of titles by default.

Do not run it while the provider ingest or the episode sweep is mid-cycle: these calls are
synchronous on the proxy side and contend for the same provider connection slots.

Usage:
  python3 scripts/probe-detail-refresh.py --dry-run
  python3 scripts/probe-detail-refresh.py --confirm --sample 30
"""

import argparse
import json
import os
import random
import sys
import time
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import xtream_catalogue as xc  # noqa: E402

MARKER_FIELDS = ["director", "cast"]
DETAIL_KEYS = ("director", "actors", "cast")
MAX_SAMPLE = 200


def populated(entry, field):
    value = entry.get(field)
    return value is not None and str(value).strip() not in ("", "0")


def has_marker(entry):
    return any(populated(entry, f) for f in MARKER_FIELDS)


def detail_has_people(payload):
    """True when the detail response carries a director/cast anywhere in it.

    Searched recursively rather than at a fixed path: the payload nests the provider's own
    block and the proxy's derived block under different keys, and which one carries the
    value varies by provider.
    """
    if isinstance(payload, dict):
        for key, value in payload.items():
            if key in DETAIL_KEYS and value is not None and str(value).strip() not in ("", "0"):
                return True
            if detail_has_people(value):
                return True
    elif isinstance(payload, list):
        return any(detail_has_people(v) for v in payload)
    return False


def fetch_detail(base, user, password, vod_id, timeout=60):
    """THE MUTATING CALL. Deliberately local to this script rather than in the shared
    catalogue module, which is read-only by design and must stay that way."""
    params = {"username": user, "password": password,
              "action": "get_vod_info", "vod_id": vod_id}
    url = base + "/player_api.php?" + urllib.parse.urlencode(params)
    started = time.time()
    with urllib.request.urlopen(url, timeout=timeout) as response:
        payload = json.load(response)
    return payload, time.time() - started


def name_prefix(name):
    text = str(name or "").strip()
    for sep in (" - ", "- ", "| ", " |"):
        if sep in text[:14]:
            return text.split(sep, 1)[0].strip()[:16] or "(none)"
    return "(no prefix)"


def main(argv):
    parser = argparse.ArgumentParser(
        description="Probe whether a get_vod_info call makes the ADR-F006 marker flip.")
    parser.add_argument("--config-glob")
    parser.add_argument("--sample", type=int, default=30,
                        help="titles to call (default 30, max %d)" % MAX_SAMPLE)
    parser.add_argument("--seed", type=int, default=1, help="sampling seed, for repeatability")
    parser.add_argument("--dry-run", action="store_true",
                        help="show what would be called and exit without calling")
    parser.add_argument("--confirm", action="store_true",
                        help="required to make any call; this script writes to the proxy")
    args = parser.parse_args(argv)

    if args.sample > MAX_SAMPLE:
        parser.error("--sample above %d; this triggers a real refresh per title" % MAX_SAMPLE)

    config_path = xc.find_config(args.config_glob)
    root = xc.load_config(config_path).getroot()
    base, user, password = xc.credentials(root)
    stores = xc.read_all_stores(root)
    categories = xc.read_store(root, "SelectedVodCategoryIds", "int-array")
    gate_on = (xc.text(root, "RequireReviewBeforeSync", "false") or "").lower() == "true"

    print("config : %s" % config_path)
    print("marker : %s\n" % " or ".join(MARKER_FIELDS))

    before = xc.fetch_raw(base, user, password, "movie", categories or None,
                          keep=MARKER_FIELDS + ["name"])
    excluded = set(stores["ExcludedVodStreamIds"])
    reviewed = set(stores["ReviewedVodStreamIdsJson"] or [])

    wanted = {i: e for i, e in before.items()
              if i not in excluded and (not gate_on or i in reviewed)}
    unmarked = sorted(i for i, e in wanted.items() if not has_marker(e))

    print("wanted set      : %d" % len(wanted))
    print("without marker  : %d  (%.1f%%) — the population this would call\n"
          % (len(unmarked), 100.0 * len(unmarked) / max(len(wanted), 1)))

    if not unmarked:
        print("Nothing to probe: every wanted title already carries the marker.")
        return 0

    random.seed(args.seed)
    sample = random.sample(unmarked, min(args.sample, len(unmarked)))

    print("Would call get_vod_info for %d of them (seed %d)." % (len(sample), args.seed))
    if args.dry_run or not args.confirm:
        print("\nNo calls made.%s" % ("" if args.dry_run else "  Re-run with --confirm to probe."))
        print("Each call triggers a detail refresh on the proxy and may merge duplicate rows.")
        return 0

    print("Calling...\n")
    timings, supplied = [], []
    for n, vod_id in enumerate(sample, 1):
        try:
            payload, elapsed = fetch_detail(base, user, password, vod_id)
        except Exception as exc:                                    # noqa: BLE001
            print("  [%2d/%d] id=%-8d FAILED: %s" % (n, len(sample), vod_id, exc))
            continue
        timings.append(elapsed)
        supplied.append((vod_id, detail_has_people(payload)))
        print("  [%2d/%d] id=%-8d %5.2fs  detail carries director/cast: %s"
              % (n, len(sample), vod_id, elapsed, "yes" if supplied[-1][1] else "NO"))

    if not timings:
        print("\nEvery call failed; nothing measured.")
        return 1

    # Re-read the listing. The refresh is synchronous on the proxy side, so anything it
    # stored is already visible.
    print("\nRe-reading the catalogue listing...")
    after = xc.fetch_raw(base, user, password, "movie", categories or None,
                         keep=MARKER_FIELDS + ["name"])

    flipped = [i for i, _ in supplied if i in after and has_marker(after[i])]
    gave = [i for i, ok in supplied if ok]
    called = [i for i, _ in supplied]

    print("\n" + "=" * 74)
    print("RESULT — %d titles called" % len(called))
    print("  detail response carried director/cast : %4d  (%5.1f%%)"
          % (len(gave), 100.0 * len(gave) / len(called)))
    print("  listing now shows the marker          : %4d  (%5.1f%%)   <- THE NUMBER"
          % (len(flipped), 100.0 * len(flipped) / len(called)))

    stuck = [i for i in gave if i not in flipped]
    if stuck:
        print("  !! %d title(s) whose detail HAD director/cast but whose listing did not flip"
              % len(stuck))
        print("     — that is a proxy-side question, not a provider one.")

    timings.sort()
    total = sum(timings)
    print("\n  per call: min %.2fs  median %.2fs  max %.2fs  (mean %.2fs)"
          % (timings[0], timings[len(timings) // 2], timings[-1], total / len(timings)))

    rate = len(flipped) / float(len(called))
    print("\n" + "=" * 74)
    print("PROJECTION for %d titles without the marker" % len(unmarked))
    print("  first run, serial          : %.0f min" % (len(unmarked) * (total / len(timings)) / 60.0))
    print("  first run, 3 in parallel   : %.0f min" % (len(unmarked) * (total / len(timings)) / 180.0))
    print("  still unmarked afterwards  : %.0f  (%.0f%% of the set, re-called every run)"
          % (len(unmarked) * (1 - rate), 100.0 * (1 - rate)))

    print("\n  VERDICT:", end=" ")
    if rate >= 0.9:
        print("marker works. Steady state is near zero; ADR-F006 proceeds as designed.")
    elif rate >= 0.5:
        print("marker works partially. Weigh the residual against publishing the wanted set.")
    else:
        print("marker does NOT flip for most titles. It cannot gate the calls —")
        print("           fall back to publishing the wanted set directly.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
