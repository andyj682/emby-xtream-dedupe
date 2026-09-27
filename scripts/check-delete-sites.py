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

It also checks that the Stryker mutation range in ``stryker-config.json`` still covers the
delete methods of ``StrmSyncService``. Stryker only takes a line range, and code added above
those methods moves them out of it; the mutation job then tests unrelated code and keeps
passing, which is how the range went stale unnoticed once already.

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

# The file whose delete methods Stryker mutates by line range, and the methods that must
# stay inside that range together with every delete call in the file.
MUTATED_FILE = "Service/StrmSyncService.cs"
MUTATED_METHODS = ("private int RemoveExcludedContent(", "private int CleanupOrphans(")
MUTATE_RANGE = re.compile(r"StrmSyncService\.cs\{(\d+)\.\.(\d+)\}")

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


def find_uncovered_mutation_targets(source: str, stryker_config: str):
    """Lines of the delete methods and delete calls that the Stryker range misses."""
    patterns = [p for p in json.loads(stryker_config)["stryker-config"]["mutate"]
                if MUTATE_RANGE.search(p)]
    if len(patterns) != 1:
        return [(0, "stryker-config.json must have exactly one StrmSyncService.cs{start..end} entry")]

    start, end = (int(g) for g in MUTATE_RANGE.search(patterns[0]).groups())
    lines = source.splitlines()
    targets = []
    for signature in MUTATED_METHODS:
        found = [i + 1 for i, line in enumerate(lines) if signature in line]
        if not found:
            targets.append((0, f"method not found: {signature}"))
        targets.extend((n, lines[n - 1].strip()) for n in found)
    targets.extend((i + 1, line.strip()) for i, line in enumerate(lines) if DELETE_CALL.search(line))

    return [(n, text) for n, text in targets if not start <= n <= end]


def main() -> int:
    if not PLUGIN_ROOT.is_dir():
        print(f"error: plugin root not found at {PLUGIN_ROOT}", file=sys.stderr)
        return 2

    uncovered = find_uncovered_mutation_targets(
        (PLUGIN_ROOT / MUTATED_FILE).read_text(encoding="utf-8"),
        STRYKER_CONFIG.read_text(encoding="utf-8"))
    if uncovered:
        print("The Stryker mutation range no longer covers the delete code in "
              f"{MUTATED_FILE}.\n")
        for line_no, text in uncovered:
            print(f"  line {line_no}: {text}")
        print("\nUpdate the StrmSyncService.cs{start..end} range in stryker-config.json so it"
              " spans from the first delete method to the last delete call.")
        return 1

    problems = find_unjustified(PLUGIN_ROOT)
    if not problems:
        print("delete-site check: all delete calls are sanctioned or justified,"
              " and the mutation range covers the delete methods")
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
