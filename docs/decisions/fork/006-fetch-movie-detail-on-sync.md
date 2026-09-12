# ADR-F006: Fetch Movie Detail for Titles We Sync

*(Fork ADR. Numbered in the fork's own `F` sequence so it can never collide with an
upstream ADR — see [README.md](README.md).)*

**Date**: 2026-09-11
**Status**: PROPOSED
**Affects**: `StrmSyncService.SyncMoviesAsync` (a new per-title call after the review
gate), `PluginConfiguration` (one new opt-in field), `README.md`
**Depends on**: ADR-F004 stage 3, which must be shipped and running first — see
"Ordering" below. That is now met.

---

## Context

This is the only step in a three-step plan that this plugin owns, and the plan does not
pay for itself when judged inside this plugin. Read the goal first or the trade looks
wrong.

**The goal: actionable quality metadata for most movies** — resolution, audio codec, and
similar — so that stream-selection tooling can rank movie streams the way it already
ranks series streams.

**The asymmetry that creates the problem:** providers supply this metadata *consistently
for series* and *inconsistently for movies*. Series stream selection works today. Movie
stream selection is effectively inert for want of data, and no amount of work on the
selection side fixes that, because the data is not there to select on.

Three steps close it:

1. **This plugin calls `get_vod_info` for the movies it syncs**, which triggers the
   proxy's detailed refresh and harvests whatever the provider actually has.
2. **Gaps remain, and that is expected.** Provider coverage is wildly uneven: one
   provider returns a full `ffprobe` block on ~99% of payloads and no TMDB IDs at all;
   another returns TMDB IDs and no stream data whatsoever. `side_data_list` — the Dolby
   Vision marker — has never appeared in any provider payload measured, 0 of 959.
3. **A proxy-side self-`ffprobe` pass fills the remainder**, scoped to the movies we sync.

**Our actual contribution is the scoping signal, not the data.** Nothing in the proxy
records which movies anyone wants, so an untargeted `ffprobe` pass would have to cover the
whole catalog — 37,544 titles on the install this was measured against. The
reviewed-and-included set is ~2,397, about one fifteenth of it. That is what makes step 3
a tractable job rather than a theoretical one, and it is the thing being bought here.

**Do not re-litigate this on "does it pay for itself for the STRM generator" grounds.**
The direct return here — duplicate merging, possibly some TMDB tagging — is a nice
accident. The justification is the goal above. Judge this on whether it advances quality
metadata for movies, on its risk, and on its ordering constraint.

## The call

`GET /player_api.php?username=…&password=…&action=get_vod_info&vod_id=<StreamId>`, once
per synced movie. `vod_id` is the XC stream ID the plugin already holds — the proxy
advertises `Movie.id` as the stream ID, so no new identifier is needed. The call is gated
to roughly 24 hours per relation on the proxy side, so repeat calls are cheap.

## The question that had to be answered first

**Does a detail refresh bump the movie's `added`?**

The movie delta is `movie.Added > LastMovieSyncTimestamp`. This is the series trap
exactly: for series, `get_series_info` triggers a refresh, `last_modified` means "last
refreshed" rather than "changed", and that poisoned series delta-sync — 871 series
fetched to write 9, roughly 98% waste, and it took a per-series episode-ID hash to
recover a real change signal. **If `added` moved on refresh, `SmartSkipExisting` would
break and every sync would rewrite the whole movie library.** Movies use a different
field, so it was *probably* safe; the series case is precedent for assuming nothing.

**Answered: no. `added` does not move.** Settled by reading the proxy's source rather
than by probing, which is the stronger method here — a probe cannot distinguish "does not
bump" from "the 24-hour gate swallowed my call", and it would have had to churn a row to
find out. Four links, each verified in code at the version running:

1. The `get_vod_streams` payload emits `added` from the movie row's `created_at`.
2. That column is `auto_now_add`, written on INSERT only, and the model has no custom
   `save()` override.
3. The refresh task writes description, rating, genre, duration, year, TMDB/IMDB IDs and
   custom properties, plus two fields on the relation. It never assigns `created_at`. It
   does bump `updated_at`, which no XC payload reads.
4. The ID-conflict handler preserves the movie it was called for on every return path.

So `added` genuinely means "when this row entered the catalog", not "when it was last
refreshed". The delta stays truthful and `SmartSkipExisting` is unaffected.

## Ordering: ADR-F004 stage 3 is a hard prerequisite

Bulk detail fetching **deliberately induces ID churn**, and the shape of it matters.

When the detail payload supplies a TMDB ID that another movie row already holds, the
proxy merges them: it transfers the second row's relations onto the first and **deletes**
it. Reading that code settles the direction, which is the opposite of the obvious guess:

- The row looked up **by** the TMDB ID — the one that **already had it** — is the one
  deleted.
- The row we called on is the survivor, receiving the TMDB ID it lacked.

Three consequences, and they are why the ordering is not negotiable:

- **The title we called on keeps its ID, its URL and its `.strm`.** We do not break what
  we just synced.
- **A different row's ID dies.** Without stage 3 that silently detaches any decision
  stored against it, and the title reappears in the review queue — the exact damage of
  the 2026-09-09 re-ingest, induced deliberately. With stage 3 the consolidation is
  transparent: the deleted row **had** a TMDB ID, so a stage-3 pair exists for it and the
  decision re-points on the next sync.
- **It is also the cure for the duplicate-row problem**, where one copy of a film is
  excluded and the other is not, and the sync writes a folder that excluded-content
  cleanup then deletes by name, every run. Merging collapses the pair.

The watermark cannot move as a result: deleting rows only removes `added` values from the
set the high-water mark is taken over, and a maximum never rises from a deletion.

## Decision

**Call `get_vod_info` once per movie the sync actually writes, behind an opt-in setting,
throttled through the existing sync concurrency limit.**

- **Scope it to titles we write, not the whole catalog.** The review gate already
  computes "titles the user wants". Held titles are by definition not yet wanted, and
  calling for them would both waste the call and churn rows for content nobody asked for.
  This is the scoping signal from the Context, and it is also the cheaper option — ~2,397
  calls rather than 37,544.
- **Opt-in, default off.** The first run adds real time (below), and the call has a
  deliberate side effect on someone else's database. A setting that silently makes
  everyone's first sync 20 minutes longer and merges their movie rows is not a reasonable
  default, however good the end state is.
- **Throttle, do not fan out.** The call is synchronous and inline on the proxy side
  despite being declared a task, and takes seconds on a cold title. Reuse the existing
  sync concurrency semaphore (default 3) rather than adding a second concurrency domain.
- **Never write the proxy's own bookkeeping fields.** This plugin does not write to the
  proxy at all, and that stays true: the refresh timestamps are set by the endpoint we
  call, not by us. Costs nothing to commit to.
- **Failures are non-fatal.** A detail call is an enrichment for another tool, not part of
  writing a `.strm`. A failed call must not fail the title, and must not touch the
  watermark or any counter that means "the sync did not write this".

## Alternatives considered

1. **Do nothing.** Movie stream selection stays inert indefinitely — no other component
   is positioned to supply the demand signal. Rejected: this is the only step that
   unblocks the other two.
2. **Call for the whole catalog.** 37,544 titles rather than ~2,397, hours of added sync
   time, and it induces merges for titles nobody wants. Rejected: strictly worse on cost
   and on risk, with no benefit — the goal needs the *wanted* set, not every set.
3. **Have the proxy run an untargeted `ffprobe` pass instead.** Removes us from the
   picture entirely, and fails for the reason in the Context: without a demand signal the
   pass has to cover the whole catalog. Rejected as the primary route, but it remains step
   3 *scoped by* this one.
4. **Derive quality metadata ourselves from the stream URL.** Rejected: it would mean
   probing provider streams from the plugin, which is a different and much heavier
   responsibility, and duplicates a capability the proxy already has.

## Consequences

- **First run costs roughly 20 minutes** at ~2,397 titles and ~0.5s each; cheap after
  that, since the proxy's ~24-hour per-relation gate makes repeat calls near-free.
- **One call refreshes one relation**, the highest-priority one — so this never yields
  complete coverage across providers, by design. It is a sampling of the best relation,
  not an exhaustive sweep.
- **Some movie rows will be merged and their IDs will die.** This is intended, it is the
  duplicate fix, and stage 3 is what makes it survivable. Expect the first enabled run to
  report re-pointed decisions.
- **The hoped-for TMDB tagging of ID-less rows is measured near zero** for the provider
  that matters: 38 probed ID-less relations returned zero detail TMDB IDs. Do not count
  this as a benefit.
- **Dolby Vision specifically cannot come from this step** — `side_data_list` has never
  appeared in a provider payload, 0 of 959. It can only come from step 3.
- The returns land in sibling tooling by design. That is the point, not a flaw.

## Open

- Whether the setting should also gate on the review gate being enabled. With the gate
  off, "titles we write" is the whole included catalog, which is closer to alternative 2's
  cost profile than to this decision's. Leaning toward requiring the gate, or warning
  when it is off.
- Whether to log a per-run summary of what detail was harvested. It would be useful
  evidence that the step is doing anything, but this plugin does not consume the data, so
  it can only report that calls were made.

## Implementation references

- `Emby.Xtream.Plugin/Service/StrmSyncService.cs` (`SyncMoviesAsync`, after the review
  gate and after the smart-skip probe, so a skipped title costs no call)
- `Emby.Xtream.Plugin/PluginConfiguration.cs` (one new opt-in field)
- [ADR-F004](004-survive-provider-id-churn.md) (stage 3, the hard prerequisite)
- [ADR-F002](002-require-review-before-sync.md) (the review gate, which defines "wanted")
