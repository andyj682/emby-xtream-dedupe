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

# The methods that decide what the sync deletes. Checking only the delete calls is not enough:
# CleanupOrphans could move back to StrmSyncService.cs and call a delete helper left in the
# cleanup file, and Stryker would silently stop mutating the part that decides.
CLEANUP_METHODS = (
    "RemoveExcludedContent",
    "CleanupOrphans",
    "BuildWrittenDirectories",
    "RecordExistingStrms",
    "RecordStrms",
    "NormalizeDirectory",
    "FindExistingSeriesStrms",
)


def _defines(text: str, method: str) -> bool:
    """True when ``text`` declares ``method`` (a modifier, a return type, the name, then '(')."""
    return re.search(
        r"^\s*(?:(?:private|internal|public|protected|static|async)\s+)+[\w<>\[\],\s]+?\b"
        + re.escape(method) + r"\s*\(",
        text, re.MULTILINE) is not None

# Delete invocations. Matches ``File\Delete`` / ``Directory\Delete`` / ``StrmOwnership\DeleteOwnedFiles``
# whether they sit on one line or split across lines: a method receiver on its own line followed by
# a newline and ``.Delete`` / ``.DeleteOwnedFiles`` is a legal C# form and would otherwise slip past
# the guard. We strip whitespace and newlines inside a small window before matching.
DELETE_CODE = re.compile(
    r"\b(?:File|Directory)\s*\.\s*Delete\s*\(|StrmOwnership\s*\.\s*DeleteOwnedFiles\s*\(",
    re.DOTALL,
)

# Ownership verification lives here; deletes in this file are the sanctioned ones.
SANCTIONED_FILE = "Service/StrmOwnership.cs"

DELETE_CALL = re.compile(r"\b(?:File|Directory)\s*\.\s*Delete\s*\(", re.DOTALL)

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


# Find every delete invocation across the file (handles single- and multi-line forms) and
# report the line where the invocation begins. The ``\s*`` in the regex already matches
# newlines via re.DOTALL, so ``File\\n    .Delete(...)`` matches in one pass on the whole text.
def _iter_pattern(pattern, text: str):
    """Yield ``(line_number_1based, match_text)`` for each ``pattern`` hit in ``text``."""
    for m in pattern.finditer(text):
        line_no = text.count("\n", 0, m.start()) + 1
        yield line_no, m.group(0)


def find_unjustified(root: Path):
    problems = []

    for path in sorted(root.rglob("*.cs")):
        rel = path.relative_to(root).as_posix()
        if rel == SANCTIONED_FILE:
            continue
        # Build output is not source.
        if rel.startswith(("obj/", "bin/")):
            continue

        text = path.read_text(encoding="utf-8", errors="replace")
        lines = text.splitlines()
        for line_no, _ in _iter_pattern(DELETE_CALL, text):
            if is_justified(lines, line_no - 1):
                continue
            problems.append((rel, line_no, lines[line_no - 1].strip()))

    return problems


# Stryker treats ``mutate`` entries as globs and follows the documented precedence:
# a file is mutated when it matches at least one inclusion and no exclusion. The
# suffix check below only catches a direct exclusion; it misses a broad exclusion
# like ``!**/Service/*.cs`` that swallows the cleanup file even when the cleanup
# file is also listed as an inclusion. ``PurePosixPath.match`` and ``fnmatch`` both
# get ``**`` wrong, so translate the glob to a regex that lets ``**`` cross ``/``.
# Stryker routes ``mutate`` through DotNet.Glob, which honors ``[...]`` character
# classes (e.g. ``*.[cC]s`` matches ``.cs`` and ``.Cs``); escape them as literals
# and the checker disagrees with Stryker on the exact files the exclusion catches.
def _glob_class_to_regex(class_body: str) -> str:
    """Translate one bracket character class body to a regex character class.

    Supports negation via a leading ``!`` or ``^`` (DotNet.Glob accepts both),
    otherwise the class lists literal characters and ``a-z`` ranges verbatim.
    An unclosed bracket is treated as a literal so the glob still compiles.
    """
    if not class_body:
        return r"\["
    negated = class_body[0] in "!^"
    body = class_body[1:] if negated else class_body
    inner = []
    for ch in body:
        if ch in r"\][^":
            inner.append("\\" + ch)
        else:
            inner.append(ch)
    return "[^" + "".join(inner) + "]" if negated else "[" + "".join(inner) + "]"


def _glob_to_regex(glob: str) -> "re.Pattern[str]":
    parts = []
    i = 0
    while i < len(glob):
        c = glob[i]
        if c == "*":
            if i + 1 < len(glob) and glob[i + 1] == "*":
                parts.append(".*")
                i += 2
                if i < len(glob) and glob[i] == "/":
                    i += 1
                continue
            parts.append("[^/]*")
        elif c == "?":
            parts.append("[^/]")
        elif c == "[":
            end = glob.find("]", i + 1)
            if end == -1:
                parts.append(re.escape(c))
                i += 1
                continue
            parts.append(_glob_class_to_regex(glob[i + 1:end]))
            i = end + 1
            continue
        else:
            parts.append(re.escape(c))
        i += 1
    return re.compile("^" + "".join(parts) + "$")


def _glob_matches(glob: str, path: str) -> bool:
    return _glob_to_regex(glob).match(path) is not None


def _stryker_mutates(patterns, path: str) -> bool:
    inclusions = [p.lstrip("!") for p in patterns if not p.startswith("!")]
    exclusions = [p[1:] for p in patterns if p.startswith("!")]
    included = any(_glob_matches(pat, path) for pat in inclusions)
    excluded = any(_glob_matches(pat, path) for pat in exclusions)
    return included and not excluded


def find_mutation_gaps(service_sources, stryker_config: str):
    """Problems that would let the sync's delete code escape mutation testing.

    ``service_sources`` maps a path relative to the plugin root to that file's text, for every
    StrmSyncService*.cs file.

    The Stryker ``mutate`` list treats entries prefixed with ``!`` as exclusions. A config that
    contains only ``!**/Service/StrmSyncService.Cleanup.cs`` would (a) pass the naive
    ``endswith`` check below and (b) leave the cleanup file unmutated, defeating the whole point
    of the guard. An exclusion that targets the cleanup file is treated as a gap too.
    """
    problems = []
    mutate = json.loads(stryker_config)["stryker-config"]["mutate"]
    if not _stryker_mutates(mutate, MUTATED_FILE):
        problems.append((MUTATED_FILE, 0, "not in the mutate list of stryker-config.json"))
    if any(p.startswith("!") and _glob_matches(p[1:], MUTATED_FILE) for p in mutate):
        problems.append((MUTATED_FILE, 0,
                         "excluded from the mutate list of stryker-config.json"))

    cleanup_text = service_sources.get(MUTATED_FILE, "")
    for method in CLEANUP_METHODS:
        if not _defines(cleanup_text, method):
            problems.append((MUTATED_FILE, 0, f"does not define {method}"))

    for rel, text in sorted(service_sources.items()):
        if rel == MUTATED_FILE:
            continue
        for line_no, matched in _iter_pattern(DELETE_CODE, text):
            problems.append((rel, line_no, matched))
        for method in CLEANUP_METHODS:
            if _defines(text, method):
                problems.append((rel, 0, f"defines {method}, which belongs in {MUTATED_FILE}"))

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
