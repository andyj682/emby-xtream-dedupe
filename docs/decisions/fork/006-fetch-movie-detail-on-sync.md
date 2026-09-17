# ADR-F006: Fetch Movie Detail for Titles We Sync

*(Fork ADR. Numbered in the fork's own `F` sequence so it can never collide with an
upstream ADR — see [README.md](README.md).)*

**Date**: 2026-09-11
**Status**: 🚫 **WITHDRAWN 2026-09-16 — built, measured, and withdrawn before shipping.** Every
open question was resolved and the implementation was complete and passing (644/648) when
measurement showed the call has no consumer. The code was discarded; this record is kept
because the measurements are the valuable part and they say precisely what would have to
change for the answer to differ.

**Do not rebuild this from the design below without reading "Why it was withdrawn" first.**
The design is sound and the code paths described are real — that is exactly the trap. What
fails is not the mechanism but the premise that anything consumes what it produces.

---

## Why it was withdrawn

Three benefits were claimed. All three were measured away, and the third only became visible
once the end goal was restated precisely.

1. **Demand signal → superseded.** The wanted-set file (below) carries it losslessly, stays
   current, self-expires, and survives relation churn. The refresh timestamp does none of
   those under this ADR's own cadence.
2. **De-duplication → measured at zero.** 16 candidates chosen specifically to conflict
   produced no merges, because a row is ID-less precisely because its provider cannot
   identify it, so its *detail* endpoint has no ID either.
3. 🔑 **Metadata harvest → belongs to the consuming side, structurally.** The goal is video
   and audio data for **all the relations** of each wanted movie, because the consumer ranks
   candidates against one another and partial coverage is worse than none — a ranking
   function returning 0 for "no data" sorts an enriched stereo track above an unenriched
   possible-surround one. **`xc_get_vod_info` refreshes exactly ONE relation per movie**
   (`order_by('-m3u_account__priority','id').first()`), and that is structural in core, not
   configuration. The consuming plugin's sweep calls detail **per relation** directly against
   the provider, bypassing the priority pick. So it covers all N where this covers 1/N.

   That limitation was recorded in the first draft of this ADR as a caveat. **Nobody connected
   it to "therefore the harvest belongs to whoever can reach all N" for three weeks.**

**And nothing on this side consumes what the call uniquely produces.** Core's refresh writes
`director`, `actors`, `backdrop_path` and `youtube_trailer` to the *movie row*, which the
consuming plugin's sweep never touches — so those come from this call or from nowhere. But
`NfoWriter.WriteMovieNfo` emits only `<title>`, `<year>` and a TMDB `<uniqueid>`: a **pointer**,
not metadata. Combined with the `[tmdbid=]` folder suffix, **Emby scrapes TMDB itself** for
cast, director and artwork. A grep of the whole plugin for those fields matches only the word
"directory". Nothing reads `detailed_fetched` either.

**The last hope checked and closed:** a detail refresh writes `tmdb_id` to the movie row, which
appears on the *list* payload — which the review gate matches against library folders and which
ADR-F004 stage 3 stores as identity pairs. Better coverage there would have helped directly.
**Measured at ~zero across 54 observations** (0 of 16, plus 38 ID-less relations probed earlier
returning zero detail TMDBs).

**Why discarded rather than shipped dormant.** Opt-in and default off is a fair resting state
for an uncertain feature, and the argument against is not runtime cost — it is that the call
site sits **inside the upstream sync loop**. ADR-F001's work made that loop the one place fork
logic lives in upstream's write path, and the standing instruction at every upstream merge is to
diff `SyncMoviesAsync` first and confirm the insertion points still hold. A second insertion
there, for a feature with no consumer, is a tax paid at every future merge forever. A dormant
setting with an encouraging name is also a trap: someone enables it in a year on the strength of
the description without reading this.

**What would change the answer.** A provider whose *detail* endpoint returns TMDB IDs for
ID-less rows; a library where the consuming plugin's per-relation sweep is not available; or
Emby-side consumption of descriptive metadata that does not come from TMDB. None hold here.

**What was kept:** this record, `scripts/measure-detail-marker.py` and
`scripts/probe-detail-refresh.py`. The scripts are general diagnostics and are independent of
the feature — they answered questions it never could, including a timing correction to a figure
this ADR had been quoting.

---

**Original status line, for context:** *ACCEPTED — designed and measured, not yet built. The
marker is `director` OR `cast`; `release_date` is never written and must not be used.*
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

⚠️ **THE RISK THIS SECTION IS BUILT ON WAS MEASURED ON 2026-09-16 AND IS FAR SMALLER THAN
DESCRIBED — read this before weighing anything below.** The premise is that bulk detail
fetching deliberately induces ID churn at scale. **On this library it induces approximately
none**: 16 candidates chosen specifically to conflict produced zero merges, because none of
their detail payloads carried a TMDB ID (see the duplicate-row bullet). A merge needs an
ID-less row to *gain* a TMDB another row already holds, and the measurement says that input
does not arrive.

**Stage 3 stays a prerequisite anyway, and the ordering is unchanged** — it is already
shipped and running, so the requirement costs nothing, and the analysis below is exactly
what governs a library whose providers do supply detail TMDBs. **But do not cite this
section as evidence that the feature is dangerous here.** An overstated risk gets discounted
wholesale once someone notices it did not materialise, which would take the accurate parts
down with it.

⚠️ **And note the direction correction further down**: with the consuming plugin's guard
installed, the row that dies is the ID-LESS one, so stage 3 cannot recover decisions against
it. That makes stage 3 *less* protective here than this section implies, not more.

Bulk detail fetching **deliberately induces ID churn**, and the shape of it matters.

When the detail payload supplies a TMDB ID that another movie row already holds, the
proxy merges them: it transfers the second row's relations onto the first and **deletes**
it. Reading that code settles the direction, which is the opposite of the obvious guess:

- The row looked up **by** the TMDB ID — the one that **already had it** — is the one
  deleted.
- The row we called on is the survivor, receiving the TMDB ID it lacked.

Three consequences, and they are why the ordering is not negotiable:

🚨 **THE DIRECTION FLIPS DEPENDING ON WHETHER THE CONSUMING PLUGIN'S PROTECTION IS
INSTALLED, AND THE CONCLUSION BELOW WAS DRAWN FOR THE WRONG ONE. Settled against both
sources 2026-09-16.** The two bullets above describe **core unprotected**, and that reading
is correct — verified unchanged from v0.30.0 to v0.31.0. **With the protection installed —
which is the case on this install — the relation is re-pointed onto the tmdb-holder inside a
transaction and NOTHING is deleted.** The ID-less row we called simply loses that relation,
and is orphaned and pruned later if it had no others. **So the row that dies is the ID-LESS
one, the opposite of core's behaviour**, and these follow:

- 🚨 **Stage 3 CANNOT recover decisions against the dying row.** It stores `StreamId → TMDB`
  **pairs**, and a row with no TMDB has no pair by construction. **A quiet `Movie identity:`
  line after inducing these merges is therefore CORRECT, not a failure** — the earlier claim
  that this and stage 3 "compose correctly" holds only for core's unprotected direction,
  which nobody actually runs.
- ⚠️ **The title we called on does NOT reliably keep its ID, URL and `.strm`.** If it is
  orphaned and pruned, its stream ID dies with it and any `.strm` written for it breaks,
  until the canonical is reviewed and written under its own ID. Only bites titles that were
  in the library already; an excluded or un-reviewed one has no file to lose.
- ⚠️ **The returning duplicate may carry a NEW ID rather than the original.**
  `cleanup_orphaned_vod_content` deletes relation-less movies **globally** at the end of
  *any* account's refresh, so if the orphan goes before its own provider rescans,
  `lookup_by_name_year` finds nothing and core mints a fresh row. Which path a row takes
  depends on whether it had other relations.
- 💡 **For our recovery mechanism specifically, core's destructive direction is the more
  recoverable one** — a dead tmdb-holder has a pair; a dead ID-less row does not. The
  protection is still plainly the right trade, since losing an exclusion mark on a duplicate
  is far cheaper than deleting the canonical row with most of the relations and the UUID
  clients have indexed. **But the cost lands on us rather than on them.**

🔑 **THE PAIR IS NOT LOST, ONLY UNREACHABLE — do not write this up as impossible.** Every
protected merge writes an audit entry carrying `previous_movie_id`, `canonical_id`,
`canonical_uuid`, `tmdb_id`, `stream_id` and account — which **is** the `dying ID → TMDB`
mapping, recorded at the moment of the merge, for exactly the population stage 3 cannot
cover. It lives in a `CoreSettings` row and we speak XC, so it is out of reach today. If
recovering these is ever worth it, the natural shape is **the mirror of the wanted-set
file**: they write a merge-event file into the same exchange directory and we read it.
- 🚨 **MEASURED 2026-09-16: IT IS NOT A CURE FOR THE DUPLICATE-ROW PROBLEM AT ALL ON THIS
  LIBRARY. Not "scoped to one provider" — it does not happen.** 16 ID-less rows selected
  specifically as merge candidates were called; **none of the 16 detail payloads contained a
  TMDB ID**, so `handle_movie_id_conflicts` was never reached, no row merged, no relation
  re-pointed, and the consuming plugin's guard never fired. Zero change on their side.

  **The reason is structural and should have been predictable:** a row is ID-less precisely
  because the provider could not identify it, and a provider with no ID in its *listing*
  generally has none in its *detail* either. Calling detail on exactly the population that
  lacks IDs is asking the least likely source for the thing it has already failed to supply.

  🔑 **So de-duplication is retired as a justification for this feature, not merely narrowed.**
  The metadata harvest and the demand signal stand on their own. **Do not resurrect
  "it also fixes duplicates" from the reasoning below** — that reasoning describes what the
  code paths would do, and the measurement says the input that triggers them does not arrive.

  💡 It also explains the empty stratum 1a from the other direction: on the provider inside
  the merge plugin's scope everything reachable has already been healed, and on the providers
  outside it there is nothing to heal with.

  *(The durability analysis below remains correct and is kept, because it governs what would
  happen IF a merge were ever induced — on another library, or if a provider's detail
  coverage improves.)*

- ⚠️ **Were a merge ever induced, it would be durable only on providers inside the merge
  plugin's configured scope.** The mechanism is worth keeping because it is the same one that
  creates the duplicates:

  At the provider's next M3U refresh, core re-derives the relation's movie from the listing
  entry. That entry has no TMDB ID — which is why the row was ID-less to begin with — so
  core keys it by `(name, year)`, and `lookup_by_name_year` matches **only rows where both
  tmdb and imdb are null**. The canonical now carries a TMDB ID, so it cannot be seen. Core
  finds the old ID-less row, which still exists, and re-points the relation back to it.
  **The merge reverts exactly and the duplicate returns, within a day.**

  Durability comes from the merge plugin re-injecting the ID on every scan, not from the
  merge being written once — so it holds only where that plugin is scoped. On this install
  that was **13 of 16** sampled conflict candidates sitting off-scope.

  **Do not justify this feature on fewer duplicates.** The metadata harvest and the demand
  signal stand on their own; de-duplication is a side effect scoped to one provider, and
  widening it is a configuration change on the consuming side rather than anything here.
  ⚠️ **The library is protected regardless** — ADR-F007 stops the sync deleting a folder it
  just wrote, which is what made that delete-recreate cycle harmless. A returning duplicate
  is a row in the de-dup view, not a missing film.

  💡 **Consequence for ADR-F004 stage 3: it may be exercised TWICE per off-scope row** — once
  when the merge lands and the old ID dies, once when the revert brings it back. Expect that
  rather than reading the second re-point as a fault.

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

### Publish the wanted set as a file, alongside calling

**Agreed with the consuming plugin's author, 2026-09-16.** The call and the demand signal are two
jobs, and this ADR had them riding on one mechanism. They separate cleanly:

- **The file** is the demand signal — lossless, current, self-expiring, and it survives relation
  churn because TMDB IDs do.
- **The call** is purely a metadata harvest, justified on its own terms: provider data is free
  where it exists, so the later probe pass only has to cover the remainder.

🔑 **What forced it: under this ADR's chosen cadence the refresh timestamp never expires.** Calling
once per newly-marked title means a stable title keeps an old timestamp forever, so recency carries
no information and the signal degenerates to a boolean — it can say "was wanted once" and never "no
longer wanted". For scoping a pass where each probe costs a real provider connection slot, the
population would accumulate titles the user has since dropped. **That is lossy on precisely the axis
that matters, and it is the one thing the consuming side cannot reconstruct later.**

**The contract:**

```json
{ "schema": 1, "generated_at": "<ISO 8601 UTC>", "generator": "emby-strm",
  "count": 2412, "tmdb_ids": [603, 27205], "unidentified": [{"stream_id": 419883}] }
```

Written at the end of each movie sync, to a configurable path, blank meaning do not write.

- **An object, not a bare array**, for the same reason that disqualified the refresh timestamp: if
  the generator stops writing, a stale bare array is indistinguishable from a current one forever.
  `generated_at` lets a consumer refuse to act on a set nobody has confirmed in months.
- **`count` guards a partial write.** A sync can abort part-way for reasons unrelated to this file;
  a count disagreeing with the array length is the cheapest possible signal that it did. It covers
  `tmdb_ids` only — stated in the README so nobody has to guess.
- **Written atomically** — temp file, then rename within the same directory. The consumer is an
  unattended nightly pass and a truncated JSON read at 3am is miserable to diagnose.
- 🔑 **`unidentified` is closer to the CORE of the probe population than its margin**, which
  inverts the obvious reading. It looks like a ~5% tail — the titles a provider shipped with no
  TMDB ID. But on a real library that correlates hard with shipping no *metadata at all*, so the
  free API route can never populate them and a self-probe is the only thing that ever will.
  **Do not drop this array as a simplification**; the 5% figure makes it look optional and it is
  not. We supply `stream_id` only, having no account concept; the consumer resolves accounts.
- Entries will sometimes fail to resolve, because `stream_id` is `Movie.id` and those rows churn.
  Expected, not corruption. ADR-F004 stage 3 keeps the *decision* attached across that churn; it
  is only this file's pointer that goes stale, and the next sync rewrites it.

**Transport: a file, not an HTTP endpoint**, though the plugin already has an authenticated API and
both containers share a network. Serving it would mean storing an Emby API key in the consumer's
plugin settings — **a credential lifecycle introduced to solve a file-placement problem**, in a
plugin that otherwise holds nothing sensitive. A read-only mount adds no secret. Two smaller
reasons point the same way: the consumer runs unattended overnight, where a missing file fails
louder than a 401 nobody sees; and a file can be inspected with `cat` at 3am.

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

🔑 **AND THE 87% WILL DRIFT ONCE MERGES START FIRING — raised by the consuming plugin's author
2026-09-16, and we had missed it.** `director` and `cast` live on the **movie row**, and a merge
changes which row backs a title: the relation is re-pointed onto the canonical, whose people
fields may differ from the row the call just populated. **So the marker can appear or disappear
as a side effect of merging rather than of fetching, and "flipped the marker" is not quite the
same event as "got detail".**

Self-correcting — a title whose marker went away is simply called again — and it does not change
the design, because the failure direction is still only ever an extra call. But **the 87% was
measured in a window with no merges firing**, and these calls deliberately induce them. Expect the
figure to move once the feature runs at scale, and **do not treat that drift as a regression or go
hunting for a cause**: it is this, and it is benign.

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
