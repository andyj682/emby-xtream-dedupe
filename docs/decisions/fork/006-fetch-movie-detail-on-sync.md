# ADR-F006: Fetch Movie Detail for Titles We Sync

*(Fork ADR. Numbered in the fork's own `F` sequence so it can never collide with an
upstream ADR — see [README.md](README.md).)*

**Date**: 2026-09-11
**Status**: ACCEPTED — designed and measured, not yet built. Every open question is resolved,
including by the author of the consuming plugin (2026-09-15) and by measurement against live
data (2026-09-16). **The marker is `director` OR `cast`; `release_date` is never written and
must not be used.**
**Affects**: `StrmSyncService.SyncMoviesAsync` (a new per-title call after the review
gate), `PluginConfiguration` (one opt-in field), `README.md`
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

- **Scope it to the *wanted set*: every title that clears the review gate, whether or not
  its `.strm` was rewritten this run.** Held titles are by definition not yet wanted, and
  calling for them would waste the call and churn rows for content nobody asked for. This
  is the scoping signal from the Context, and the cheaper option — ~2,400 calls rather than
  37,544.

  **"Titles we write" is not the same as "titles written this run", and the difference is
  load-bearing.** In steady state this sync writes approximately nothing: a representative
  run reported `0 written, 3531 skipped`, because everything wanted is already on disk and
  smart-skipped. Scoping the call to titles actually written would mean that **enabling the
  setting on an established library harvests nothing at all** — only newly-added titles
  would ever be called for, and the existing library, which is the entire point, would
  never be covered. The wanted set is the union of written and smart-skipped titles.
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

- 🔑 **Decide what to call for from the ABSENCE OF STORED DETAIL, never from our own record
  of what we have already called for.** This is the single most important constraint in the
  design, and it came from the consuming plugin's author (see "Resolved" below).

  Relation rows churn wholesale — one provider's entire set of ~31,470 was deleted and
  recreated twice in a single week, with fresh primary keys each time. The stored detail
  goes with them. **A client that remembers "I already called for this title" would then
  silently skip precisely the titles that just lost their data**, and the gap would be
  invisible from either side. Keying on absence instead makes churn self-healing.

- **Do not call more often than the proxy's gate, and do not mistake a gated call for a
  cheap one.** Confirmed from the proxy's source: `xc_get_vod_info` invokes the refresh only
  when the relation has never been fetched, has no refresh timestamp, or was last refreshed
  more than 24 hours ago — and the refresh task re-checks the same condition itself. The
  timestamp is written **inside** that guarded path, so a call within the window is a
  complete no-op for bookkeeping.

  🚨 **The corollary is the opposite of the intuition, and an earlier draft of this ADR had
  it wrong.** A periodic full pass at any interval *above* 24 hours does not produce mostly
  gated no-ops: the previous pass is what wrote the timestamp, so **by construction
  essentially every call clears the gate and performs a real provider fetch.** A daily pass
  over ~2,400 titles is ~2,400 real inline detail fetches per day, not a cheap re-stamp. For
  scale, the heaviest night the consuming plugin's own sweep has ever run was 406. This is
  why there is no periodic re-harvest.

## Alternatives considered

1. **Do nothing.** Movie stream selection stays inert indefinitely — no other component
   is positioned to supply the demand signal. Rejected: this is the only step that
   unblocks the other two.
2. **Call for the whole catalog.** 37,544 titles rather than ~2,400, hours of added sync
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

- **The first enabled run is the expensive one**, because that is when most of the wanted set
  still carries no detail — measured at **97%** of it. At ~2,325 titles and a measured mean of
  **1.02s** per call (median 0.65s, max 4.17s), that is **~13 minutes of added sync time at the
  default concurrency of 3**, or ~40 serial. Afterwards it drops to the residue below, because
  titles that carry the marker are filtered out by a field test before any request is made.
- **Steady state is ~310 calls per run**, the titles whose providers supply no director or
  cast, so the marker can never flip for them. Bounded and far below the ~2,400 per run that
  was rejected — and those calls still refresh the row and still stamp the demand signal, so
  they are invisible rather than wasted.
- **A gated call is not a free call.** The proxy's 24-hour gate suppresses the refresh
  *work*, not the request: the HTTP round trip still happens. This is why the design does
  not simply call for the wanted set every run and lean on the gate to make it cheap.
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

## Resolved, 2026-09-15

### Warn when the review gate is off; do not require it

**The cost argument for requiring it was overstated and is corrected here.** The earlier
draft said that with the gate off, the scope becomes "the whole included catalog, closer to
alternative 2's cost profile" — implying ~37,544. That conflates the *catalog* with the
*included* set. On a curated install they are nothing alike: 37,553 total minus 34,022
excluded leaves **3,531 included**, which splits into 2,398 written and 1,133 held for
review. So turning the gate off takes the scope from ~2,400 to ~3,500 — about 1.5×, not
15×. The 37,544 figure only describes someone who has excluded nothing.

The stronger argument was never cost but **signal quality**: the review gate is a positive
statement of demand, whereas "not excluded" is only the absence of rejection. But that
distinction also collapses under curation — someone who has excluded 34,022 titles has
expressed demand just as clearly, by a different route. **The signal tracks how curated the
install is, not which mechanism did the curating.**

Requiring the gate would therefore force an unrelated behavioral change — held titles, a
review queue — on a user who curates by exclusion alone and already has a good signal, in
order to solve a cost problem they do not have. So: **the setting works independently, and
the sync logs the scope before spending it**, naming the count and whether the gate was on.
An uncurated install sees a five-figure number in the log before the calls are made rather
than after.

### Log counts only, not payload coverage

A per-run line reporting calls made and calls failed. **Not** a breakdown of what the
payloads contained.

Inspecting payloads was considered — counting how many carried stream metadata or a TMDB ID
would measure the provider-coverage gap that step 2 says will remain, and the data is
already in hand. **It was rejected because the consuming tooling measures the same thing
better.** It sees the stored rows after merging, across all relations; this plugin would see
a single payload from a single relation at call time. Two "coverage" numbers that
legitimately disagree is the same failure as two incompatible log formats: it forces someone
to relitigate which is authoritative at exactly the wrong moment. One measurement, taken
where the data lives.

### Report progress while detail is being fetched

A run with many titles to fetch adds minutes to the sync with no other outward sign, and a
sync that appears stalled is indistinguishable from one that has hung.

The per-title counters already advance during the pass, because the call is made inline in
the existing loop rather than as a separate phase — so the progress bar keeps moving on its
own, just more slowly. What is missing is *why*. **The phase string says so during a
harvest run.** A separate phase was considered and rejected: it would need either its own
progress object or a deliberate reset of `Total`/`Completed`, and getting that wrong
corrupts the end-of-run summary for no gain over one honest string.

### Answered by the consuming plugin, 2026-09-15: no periodic re-harvest

The question put to it was whether it needs the refresh timestamp to be *recent* or only
*ever set*. The answer was **neither — it reads neither field.** As of its v1.2.0 it does
not write `detailed_fetched` or `last_advanced_refresh` and has never read them; its sweep
resumes on the presence of stored `detailed_info`, and its own bookkeeping lives under its
own key. So nothing shipping today depends on the answer either way.

The question was really about the future ffprobe pass, and the answer there is **do not buy
recency at that price**, for the cost reason recorded in the Decision above. In principle
recency is the better signal — a wanted set shrinks as well as grows, and a boolean can
never express "no longer wanted", so an ever-set marker makes the probe population only
accumulate. But ~2,400 real provider fetches a day is not the way to buy expiry.

🔑 **If expiry turns out to matter when the ffprobe pass is designed, the designated path is
to publish the wanted set directly** — a settings row or a file whose contents this plugin
already knows — which is always current and costs nothing recurring. That was previously set
aside as coupling cost, but that judgement predates anyone pricing the alternative. **Do not
reach for the timestamp again**; a small explicit contract is cheaper than a daily load.

### The detail marker: deciding what to call for, at zero cost

Keying on absence of stored detail is the constraint; this is how a client that cannot see
relation records satisfies it.

`xc_get_vod_streams` emits `director` and `cast` from the movie row's custom properties, and
`refresh_movie_advanced_data` writes exactly those two keys (`director`, `actors`) when the
provider's detail response supplies them. **So their absence in the list payload is a usable
marker for "this title has never had a detail refresh" — computed from a payload the sync
already fetches every run, at no additional request cost.**

⚠️ **Do not add `release_date` back.** The payload emits it, but nothing writes it to the
movie row, so it is empty on every title in the catalogue. **A field appearing in the emitter
says nothing about anything populating it** — check the writer, not the reader.

That removes the harvest timestamp, the bootstrap concept and the manual procedure all at
once. The rule becomes simply: *call for wanted titles whose list payload carries no
detail.* On first enable that is most of the wanted set; afterwards it is new arrivals plus
anything whose detail has genuinely gone.

✅ **MEASURED ON REAL DATA 2026-09-16, and the marker works — with one field removed.**

**`release_date` is out.** Populated on **0 of 37,561** movies. The refresh never writes it to
the movie row at all, so it can never be part of the marker. It was in the first draft because
it appears in the list payload's emitter — a field being *emitted* says nothing about anything
*writing* it. **The marker is `director` OR `cast`.**

**Current state of the wanted set:** 2,397 titles, of which **2,325 (97%) carry no marker** —
as expected, since nothing has ever called `get_vod_info` here.

**What a real call changes** (30 titles sampled from the unmarked population):

| | count | of 30 |
| --- | --- | --- |
| detail response carried director/cast | 26 | 86.7% |
| listing then showed the marker | 26 | 86.7% |

🔑 **The two figures are identical, which is the important part: there were ZERO cases where the
provider supplied the data and the listing failed to show it.** The mechanism is exact. Every
miss is provider silence, which no change on either side can fix.

🔑 **AND THE FAILURE DIRECTION IS THE SAFE ONE.** A silent provider leaves the marker empty, so
the title is called again — costing a request. It can never cause a title that *needs* detail to
be **skipped**, which would be a silent coverage gap. Over-calling is the error to prefer, and
the marker only makes that one.

**Cost, measured rather than guessed.** Per call: median 0.65s, mean 1.02s, max 4.17s — a long
tail of cold titles doing real provider round trips, and about double the 0.5s this ADR
originally assumed. First run over ~2,325 titles: **~13 minutes at the default concurrency of 3**
(~40 serial). Steady state: **~310 titles re-called every run**, the residue whose providers
supply no people.

⚠️ **That 13% residue is 4 misses in 30, so the real figure is roughly 90–720 per run.** The
decision holds across that whole interval — even the top end is far below the ~2,400 per run
that was rejected — so a larger sample would buy precision, not a different answer.

💡 **The residue is not wasted work.** Those calls still refresh the row and still stamp
`detailed_fetched` / `last_advanced_refresh`, which is the demand signal this whole plan exists
to produce. They are only invisible *to us*. If the per-run cost ever needs bounding, a
per-run call budget can be added without redesigning anything.

⚠️ **The original limits, for the record:**

1. **The marker reads the movie row, not the relation.** It is exact when a movie row is
   pruned and recreated — new stream ID, empty properties, and the title re-enters the
   wanted set as new regardless. It is **wrong in the narrower case where relations churn
   but the movie row survives** on another provider: movie-level `director` persists while
   the new highest-priority relation has no stored detail, so a title needing a re-call
   reads as done. This is the residual of the churn constraint above, and it is the one
   argument for a *long*-interval safety re-harvest — monthly, not daily.
2. ✅ **"Only useful if those fields discriminate" — now answered, above.** They do, for ~87%
   of titles. The residue is real but bounded, and it fails by calling too often rather than
   too rarely. **The concern was correct to raise and the measurement is why this is a
   decision rather than a hope**; `scripts/measure-detail-marker.py` and
   `scripts/probe-detail-refresh.py` make it repeatable.
   ⚠️ **An empty marker is ambiguous between "never refreshed" and "refreshed, but this
   provider supplies no director".** That ambiguity is the residue, and it is why the marker
   can only ever over-call. Do not try to resolve it from the list payload — the unambiguous
   flags (`detailed_fetched`, `last_advanced_refresh`) live on the *relation*, and there is
   **no bulk relation endpoint** to read them from: only `movies`/`episodes`/`series`/
   `categories`/`all` are routed, so reading them means one request per title, which is not
   cheaper than simply making the call.

### Constraints from the consuming plugin, to honor when building

- **Key on `tmdb_id`, never on the movie ID or UUID.** These calls deliberately induce
  merges, and a merge makes a movie row disappear — the relation is re-pointed onto the
  canonical row and the freshly-minted duplicate is left relation-less and later pruned. Any
  cached proxy-side ID for that title breaks. ADR-F004 stage 3 already stores the pairs.
- **One call refreshes one relation**, resolved as
  `order_by('-m3u_account__priority', 'id').first()` — the single highest-priority account,
  not all of a movie's relations. A nine-relation movie gets detail for one. **Do not size a
  later probe pass as "whatever step 2 left over" without accounting for this.**
- **Do not run during the provider ingest window.** Not a correctness constraint: these
  calls are inline and would contend with ingest for the same provider connection slots.
  Scheduling the sync clear of the ingest and sweep windows is an operational note for the
  README, not a code change.
- **Keep not writing the proxy's bookkeeping fields.** Already committed to above. The
  reason is now sharper: those fields are the only record anywhere that a client asked for a
  movie's detail, and the entire ffprobe scoping design rests on that meaning staying
  uncontaminated.
- **Prerequisites are met.** The consuming plugin's destructive-merge protection and its
  clobber guard are both live in its deployed version. The clobber case specifically is
  covered: a refresh replaces stored detail wholesale, and it restores the TMDB and stream
  fields where the new payload left a hole, so these calls cannot silently cost it its
  detail-tier identity.

## Implementation design

All of it sits inside the existing per-title loop in `SyncMoviesAsync`, which is already
throttled by the sync concurrency semaphore. No new concurrency domain, no new pass.

**Configuration** — one field: `EnableMovieDetailFetch`, opt-in, default off.

🔑 **No persisted bookkeeping field.** An earlier draft of this section specified a
`LastMovieDetailHarvestUnix` timestamp driving a one-off bootstrap pass. **That is exactly
the pattern the churn constraint forbids** — it is our own memory of what we have called
for, and after a wholesale relation recreation it reports coverage that no longer exists.
It is recorded here as rejected so it is not re-derived; the detail marker replaces it and
needs nothing persisted.

**One call site**, in the loop body: **immediately after the review gate's hold decision**,
where a title is known to be wanted, and **before the smart-skip probe**. A title is called
for when the setting is on and its list payload carries no detail.

Placing it before the skip probe is what makes the scope the *wanted* set rather than the
*written* set. This **reverses the earlier implementation note**, which placed the call
after the probe "so a skipped title costs no call" — correct for steady state, and the
reason an established library would never have been covered at all. The marker is what keeps
the cost down instead: a skipped title that already has detail costs nothing, because the
check is a field test on data already in memory.

**Everything else:**

- Failures increment a private counter only, never `_movieProgress.Failed`, which means "the
  sync did not write this".
- Nothing is persisted about what was called for. A run that fails or is interrupted simply
  leaves those titles still showing no detail, so the next run retries them — the
  self-healing property is a consequence of keying on absence, not something to implement.
- The summary line is emitted only when calls were actually made, keeping ordinary runs
  silent — the same rule the collapse-group logging follows, and for the same reason.

**Testing.** The call is an HTTP request through the existing fake handler, so scope
selection, the detail-marker filter, the held-title exclusion and non-fatal failure handling
are all unit-testable with no network. The case most worth pinning is a title that is
smart-skipped but carries no detail: it must still be called for, since that is the whole
reason the call site sits before the skip probe. ⚠️ **Register one response per expected
call** — the fake handler's single-response registration is one-shot, and a per-title call
across a multi-title fixture will exhaust it otherwise.

## Implementation references

- `Emby.Xtream.Plugin/Service/StrmSyncService.cs` (`SyncMoviesAsync`: the review gate's
  hold decision, and the smart-skip probe — the two call sites in the implementation design
  above)
- `Emby.Xtream.Plugin/PluginConfiguration.cs` (the opt-in field and the harvest timestamp)
- [ADR-F004](004-survive-provider-id-churn.md) (stage 3, the hard prerequisite)
- [ADR-F002](002-require-review-before-sync.md) (the review gate, which defines "wanted")
