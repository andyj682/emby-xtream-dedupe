# ADR-020: Failed Series Are Retried by the Series Sync

**Date**: 2026-09-27
**Status**: Accepted
**Affects**: `StrmSyncService.SyncSeriesCoreAsync`, `StrmSyncService.RetryFailedAsync`, the retry endpoint

---

## Context

Two things were wrong with failed series.

1. **"Retry failed items" had its own series writer** (`RetrySeriesItemAsync`). It wrote under
   `Shows/<name>` whatever the folder mode, `Season 1` instead of `Season 01`, `S1E01.strm`
   without the show name, and none of the specials, multi-version or NFO handling. A retried
   series landed beside the copy the sync wrote.
2. **A failed series was never processed again.** The watermark moves past a series before its
   detail is fetched, so after a failed fetch it looks unchanged. With smart skip on and its
   folder on disk, every later sync skipped it without a fetch, and the reset of the failed list
   at the start of each series sync dropped it without a trace. An empty episode list for a show
   with files on disk counted as failed but was never put in the failed list at all.

## Alternatives Considered

### 1. Fix the retry writer's layout

**Rejected**: the writer would still be a second copy of about 300 lines of per-series logic
(folder modes, metadata-ID suffixes, specials, multi-version, NFOs), and it had already drifted.

### 2. Extract the per-series body into a method both paths call

**Rejected for now**: the body closes over a dozen pieces of run state (hashes, directory index,
watermark, progress counters, written paths). Extracting it is a large change to the most
delicate code in the plugin, for no gain over alternative 3.

### 3. A "retry only these series" mode of the series sync

**Rejected**: the sync has many run-wide side effects (history, naming upgrade, watermark,
exclusion removal, orphan cleanup) that would each need a guard in that mode.

## Decision

- The series sync forces every series in the failed list through again: both skip paths (the
  pre-fetch skip and the episode-hash skip) are bypassed for them.
- The failed list is only cleared once the catalogue has loaded, and a run that throws after that
  puts back the series it started with. A run that aborts or fails early therefore forgets
  nothing.
- A failed series missing from the catalogue stays in the list only when a category failed to
  load, since orphan cleanup does not run then. When every category loaded, the provider no
  longer lists it: it leaves the list and orphan cleanup treats it like any dropped series.
  Keeping it without protecting its files would have let cleanup delete them anyway.
- An empty episode list for a show with files on disk now adds the series to the failed list.
- "Retry failed items" retries movies as before, then runs a normal series sync if the list held
  any series. `RetrySeriesItemAsync` is removed. The retry already holds the series gate, so it
  calls `SyncSeriesCoreAsync` directly.

## Consequences

- A retried series is written exactly as the sync writes it, and failed series recover on the
  next scheduled sync without anyone pressing retry.
- A retry that includes series fetches the whole series catalogue and appears in history as a
  series sync, with everything a series sync does, including orphan cleanup where enabled.
- The retry endpoint reports, for the items it started with, how many are still failing. Its
  movie counters do not cover series, and the failed list can gain new series during the run.
- A retried movie still gets the plain provider URL even with Dispatcharr multi-version on, until
  the next sync. Movies keep their own retry writer; it produces the same paths as the sync.
