# ADR-F004: Survive Provider ID Churn Inside the Plugin

*(Fork ADR. Numbered in the fork's own `F` sequence so it can never collide with an
upstream ADR — see [README.md](README.md).)*

**Date**: 2026-09-09
**Status**: ACCEPTED (not yet implemented)
**Affects**: `StrmSyncService.SyncMoviesAsync` (the review gate, `CleanupOrphans`,
`BuildLibraryIdentityIndex`), `PluginConfiguration` (new parallel TMDB fields),
`Configuration/Web/config.js` (de-dup view store handling), `scripts/repair-id-churn.py`
(remains, but leaves the critical path for movies)

---

## Context

Every decision this fork stores — excluded, reviewed — is keyed on the provider's
StreamId. That is the only identifier the plugin has ever had, and it works exactly as
long as the provider keeps its IDs stable.

On 2026-09-09 a provider re-issued **every** stream ID in its catalog and changed its
naming conventions in the same event. Dispatcharr deleted and recreated all 30,030 of
that provider's movie relations. Of the 23,103 movies affected:

- **13,309 (58%) survived.** Another provider held a relation on the same `Movie` row,
  so the row was never pruned: same `Movie.id`, same XC `stream_id`, working URL.
- **9,794 (42%) did not.** Those were exclusive to that provider, so the row became
  relation-less, was pruned, and came back with a new ID.

The survival was **Dispatcharr's** TMDB-based row merging, not anything this plugin
does, and it is structurally unavailable to single-provider content — there is no other
row to share. Note also that XC advertises `Movie.id` as the stream ID, so URL survival
is exactly row survival.

## Problem

One root cause — decisions keyed on a rotating identifier — produces three distinct
failures, and all three are silent.

**1. Decisions detach.** ~9,700 titles the user had already excluded reappeared as
un-reviewed. Nothing in any log explains it; the queue simply grows.

**2. Content is deleted with no record.** A title whose ID goes dead leaves a `.strm`
pointing at nothing. It is not in `writtenPaths`, so orphan cleanup deletes it — correct
by cleanup's own rules, wrong in effect, and unlogged. This happened on 2026-09-08:
that run reported **−127** with no indication of what. Those files were titles the user
had decided to keep, and the sync could never restore them because the decision pointed
at an ID no longer in the catalog. Today's repair rewrote **113** of them.

**3. Recovery depends on something outside the plugin.** `repair-id-churn.py` can
re-point dead IDs, but only against a `catalogue-snapshot.py` snapshot taken *before* the
event. The plugin keeps no such record. The 2026-09-09 recovery worked only because a
snapshot from 48 hours earlier happened to exist, taken by hand; the same directory
showed a six-day gap immediately before it. Recovery rested on luck.

**What did work, and should be preserved:** the review gate held the ~9,700 rather than
writing them. That run processed 12,079 included titles and wrote **3**. The library was
never at risk, which is the difference between an annoyance and a disaster.

## Alternatives considered

1. **Status quo — external scripts plus a snapshot cron.** Cheapest, and the scripts are
   already written and proven. Rejected as the primary answer because it leaves recovery
   dependent on a scheduled job whose absence is invisible until the moment it is needed,
   and because it cannot prevent failure 2 at all: cleanup deletes the file long before
   anyone runs a repair.
2. **Match on title name.** Rejected outright. The same event changed the naming
   convention (dotted filenames, mangled whitespace, dropped prefixes). Name matching
   produced confidently wrong answers twice during the investigation, and a wrong
   re-point onto an excluded title makes `RemoveExcludedContent` delete a folder with no
   ratio guard. Name matching must not enter the plugin.
3. **Match on poster asset basename.** Survives renames and covers the ~11% of titles
   that arrive with no TMDB ID. Deferred, not rejected: it needs the plugin to see poster
   URLs and to key them identically to `dispatcharr_vod_merge`, which is real work for
   the last tenth of the problem.
4. **Have the plugin maintain its own catalog snapshot.** Would close failure 3 without a
   schema change to the stores. Rejected as strictly worse than option 5: it stores
   ~37,500 rows to answer a question about ~66,000 decisions, and it still has to be
   read, pruned, and kept coherent.
5. **Store the TMDB ID alongside each decision.** Chosen — see below.

## Decision

**Treat TMDB as the durable identity for movie decisions, and heal ID churn during the
normal sync rather than through an external repair.**

TMDB is the right key here specifically: `tmdb_id` is on the movie *list* payload at ~95%
coverage, needs no detail fetch, and Dispatcharr enforces it as unique, so the mapping is
1:1. Series are excluded from this decision — they carry no TMDB ID on the `get_series`
list payload (measured 0 of 9,979) and need the detail-learned cache tracked separately.

Three stages, deliberately ordered so each is useful alone.

### Stage 1 — Log what was acted on

Log a sample of deleted paths in the orphan-cleanup and excluded-content summaries, and
log the collapse representative the sync picked. Neither changes behavior. Both exist
because three separate investigations in one week ended at "the plugin took a correct
action and recorded nothing about *what* it acted on."

### Stage 2 — A recoverability guard in `CleanupOrphans`

Before deleting a `.strm` whose ID is dead, check the folder's `[tmdbid=N]` against the
live catalog. If a live StreamId carries that TMDB, the title is **recoverable, not
orphaned**: rewrite the `.strm` with the new ID and add it to `writtenPaths` instead of
deleting it.

This is the REPAIRABLE/ORPHANED split `scripts/audit-strm-links.py` already computes,
moved inside the sync. The inputs are present — the sync holds the catalog, and
`BuildLibraryIdentityIndex` already parses `[tmdbid=]` from folder names. It converts
silent deletion into silent self-repair, which is the correct default because the user
has already expressed a decision about that title.

### Stage 3 — Persist TMDB alongside each decision

Record the TMDB ID next to the StreamId when a title is excluded or reviewed, in
**separate typed fields** rather than self-describing `tmdb:603` prefixes — prefixes lose
native `int[]` XML serialization and inflate the entries by ~1.8× in exactly the fields
whose size is already a concern.

With that in place a rotation cannot detach a decision: the sync sees the new StreamId,
matches its TMDB against a stored decision, and acts on it. No re-pointing, no snapshot,
no external script, no cron.

### Not a button

Healing runs automatically during the sync. A manual "repair" action was rejected: the
defining property of this failure is that it is silent, so any design requiring the user
to know it happened has already failed. A log line and a count in the sync summary are
the right surface.

## Consequences

- Movies become resilient to ID rotation. Measured against this event, TMDB matching
  covers **8,560 of the ~9,794** broken titles (87%), independently corroborated at 89%
  by direct measurement against the Dispatcharr database.
- **The 1.8% figure that previously demoted TMDB-keyed stores does not apply and must not
  be cited against this.** It was measured on an *additions* event — 5,974 genuinely new
  titles — where TMDB keying has no prior decision to carry and correctly scores near
  zero. This is the opposite class: the same titles under new IDs. Both measurements are
  right; they measure different events.
- `scripts/repair-id-churn.py` leaves the critical path for movies but stays, because it
  is the only route for damage predating stage 3, and the only route for series.
  Snapshots likewise stay useful as an independent record — just no longer load-bearing.
- The ~11% of titles arriving with no TMDB ID remain exposed. They are the honest
  remainder, and alternative 3 is the eventual answer if the residue proves annoying.
- Store size grows: a parallel TMDB field for ~66,000 decisions. Item 8's pruning of dead
  IDs becomes more attractive as a companion, since a re-pointed decision makes the old
  ID genuinely redundant rather than merely stale.
- Stage 2 changes deletion behavior, so it needs a test asserting that a dead-ID `.strm`
  whose folder TMDB is live is **rewritten and not deleted**, and its mirror: that a
  genuinely orphaned file is still removed. Both are unit-testable against a temp
  directory; no rig, no sync.
- Safety rules carry over from the external script unchanged and are non-negotiable:
  never re-point onto an ID that is currently on disk or reviewed-and-kept, and never
  match on name.

## Amendment, 2026-09-09 (same day): stage 2 withdrawn

Stage 1 shipped. **Stage 2 is withdrawn before implementation.** Reading the review gate's
actual condition showed the guard would never fire, and could be actively wrong.

**It tests the same thing the gate does.** The gate exempts an on-disk title when
`libraryTmdbIds.Contains(providerTmdb)`, and `libraryTmdbIds` is built from the `[tmdbid=N]`
in folder names. Stage 2's proposed condition — "the folder's TMDB is live in the catalog" —
is that same comparison. Wherever it would rescue an orphan, the gate has already
auto-reviewed and written the title, so no orphan exists. Wherever the gate misses, stage 2
misses for the identical reason.

**And it would retain files for excluded titles.** If a title is excluded but another live
StreamId carries its TMDB, the guard would preserve content the user deliberately blocked —
a regression, in service of a case that does not arise.

**Why the gate missed the titles that motivated this.** Of the ~9,794 broken titles, ~1,113
arrived from the provider with **no TMDB at all**. `hasTmdb` is then false regardless of what
the folder says, so everything falls to `libraryFolderNames.Contains(movieName)` — which
missed because the same event renamed every title. Those were held, never written, and their
old `.strm` files were swept as orphans. TMDB cannot reach that cohort in cleanup any more
than it can in the gate.

**Merging does not replace this, but the relationship is subtler than "it cannot help."**
The distinction is between **row identity** and **title identity**:

- **Row identity cannot be preserved at the Dispatcharr layer, ever.** No `Movie.id` or `uuid`
  survives a prune-and-recreate. This is what makes persisted TMDB identity *our* job and only
  ours, and it is why 58% survived (another provider held a relation on the same row) while the
  sole-provider 42% did not.
- **Title identity can be widened by merging.** With `tag_unique_movies` enabled,
  `dispatcharr_vod_merge` injects a TMDB into the listing entry *before* the merge key is
  computed, so even a sole-provider recreated row can arrive already carrying a stable TMDB —
  which is exactly what this ADR's recovery keys on.

**That distinction is the difference between the 8,560 recovered and the 1,113 residue**: the
recovered rows arrived tagged; the residue did not. The two layers are complementary rather
than alternatives. For this provider it would not have changed the outcome — 38 probed id-less
relations returned zero detail TMDBs, and it supplies TMDB artwork on roughly 8% of entries —
but that is a property of the provider's metadata, not a structural limit.

**A merge-layer setting change is an ID-churn event.** Merges are re-derived at scan time
rather than stored, so re-enabling `dry_run` or disabling a merge switch lets already-merged
titles split apart again, minting new rows and new IDs; `tag_unique_movies` likewise changes
IDs deliberately when it tags. Roughly 208 rows on the current instance exist only because
that plugin keeps running. Treat reconfiguring it as equivalent to a provider re-ingest.

**Consequences of the amendment:**

- **Stage 3 is unconditional and is now the only remaining stage.** It is the only layer that
  can carry a decision across a row being destroyed and recreated, so it does not depend on
  how well merging performs.
- The ~1,113 id-less titles remain uncovered by TMDB. `dispatcharr_vod_merge` matches
  `tmdb_id` → poster basename → plot text, and its maintainer's measurements settle two
  things: **the plot tier is inert for movies** (6 keys against ~43,000 poster keys — it is a
  series mechanism, so do not build it), and any poster tier must be **byte-equality on a TMDB
  asset basename with a uniqueness guard** — a key usable only if it maps to exactly one TMDB
  library-wide, anything else permanently demoted to ambiguous. No fuzzy matching, on either
  side. Whether the Emby plugin should implement a poster tier at all is **open** and hinges on
  how many of the residue carry a usable poster key; expectation is low.
- Orphan cleanup keeps its current behavior. Stage 1's logging means the next occurrence
  names the files, which is what makes the question answerable at all.

## Implementation references

- `Emby.Xtream.Plugin/Service/StrmSyncService.cs` (`CleanupOrphans`,
  `RemoveExcludedContent`, `BuildLibraryIdentityIndex`, the movie review gate)
- `Emby.Xtream.Plugin/PluginConfiguration.cs` (new parallel TMDB fields)
- `scripts/audit-strm-links.py` (the REPAIRABLE/ORPHANED logic stage 2 internalizes)
- `scripts/repair-id-churn.py` (retained for series and historical damage)
