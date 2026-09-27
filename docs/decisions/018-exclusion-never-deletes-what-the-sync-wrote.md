# ADR-018: An Exclusion Never Deletes a Folder the Sync Just Wrote

**Date**: 2026-09-27
**Status**: Accepted (amends ADR-012)
**Affects**: `StrmSyncService.RemoveExcludedContent`, both sync call sites, `stryker-config.json`, `scripts/check-delete-sites.py`

---

## Context

ADR-012 added per-item exclusion: the catalogue is split by stream ID, and after the write
loop `RemoveExcludedContent` removes an excluded title's existing folder. That pass finds the
folder by cleaned name, ignoring case, with any `[tmdbid=…]` suffix stripped.

Found and fixed first in andyj682/emby-xtream-dedupe (ADR-F007 there): a provider listed two
entries whose names differed only in case. One was kept, the other excluded. Every sync wrote
the kept one's folder and then deleted it, because the excluded name matched it. The kept
title was permanently missing, and the only trace was one added and one deleted file in the
counters.

Case is only one way to collide. `SanitizeFileName` removes invalid characters and collapses
whitespace, so different names can clean to the same folder too.

## Alternatives Considered

### 1. Case-sensitive folder matching

**Rejected**: it only covers the case collision, and on a case-insensitive filesystem (macOS,
Windows) the two entries already share one folder, so it fixes nothing there.

### 2. Delete only STRMs whose URL holds the excluded stream ID

Precise for movies, where the URL ends in the stream ID.

**Rejected**: episode URLs carry episode IDs, not the series ID, so it cannot work for series.

## Decision

`RemoveExcludedContent` receives `writtenPaths` and skips any folder that holds, or is an
ancestor of, a path this run wrote or kept. Episodes sit in season folders, so ancestors up to
the library root count.

That protection only covers titles in `writtenPaths`, so every way a kept title can miss being
written now records it anyway:

- **A category fails to load.** Its titles are never seen, so the exclusion pass is postponed,
  with a warning, until a sync that loads every category (the same signal as ADR-013).
- **One item fails** (a movie write, a series detail fetch or a series write). Its catch block
  adds the files an earlier sync left for it to `writtenPaths`. An empty episode list already
  did this. Orphan cleanup is unaffected, because it does not run on a sync with a failed item.

Item failures deliberately do not postpone the pass. A failed item stays in the retry list
until someone retries it, so gating on it would stop every exclusion on every sync, which is
the "filter does nothing" outcome ADR-012 exists to avoid.

## Consequences

- Keeping one entry and excluding its twin now works for movies and series. Excluding both
  still removes the folder.
- When a category fails to load, excluded titles stay on disk until the next sync that loads
  every category.
- On a case-sensitive filesystem (most Docker installs) both twins can already have their own
  folder from before the exclusion, `Twin Title` and `twin title`. The folder index keeps one
  of them per name ignoring case, and the written-folder check also ignores case, so the
  excluded twin's folder can be left in place. That leaves a stale copy behind rather than
  losing data, which is the safe direction.
- `ExclusionCollisionTests` covers both collisions, both failed-category cases, a failed movie
  write and a failed series detail fetch for the kept twin, each shown to fail without the fix.
  Two controls check that an exclusion still deletes with no collision and while an unrelated
  item is failing.

### The mutation job was not testing these methods

While checking this change, the Stryker range in `stryker-config.json` turned out to be stale:
code added above the delete methods had moved them out of `StrmSyncService.cs{1992..2243}`. The
range is corrected, and `check-delete-sites.py` now fails CI when the range stops covering
`RemoveExcludedContent`, `CleanupOrphans` and every delete call in the file.

This does not yet make the job effective. Stryker reports every mutant in `StrmSyncService.cs`
as a compile error (`CS0165` definite-assignment failures in unrelated methods trigger its
fallback mode), so the reported 95% comes from `StrmOwnership.cs` alone, both locally and in CI.
That needs its own fix.

**Resolved (issue #75):** the delete methods and their helpers moved to
`StrmSyncService.Cleanup.cs`, a separate file of the same class, and Stryker mutates that whole
file instead of a line range. The first run that actually reached this code scored 59.75%; tests
for the threshold limits, emptied-folder removal and the deleted count
(`OrphanCleanupBoundaryTests`) brought it to 77.6%, above the 75% break. `check-delete-sites.py`
now requires every delete call of the sync service to stay in that file.
