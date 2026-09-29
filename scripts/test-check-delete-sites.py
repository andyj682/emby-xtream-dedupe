#!/usr/bin/env python3
"""Tests for the delete-site guard.

The guard decides whether a filesystem delete is allowed to reach main. That makes it
load-bearing, so it needs to be shown failing on the things it claims to catch. A guard
that cannot reject is the same problem as a test that cannot fail.

Run: python3 scripts/test-check-delete-sites.py
"""

import importlib.util
import sys
import tempfile
from pathlib import Path

SCRIPT = Path(__file__).resolve().parent / "check-delete-sites.py"

spec = importlib.util.spec_from_file_location("check_delete_sites", SCRIPT)
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)


CASES = [
    (
        "bare delete is rejected",
        'class A { void M() { File.Delete("x"); } }',
        False,
    ),
    (
        "comment directly above is accepted",
        'class A { void M() {\n'
        '    // delete-ok: temp file this method just wrote\n'
        '    File.Delete("x"); } }',
        True,
    ),
    (
        "comment separated by real code is rejected",
        'class A { void M() {\n'
        '    // delete-ok: temp file\n'
        '    if (File.Exists(p))\n'
        '        File.Delete("x"); } }',
        False,
    ),
    (
        "justification inside a string literal is rejected",
        'class A { void M() { File.Delete("http://delete-ok: not a comment"); } }',
        False,
    ),
    (
        "marker buried in unrelated comment prose is rejected",
        'class A { void M() {\n'
        '    // see delete-ok: conventions elsewhere\n'
        '    File.Delete("x"); } }',
        False,
    ),
    (
        "reason is required after the marker",
        'class A { void M() {\n'
        '    // delete-ok:\n'
        '    File.Delete("x"); } }',
        False,
    ),
    (
        "Directory.Delete is covered too",
        'class A { void M() { Directory.Delete("x", true); } }',
        False,
    ),
    (
        "multi-line comment block reaches the marker",
        'class A { void M() {\n'
        '    // delete-ok: plugin backup, not library content\n'
        '    // (kept short on purpose)\n'
        '    File.Delete("x"); } }',
        True,
    ),
    (
        "File and .Delete on separate lines is rejected",
        'class A { void M() {\n'
        '    File\n'
        '        .Delete("x"); } }',
        False,
    ),
    (
        "Directory and .Delete on separate lines is rejected",
        'class A { void M() {\n'
        '    Directory\n'
        '        .Delete("x", true); } }',
        False,
    ),
]


def run_case(source: str) -> bool:
    """True when the guard accepts the source."""
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / "Service").mkdir()
        (root / "Sample.cs").write_text(source, encoding="utf-8")
        return not guard.find_unjustified(root)


def main() -> int:
    failures = []

    for name, source, expected in CASES:
        actual = run_case(source)
        if actual != expected:
            failures.append(
                f"  {name}: expected {'accept' if expected else 'reject'}, "
                f"got {'accept' if actual else 'reject'}"
            )

    # The sanctioned file must be exempt.
    with tempfile.TemporaryDirectory() as tmp:
        root = Path(tmp)
        (root / "Service").mkdir()
        (root / "Service" / "StrmOwnership.cs").write_text(
            'class A { void M() { File.Delete("x"); } }', encoding="utf-8")
        if guard.find_unjustified(root):
            failures.append("  StrmOwnership.cs should be exempt but was flagged")

    # The sync's delete code must stay in the file the mutation tests cover.
    covered = '{"stryker-config": {"mutate": ["**/Service/StrmSyncService.Cleanup.cs"]}}'
    not_covered = '{"stryker-config": {"mutate": ["**/Service/StrmOwnership.cs"]}}'
    delete_line = "  // delete-ok: test\n  File.Delete(x);"
    # A cleanup file that defines every method the guard requires, so each case below is
    # rejected only for the reason it is about.
    cleanup_file = "\n".join(
        f"        private static int {m}(string a)" for m in guard.CLEANUP_METHODS) + "\n" + delete_line
    range_cases = [
        ("delete code in the cleanup file is accepted",
         {"Service/StrmSyncService.Cleanup.cs": cleanup_file, "Service/StrmSyncService.cs": "class S {}"},
         covered, True),
        ("a delete in the main service file is rejected",
         {"Service/StrmSyncService.Cleanup.cs": cleanup_file, "Service/StrmSyncService.cs": delete_line},
         covered, False),
        ("DeleteOwnedFiles in the main service file is rejected",
         {"Service/StrmSyncService.Cleanup.cs": cleanup_file, "Service/StrmSyncService.cs": "StrmOwnership.DeleteOwnedFiles(d);"},
         covered, False),
        ("DeleteOwnedFiles split across lines in the main service file is rejected",
         {"Service/StrmSyncService.Cleanup.cs": cleanup_file,
          "Service/StrmSyncService.cs": "StrmOwnership\n    .DeleteOwnedFiles(d);"},
         covered, False),
        ("the cleanup file missing from Stryker is rejected",
         {"Service/StrmSyncService.Cleanup.cs": cleanup_file},
         not_covered, False),
        ("the cleanup file excluded from Stryker is rejected",
         {"Service/StrmSyncService.Cleanup.cs": cleanup_file,
          "Service/StrmSyncService.cs": ""},
         '{"stryker-config": {"mutate": ["!**/Service/StrmSyncService.Cleanup.cs"]}}',
         False),
        ("the cleanup file listed alongside an exclusion for another file is accepted",
         {"Service/StrmSyncService.Cleanup.cs": cleanup_file,
          "Service/StrmSyncService.cs": ""},
         '{"stryker-config": {"mutate": ['
         '"!**/Service/StrmSyncService.cs", '
         '"**/Service/StrmSyncService.Cleanup.cs"]}}',
         True),
        ("a broad exclusion of the cleanup file is rejected",
         {"Service/StrmSyncService.Cleanup.cs": cleanup_file,
          "Service/StrmSyncService.cs": ""},
         '{"stryker-config": {"mutate": ['
         '"**/Service/StrmSyncService.Cleanup.cs", '
         '"!**/Service/*.cs"]}}',
         False),
        ("a bracket-class exclusion of the cleanup file is rejected",
         {"Service/StrmSyncService.Cleanup.cs": cleanup_file,
          "Service/StrmSyncService.cs": ""},
         '{"stryker-config": {"mutate": ['
         '"**/Service/StrmSyncService.Cleanup.cs", '
         '"!**/Service/*.[cC]s"]}}',
         False),
        ("a cleanup method moved back to the main service file is rejected",
         {"Service/StrmSyncService.Cleanup.cs": cleanup_file,
          "Service/StrmSyncService.cs": "        private int CleanupOrphans(\n            string rootPath)"},
         covered, False),
        ("a cleanup method missing from the cleanup file is rejected",
         {"Service/StrmSyncService.Cleanup.cs": cleanup_file.replace("RemoveExcludedContent", "SomethingElse"),
          "Service/StrmSyncService.cs": ""},
         covered, False),
        ("a call to a cleanup method from the main service file is accepted",
         {"Service/StrmSyncService.Cleanup.cs": cleanup_file,
          "Service/StrmSyncService.cs": "            removed += CleanupOrphans(root, paths);"},
         covered, True),
    ]
    for name, sources, config, expected in range_cases:
        actual = not guard.find_mutation_gaps(sources, config)
        if actual != expected:
            failures.append(
                f"  {name}: expected {'accept' if expected else 'reject'}, "
                f"got {'accept' if actual else 'reject'}")

    if failures:
        print("delete-site guard self-test FAILED:\n" + "\n".join(failures))
        return 1

    print(f"delete-site guard self-test: {len(CASES) + 1 + len(range_cases)} cases passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
