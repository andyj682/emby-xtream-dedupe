# ADR-016: Propagate Series Exclusion Across the Collapse Group

**Date**: 2026-08-26
**Status**: ACCEPTED
**Affects**: `StrmSyncService.SyncSeriesCoreAsync` (new shared `collapseKeyBySeriesId`, the per-item exclusion block, and the collapse representative pick), `Configuration/Web/config.js` (`healPartialExclusions` — unchanged, no longer load-bearing)

---

## Context

Per-item content exclusion (ADR-012) stores a flat list of provider IDs: `ExcludedVodStreamIds`
and `ExcludedSeriesIds`. For movies that is title-level by construction — Dispatcharr gives a
cross-listed movie one shared `StreamId`, so one ID covers every category it appears in.

Series are not. Dispatcharr issues a distinct `SeriesId` per (provider, category) for the same
show, so `ExcludedSeriesIds` records only the copies that existed when the user made the
exclusion.

## Problem

The **Path-A quirk**: enable a series category after excluding a show and the show comes back —
Dispatcharr hands out a fresh `SeriesId` for it in the new category, that ID is not on the
blocklist, and the sync writes it.

The de-dup view already compensates. `healPartialExclusions` (`config.js`) extends any *partially*
excluded title (some of its IDs blocklisted, not all) to cover all of them and prompts a save. But
it is client-side and runs on load, so the repair only happens if the user **opens the de-dup view
and saves**. A sync that runs unattended — the point of the scheduled task — re-syncs the show,
and nothing in the log says why.

## Alternatives considered

1. **Fix it in the UI only** — run the heal on load *and* auto-save. Rejected: still requires
   someone to open the plugin's config page. Does nothing for an unattended sync, which is the
   failure being fixed.

2. **Key exclusions on TMDB ID.** The route this ADR originally took, and rejected on measurement.
   `SeriesInfo.TmdbId` exists on the model, but the provider does **not populate `tmdb` on the
   `get_series` list payload — measured 0 of 9,979 series.** Series TMDB IDs come from the
   *detail* payload (`get_series_info` → `info.tmdb`, consumed at `StrmSyncService.cs:1486`), where
   coverage is ~99.5%. So a TMDB-keyed filter would need a persisted `SeriesId → TMDB` cache
   learned from the detail fetches the sync already makes, plus a one-sync lag for any ID never
   fetched before. All of that to solve a problem the collapse key already solves exactly.
   TMDB keying is also *folder-blind*, so one shared bad ID value could reach across genuinely
   separate folders — a strictly worse safety profile than option 4.

3. **A persisted TMDB key written by both exclusion UIs.** Necessary for a *different* problem —
   surviving wholesale ID reassignment, which a census showed is real (36% of stored movie IDs and
   51% of stored series IDs no longer exist in the catalogue). Deliberately out of scope here: it
   needs new config fields, a retraction rule, and TMDB-awareness in JS, and it does not fix
   Path-A any better than option 4.

4. **Propagate exclusion across the collapse group.** Chosen.

## Decision

The sync already computes, a few lines below the exclusion filter, a grouping that identifies
copies of the same show: the collapse key `(target folder + cleaned name)`. Path-A is purely an
**ordering** defect — the filter runs *before* that grouping exists, so the lone unblocked copy
survives into the collapse alone and is written.

Three changes, all inside `SyncSeriesCoreAsync`:

1. **Hoist the key.** `collapseKeyBySeriesId` is built once over the *full* fetched list, before
   the exclusion filter. Both the propagation and the collapse now read the same dictionary, so
   they cannot disagree about which copies are "the same show" — the invariant the safety argument
   rests on.

2. **Propagate.** Any group containing a blocklisted member is excluded wholesale:

```csharp
var excludedGroupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var s in fetchedSeries)
    if (ContentExclusionFilter.IsExcluded(excludedSeriesSet, s.SeriesId))
        excludedGroupKeys.Add(collapseKeyBySeriesId[s.SeriesId]);
// ...then, per series:
var byId = ContentExclusionFilter.IsExcluded(excludedSeriesSet, s.SeriesId);
if (byId || excludedGroupKeys.Contains(collapseKeyBySeriesId[s.SeriesId])) { /* skip + queue removal */ }
```

   Two passes are required: every excluded group must be known before filtering starts, or a copy
   listed ahead of its blocklisted sibling escapes.

3. **Deterministic collapse representative.** Independent fix, same block. The pick was first-wins
   over provider-ordered input; the episode hash is keyed on `SeriesId`, so a flipped representative
   had no stored hash, was delta-unchanged, pre-fetch-skipped, carried nothing, and stranded in the
   no-hash state (observed creeping 1 → 3 on live). Candidates are now ordered by *(has a stored
   episode hash, then lowest `SeriesId`)*; the hash term means an established representative never
   loses its place, even to a lower ID appearing later.

Group-propagated copies go down the *same* excluded-items path as directly named ones, so
`RemoveExcludedContent` removes their folders and their `LastModified` still folds into the delta
watermark.

## Why this is safe

**The propagation group is exactly co-extensive with the collapse the sync already performs.** Only
one member of a group — the representative — is ever written; the rest are dropped regardless of
exclusion. So widening an exclusion across a group cannot suppress anything that would otherwise
have appeared. That is a stronger guarantee than any identity-keyed scheme can offer, because it
derives from the same key that decides what gets written in the first place.

Two consequences fall out for free:

- **Multiple/Custom folder mode stays correct.** The key includes the target folder, so excluding a
  copy in one folder leaves another folder's copy alone. TMDB keying would not have done this.
- **No disagreement with the UI.** The de-dup view groups rows by name, which is what the collapse
  key reduces to in single-folder mode, so the two surfaces agree exactly.

## Consequences

- An excluded show stays excluded through a category being enabled later, on the next sync, with no
  UI visit and **no lag** — the group is built from data already in the list payload.
- `healPartialExclusions` still runs and is still correct, but is no longer load-bearing.
- A run where propagation caught something logs a second line naming the count, since "skipping N of
  M series" cannot show it.
- The collapse loop no longer recomputes the key, removing a duplicated name-clean and path-build.
  The precompute now runs over the full fetched list rather than the post-exclusion subset, which on
  a heavily-excluded catalogue is more work — string and dictionary operations over ~10k entries,
  immaterial next to the HTTP the same run performs.
- **Limitation, unchanged**: copies whose cleaned names differ ("WeCrashed" vs "We Crashed", a
  `4K-A+ -` prefix) are separate groups and still need excluding individually. That is the existing
  near-duplicate limitation, now pinned by a test.
- **Not addressed**: wholesale ID reassignment, where the blocklisted ID stops existing entirely.
  Measured at 36% of stored movie IDs and 51% of stored series IDs, concentrated in episodic bulk
  re-ingest events rather than continuous drift. That needs persisted identity keys (alternative 3)
  and is tracked separately.
- No new delete sites, so the ADR-014 guard is unaffected.
- Tests: 3 integration cases for the propagation (cross-listed copy under a new ID also excluded,
  including the watermark fold; different-name copy deliberately *not* propagated; re-inclusion
  taking effect) and 2 for the representative ordering. The folder-boundary property is not tested
  directly — there is no existing working Custom-mode series fixture to extend, and the property
  follows from the shared key rather than from separate logic.

## Implementation references

- `Emby.Xtream.Plugin/Service/StrmSyncService.cs:1139-1153` (shared `collapseKeyBySeriesId`)
- `Emby.Xtream.Plugin/Service/StrmSyncService.cs:1155-1222` (propagation + exclusion filter)
- `Emby.Xtream.Plugin/Service/StrmSyncService.cs:1224-1282` (hash cache hoist + representative pick)
- `Emby.Xtream.Plugin.Tests/SyncSeriesIntegrationTests.cs` (propagation + collapse-ordering sections)
