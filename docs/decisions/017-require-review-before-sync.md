# ADR-017: Hold Un-Reviewed Titles Out of the Sync

**Date**: 2026-08-27
**Status**: ACCEPTED
**Affects**: `PluginConfiguration.RequireReviewBeforeSync` (new), `StrmSyncService.SyncMoviesAsync`, new helpers `DeserializeIdSet` / `SerializeIdSet` / `BuildLibraryIdentityIndex`

---

## Context

The sync's contract has always been: **anything not excluded is written**. `Excluded*` is a
blocklist the sync reads; `Reviewed*` is a triage bookmark the sync ignores entirely —
before this change the string `Reviewed` did not appear anywhere in `Service/`.

That works while a provider's catalogue is stable. It stops working when the provider adds
content in bulk.

## Problem

Measured on a live account, 2026-08-27: the movie catalogue went from 31,206 to 37,180
titles overnight — **5,974 arrivals**, of which 5,678 carried TMDB IDs the user had never
seen. None were excluded, so all of them would sync on the next run. The user curates
aggressively (roughly 93% of the movie catalogue is blocklisted), so the overwhelming
majority of any such batch is unwanted, and the library would absorb thousands of titles
before they could triage them.

Working around it with the existing machinery does not hold:

- **Bulk-exclude the arrivals.** Excluding implies reviewed (`isTitleReviewed` returns true
  when a title is in the reviewed set *or* excluded), so the queue empties and the titles
  vanish from the unreviewed worklist — the opposite of what triage needs. It also makes
  `RemoveExcludedContent` delete the folder of anything that *was* wanted.
- **Exclude, sync, then un-exclude via a config edit.** Works once, but any scheduled sync
  in the gap writes everything, and there is no way to express "undecided" in the meantime.

The missing concept is a title that is neither wanted nor blocked: **held**.

## Alternatives considered

1. **Key exclusions on TMDB ID** so prior decisions carry across the provider's ID churn.
   Rejected on measurement: of the 5,974 arrivals, TMDB keying would have covered **110
   (1.8%)**. It solves ID churn, which the same day's measurements showed is not the problem —
   `ExcludedSeriesIds` is 0.3% dead and every dead movie link was a title that came back.
2. **Prune redundant categories** so the arrivals never appear. Rejected: the overlapping
   categories are three different providers consolidated behind Dispatcharr, so dropping one
   drops that provider's coverage. That is the reason for running Dispatcharr at all.
3. **A periodic script that bulk-excludes new arrivals.** Workable, and it was built
   (`scripts/analyse-new-arrivals.py` classifies them), but it inherits the excluding-implies-
   reviewed problem and has to be remembered and run.
4. **Hold un-reviewed titles at sync time.** Chosen.

## Decision

`RequireReviewBeforeSync`, **off by default**. When on, a movie that is neither reviewed nor
excluded is skipped rather than written, and counted as skipped.

**Held is not excluded.** Nothing is added to a blocklist, no folder is removed, and the
title keeps showing as un-reviewed so it stays in the triage worklist. Reviewing it (or
excluding it) makes the next sync act on it.

### The exemption, and why it is load-bearing

A title **already on disk** is exempt: it syncs, and its StreamId is added to the reviewed
set. Without this, a provider reassigning an established film's ID would make it look
un-reviewed and quietly withhold a film the user already keeps.

Identity comes from the library itself. `BuildLibraryIdentityIndex` walks the STRM tree once
per run and collects two markers from folder names: the TMDB ID in a `[tmdbid=N]` suffix,
and the ID-stripped folder name. This is a record of past *keep* decisions that outlives the
provider IDs those decisions were stored against — no new config field, no migration, and it
reaches back further than any catalogue snapshot.

Both markers are collected deliberately. A TMDB-only index would miss folders written before
`EnableTmdbFolderNaming` was switched on, and those are precisely the titles that must not be
held (see below).

### Two safety rules

1. **Holding must never mean excluding.** Exclusion triggers `RemoveExcludedContent`, which
   deletes the folder with no ratio guard.
2. **A held title must never have files that orphan cleanup can reach.** Held titles are not
   added to `writtenPaths`, so cleanup would treat their `.strm` files as stale and delete
   them. This is satisfied *by construction* rather than by an extra whitelist pass: anything
   on disk matches the identity index and therefore takes the exempt path. That is the whole
   reason the index matches on stripped folder name as well as TMDB ID.

### Failing open

An unreadable `ReviewedVodStreamIdsJson` disables the gate for that run, with an error in the
log. `DeserializeIdSet` returns `null` for unparseable input and an empty set for genuinely
empty input, so the two are distinguishable. Reading "I could not parse this" as "nothing is
reviewed" would withhold the entire catalogue on the strength of a field we failed to read —
the same swallow-and-overwrite shape that makes `parseReviewedSet` in `config.js` dangerous.

## Consequences

- A provider's bulk additions land in the review queue instead of the library. The user
  decides what enters, at their own pace, with no folder churn either way — holding writes
  nothing, so reversing a decision costs nothing.
- The reviewed checkpoint becomes **self-maintaining**: a re-addition under a new ID is
  recognised, synced, and recorded, so ID drift heals instead of accumulating.
- A re-addition of an *excluded* title is simply never synced, so no re-exclusion work.
- **A second provider's entry for a film already kept is auto-approved on the following run.**
  Not predicted, observed in testing: StreamId A is reviewed and written, creating a folder with
  `[tmdbid=T]`; StreamId B is the same film from another provider and un-reviewed, so the first
  run holds it — but the next run's index contains T, so B is exempted, synced and recorded.
  Judged correct: B is another source for a title the user wants, not new content, and holding
  it would withhold provider redundancy that is the entire point of consolidating through
  Dispatcharr. It does mean the reviewed set grows by the number of duplicate entries among
  kept titles, which is bounded and self-limiting (each is recorded once).
- Orphan cleanup still runs while titles are held. That is safe under rule 2, with one narrow
  residual: a title whose folder carries no `[tmdbid=]` **and** whose provider name has since
  changed matches neither marker. Such a folder is already orphaned by the rename itself,
  with or without this feature, so the gate does not create the exposure — but it does mean
  the title is not restored under its new name until reviewed.
- The gate is movies-only for now. Series will use the folder's cleaned name rather than TMDB,
  since series carry no TMDB ID on the `get_series` list payload (measured: 0 of 9,979).
- `SyncMoviesAsync` now reads and writes `Reviewed*`, which it never did before. The write is
  additive only — the gate never marks anything un-reviewed.
- **New staleness in the de-dup view, and this feature caused it.** `instance.reviewedVodStreamIds`
  is populated once at page load (`config.js` ~488); the view's **Load** button re-fetches titles
  but not the config. So a page left open across a sync judges fresh titles against a pre-sync
  reviewed set, and every auto-reviewed title still shows "mark reviewed" until the page itself is
  reloaded. Harmless but confusing — it cost a debugging round during testing. Previously
  impossible, because nothing but the UI ever wrote the reviewed set. Worth having `loadDeduped`
  re-read the configuration alongside the titles; deferred to the UI-toggle change.
- Tests: 7 integration cases covering off-by-default, held-not-excluded, the TMDB exemption,
  the stripped-name exemption, failing open on an unparseable store, and an on-disk un-reviewed
  title surviving orphan cleanup with the guard disabled.

## Implementation references

- `Emby.Xtream.Plugin/PluginConfiguration.cs` (`RequireReviewBeforeSync`)
- `Emby.Xtream.Plugin/Service/StrmSyncService.cs` (`DeserializeIdSet`, `SerializeIdSet`, `BuildLibraryIdentityIndex`)
- `Emby.Xtream.Plugin/Service/StrmSyncService.cs` (`SyncMoviesAsync` — gate setup, the gate, the write-back)
- `Emby.Xtream.Plugin.Tests/SyncMoviesIntegrationTests.cs` (review-gate section)
