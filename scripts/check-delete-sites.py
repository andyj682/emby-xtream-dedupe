#!/usr/bin/env python3
"""Guard every filesystem delete in the plugin.

The plugin deletes files on the user's disk, and three separate paths once destroyed
content it had not written (BUG-031). Ownership verification now lives in
``StrmOwnership``, but that is only an agreement until something enforces it: a new
delete added anywhere else silently skips the check.

So every ``File.Delete`` / ``Directory.Delete`` must either live in ``StrmOwnership``,
or carry a ``delete-ok:`` comment saying why it is not touching library content. The
comment is the point. It makes "I am deleting something the user did not give me"
a decision someone had to write down.

It also checks that the mutation tests (``stryker-config.json``) reach the sync's delete code.
That code lives in ``StrmSyncService.Cleanup.cs`` so Stryker can mutate the whole file: every
delete call in the sync service must be in that file, and the file must be in Stryker's list.
A delete added elsewhere in the service would otherwise go untested while the job still passed
(issue #75).

Run: python3 scripts/check-delete-sites.py
Exits non-zero and prints every unjustified site.
"""

import json
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
PLUGIN_ROOT = REPO_ROOT / "Emby.Xtream.Plugin"
STRYKER_CONFIG = REPO_ROOT / "stryker-config.json"

# The sync's delete code, which the mutation tests must cover, and the files it must not leak
# into. See issue #75.
MUTATED_FILE = "Service/StrmSyncService.Cleanup.cs"
SERVICE_FILES = re.compile(r"^Service/StrmSyncService(\..+)?\.cs$")
DELETE_CODE = re.compile(r"\b(?:File|Directory)\.Delete\s*\(|StrmOwnership\.DeleteOwnedFiles\s*\(")

# Ownership verification lives here; deletes in this file are the sanctioned ones.
SANCTIONED_FILE = "Service/StrmOwnership.cs"

DELETE_CALL = re.compile(r"\b(?:File|Directory)\.Delete\s*\(")

# Must be a real line comment carrying a reason, not the text "delete-ok:" appearing
# anywhere. A string literal or an unrelated neighbouring line must not approve a delete.
JUSTIFICATION = re.compile(r"^\s*//\s*delete-ok:\s*\S")

# Any line comment, used to walk the contiguous comment block above a delete.
COMMENT_LINE = re.compile(r"^\s*//")


def is_justified(lines, index: int) -> bool:
    """True when a `// delete-ok:` comment sits in the comment block directly above.

    Deliberately does not accept a trailing comment on the delete line itself. Finding the
    real `//` there means knowing which ones are inside string literals, and
    ``File.Delete("http://delete-ok: x")`` would sail past a naive split. Dropping the
    form is cheaper and more trustworthy than parsing C# strings in a guard script, and no
    call site wanted it.
    """
    # Walk upwards only while the lines are still comments. The first non-comment line
    # ends the block, so a justification further up cannot reach across real code.
    i = index - 1
    while i >= 0 and COMMENT_LINE.match(lines[i]):
        if JUSTIFICATION.match(lines[i]):
            return True
        i -= 1

    return False


def find_unjustified(root: Path):
    problems = []

    for path in sorted(root.rglob("*.cs")):
        rel = path.relative_to(root).as_posix()
        if rel == SANCTIONED_FILE:
            continue
        # Build output is not source.
        if rel.startswith(("obj/", "bin/")):
            continue

        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
        for i, line in enumerate(lines):
            if not DELETE_CALL.search(line):
                continue
            if is_justified(lines, i):
                continue
            problems.append((rel, i + 1, line.strip()))

    return problems


def find_mutation_gaps(service_sources, stryker_config: str):
    """Problems that would let the sync's delete code escape mutation testing.

    ``service_sources`` maps a path relative to the plugin root to that file's text, for every
    StrmSyncService*.cs file.
    """
    problems = []
    mutate = json.loads(stryker_config)["stryker-config"]["mutate"]
    if not any(p.endswith(MUTATED_FILE) for p in mutate):
        problems.append((MUTATED_FILE, 0, "not in the mutate list of stryker-config.json"))

    for rel, text in sorted(service_sources.items()):
        if rel == MUTATED_FILE:
            continue
        for i, line in enumerate(text.splitlines()):
            if DELETE_CODE.search(line):
                problems.append((rel, i + 1, line.strip()))

    return problems


def main() -> int:
    if not PLUGIN_ROOT.is_dir():
        print(f"error: plugin root not found at {PLUGIN_ROOT}", file=sys.stderr)
        return 2

    service_sources = {
        path.relative_to(PLUGIN_ROOT).as_posix(): path.read_text(encoding="utf-8")
        for path in (PLUGIN_ROOT / "Service").glob("StrmSyncService*.cs")
    }
    gaps = find_mutation_gaps(service_sources, STRYKER_CONFIG.read_text(encoding="utf-8"))
    if gaps:
        print("The sync's delete code is not where the mutation tests look for it.\n")
        for rel, line_no, text in gaps:
            print(f"  Emby.Xtream.Plugin/{rel}:{line_no}: {text}")
        print(f"\nKeep every delete call of the sync service in {MUTATED_FILE}, and keep that"
              " file in the mutate list of stryker-config.json (issue #75).")
        return 1

    problems = find_unjustified(PLUGIN_ROOT)
    if not problems:
        print("delete-site check: all delete calls are sanctioned or justified,"
              " and the sync's delete code is covered by the mutation tests")
        return 0

    print("Unjustified filesystem delete(s) found.\n")
    for rel, line_no, text in problems:
        print(f"  Emby.Xtream.Plugin/{rel}:{line_no}")
        print(f"    {text}")
    print(
        "\nEvery delete outside Service/StrmOwnership.cs must be reachable only for content\n"
        "this plugin wrote, or carry a justification on its own comment line in the block\n"
        "directly above the call:\n"
        "\n    // delete-ok: <why this cannot touch user content>\n"
        "\nIf it can touch the STRM library, route it through StrmOwnership instead. See ADR-014."
    )
    return 1


if __name__ == "__main__":
    sys.exit(main())
