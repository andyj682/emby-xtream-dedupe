# ADR-F007: Never Delete a Folder the Sync Just Wrote

*(Fork ADR. Numbered in the fork's own `F` sequence so it can never collide with an
upstream ADR — see [README.md](README.md).)*

**Date**: 2026-09-12
**Status**: ACCEPTED
**Affects**: `StrmSyncService.RemoveExcludedContent` (one new parameter and one guard),
both sync call sites

---

## Context

Excluding one movie could permanently remove a *different* movie from the library, with
nothing in the log to explain it. Observed on a rig, reproduced deterministically across
consecutive syncs, and traceable back through months of logs.

The provider offered two catalog entries whose names differed only in capitalization —
`… with the …` versus `… With The …`. The proxy treats them as two distinct titles. One
was included and reviewed; the other was excluded.

Every sync then did this:

1. The exclusion filter splits the catalog **by StreamId**. The two entries have different
   IDs, so the included one passes and its folder is written.
2. After the write loop, `RemoveExcludedContent` matches **by name**: it indexes folders
   with `StringComparer.OrdinalIgnoreCase` after stripping any `[tmdbid=]` suffix.
3. The excluded entry's name matches the included entry's freshly written folder, and the
   folder is deleted.

The next run repeats it. The title is permanently absent despite being included and
reviewed; the only visible trace is a `+1 -1` in the counters, which is also the signature
of an ordinary rename.

**The root cause is upstream of this plugin.** These two rows should have been merged into
one by the proxy's own duplicate handling and were not. That remains the right place to fix
the data.

**But the plugin's contribution is its own.** It flattened a distinction the proxy makes.
The TMDB suffix does not protect the folder either — it is stripped before the comparison.
And nothing about the failure requires the two entries to be the same film: because the
match is name-only, two genuinely *different* films that sanitize to one folder name collide
identically. Waiting for better upstream data does not close that.

## The requirement

All four combinations must be expressible, because the two entries are independent titles
as far as anything the plugin can see:

| Intent | Before | After |
| --- | --- | --- |
| Sync both | works — distinct folder names, distinct files | unchanged |
| **Sync one, exclude the other** | **neither survives** | **the included one syncs and stays** |
| **Sync the other, exclude the first** | **neither survives** | symmetric |
| Exclude both | works | unchanged |

The middle two were impossible. That is the whole defect.

## Decision

**`RemoveExcludedContent` will not delete a folder that the current run wrote into.**

The sync already computes exactly the information needed — `writtenPaths`, every STRM path
written or deliberately kept — and `RemoveExcludedContent` runs after the write loop has
completed. It simply was not passed in. It now is, and a folder containing any written path
is skipped and logged instead of deleted.

Episode files sit under a season folder, one level below the show folder an exclusion
targets, so ancestors up to the library root count as written too.

### Why a run-scoped guard rather than a better name rule

The obvious narrower fix is to make the folder match case-sensitive, which would have
prevented this exact instance. Rejected on two grounds:

- **It only covers case.** `SanitizeFileName` strips invalid characters and collapses
  whitespace, so distinct names can still converge on one folder. The guard covers every
  collision regardless of how it arose.
- **It depends on filesystem case semantics.** On a case-sensitive filesystem the two
  folders coexist; on a case-insensitive one they are the same folder. A case-sensitive
  lookup could then fail to find a folder that genuinely needs removing.

More fundamentally: every name comparison is a guess about identity that some input
violates. *"The sync must not delete what it just wrote"* needs no such guess, and it stays
true whatever the provider does next.

The guard is surgical. A genuinely excluded title with no included twin writes nothing, so
its folder is absent from `writtenPaths` and is removed exactly as before — pinned by the
existing `ExcludedMovie_ExistingFolderDeleted_WithoutOrphanCleanup` test, which now doubles
as the over-reach guard.

### Why not give movies ADR-F001's propagation instead

[ADR-F001](001-collapse-group-exclusion-propagation.md) solves the series version of this by
propagating exclusion across the whole collapse group: any member blocklisted means all are.
Applying that to movies would also end the write/delete cycle — by removing the film
entirely.

That is correct for series and wrong for movies. A series collapse group is genuinely one
show, so "exclude this title" is the only meaningful action and propagation matches intent.
Two movie rows are, as far as the plugin can tell, two titles; the user must be able to keep
one and drop the other. Propagation would make row 2 of the table above inexpressible — it
would convert a silent erasure into a permanent, explicable one rather than fixing it.

### Series are unaffected, and provably so

The guard lives in the shared method, so both syncs pass through it, but it cannot fire for
series. The collapse key is `folder + " " + SanitizeFileName(cleanedName)` compared through
a `StringComparer.OrdinalIgnoreCase` set — **the same normalization the deletion index
uses**. Any series pair that could collide at deletion time is therefore already merged into
one collapse group by ADR-F001, excluded together, and never written, so no written folder
exists for the exclusion to match.

That is a structural argument, not an observation, and it is worth preserving: if the
collapse key and the deletion index ever stop sharing a normalization, series inherit this
bug immediately, and the guard is what would catch it.

## Consequences

- A title the user included stops disappearing. This is a bug fix with no configuration and
  no migration.
- The skipped deletion is logged at Info, naming the folder and the excluded title, because
  the underlying state is genuinely wrong and only the user can resolve it — by excluding
  both entries, or by getting them merged at the proxy. A run-level count follows.
- Orphan cleanup is untouched. It has its own partial-fetch guard and its own threshold;
  this changes only the exclusion pass.
- **Noted, not fixed:** `RemoveExcludedContent` has no partial-fetch guard, unlike
  `CleanupOrphans` immediately below it. A failed category makes a collision *more* likely,
  since the included twin is never written and so never protected. The guard narrows the
  window rather than closing it.
- **Noted, not fixed:** on a case-insensitive filesystem the two entries resolve to one
  folder and overwrite each other even when both are included. Pre-existing and separate.
- The eventual narrower fix is to stop matching on name in the deletion path at all and
  compare the `.strm`'s embedded StreamId against the exclusion list — the logic
  `scripts/audit-strm-links.py` already implements for both URL forms. That would make the
  pass identity-exact rather than name-approximate. It is a larger change and does not need
  to gate this one.

## Implementation references

- `Emby.Xtream.Plugin/Service/StrmSyncService.cs` (`RemoveExcludedContent` and both call sites)
- [ADR-F001](001-collapse-group-exclusion-propagation.md) (the series counterpart, and why
  movies deliberately differ)
- `scripts/audit-strm-links.py` (the StreamId-from-URL logic a future identity-exact pass would reuse)
