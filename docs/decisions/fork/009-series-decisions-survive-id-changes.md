# ADR-F009: Series Decisions Survive ID Changes

*(Fork ADR. Numbered in the fork's own `F` sequence so it can never collide with an
upstream ADR — see [README.md](README.md).)*

**Date**: 2026-10-27
**Status**: ACCEPTED
**Affects**: `Client/Models/SeriesInfo` (reads `tmdb_id`), `StrmSyncService.SyncSeriesCoreAsync`
(a reconcile pass before the exclusion filter), `PluginConfiguration` (new
`SeriesDecisionTmdbIdsJson`)

---

## Context

Every series decision this fork stores (excluded, reviewed, and the stored episode list that
lets a show skip unchanged) is keyed on the XC `series_id`. ADR-F004 stage 3 made the movie
decisions survive a provider re-issuing its ids by recording each decision's TMDB id and
carrying the decision to whatever id that TMDB id turns up under next. Series were left out,
on a measurement that turned out to be wrong.

**The listing has carried a TMDB id for every series all along.** Dispatcharr's `get_series`
emits it as `tmdb_id`, in 0.32 and 0.33 alike. The plugin's `SeriesInfo` reads a field named
`tmdb`, which other Xtream panels use, so it never saw Dispatcharr's. The "0 of 9,979 series
carry a TMDB id" finding behind this item's old "half-blocked" status was a field-name
mismatch, not missing data. Measured on the install this was built against: **10,149 of
10,484 shows** in Dispatcharr have one.

Dispatcharr 0.33 made this urgent. From 0.33 the `series_id` is the show's own id
(`Series.id`), so it is stable across providers coming and going, but it now changes when the
**show** changes: when the VOD merge plugin folds one show into another, when Dispatcharr's
per-category language tags split or merge shows, and when a show loses every provider entry and
is recreated. Each is a small renumbering, and the stored decisions do not follow.

Most of those are absorbed already. A kept show that comes back under a new id with the same
name is recognized by its folder and marked reviewed (the review gate's self-heal); an excluded
one comes back unreviewed and is held. The failure that is not absorbed is the merge of **two
copies of one show where the user kept one and excluded the other**: if the surviving id is the
excluded one, the show the user kept disappears from the sync and orphan cleanup removes its
folder.

## Decision

1. **Read the series TMDB id from `tmdb_id` as well as `tmdb`.** Only the catalogue snapshot
   read the field before this, so nothing else changes behavior.

2. **Record the TMDB id of every series id that carries something worth carrying** — an
   exclusion, a reviewed mark or a stored episode list — in `SeriesDecisionTmdbIdsJson`,
   backfilled for free from each sync's listing, and kept current: a listed id's record follows
   its TMDB id if Dispatcharr changes it, since a record only matters once its id is gone and the
   latest TMDB id is then the one to look for. Same pair-per-id shape as
   `VodDecisionTmdbIdsJson`, for the same reason: a bare set of TMDB ids cannot tell a
   withdrawn decision from a moved one.

3. **At the start of each series sync, before the exclusion filter**, a recorded id that has
   left the listing while its TMDB id is listed under another id has its decisions carried
   there: the exclusion, the reviewed mark and the stored episode list. Where several live ids
   carry the same TMDB id (0.32, or an unmerged pair), the lowest wins, so the target cannot
   flip between runs. Stands down on a partial fetch, where absence proves nothing.

4. **Keep wins, where "kept" means reviewed and not excluded.** When the copies of one show
   disagree, an explicit keep beats an exclusion:
   - an exclusion is **not** carried onto a show any copy of which is kept, whether that copy
     is the one that moved or one still listed;
   - a listed id that is excluded **is lifted** when a kept copy of the same show has just
     merged into it and no listed copy is kept already. This removes a decision, the one thing
     ADR-F004's movie pass never does, so it is logged by name and happens **once**: see 5.

   Merely unreviewed, or on disk because the review gate is off, is **not** kept. Those copies
   carry no decision, so an exclusion on another copy is the only decision there is and it
   carries. Same TMDB id means same show; a user who kept one copy and excluded another wanted
   the show once, and after a merge there is only once.

5. **A moved record is retired once its decisions have been carried.** The movie pass keeps old
   records forever and relies on idempotency. That is not safe here: a lift that re-ran would
   undo a user re-excluding the show, on every sync. The new id gets its own record, so a third
   move is still covered; an old id whose TMDB id is listed nowhere keeps its record, so a show
   that comes back later is still recognized.

## Alternatives considered

- **Hold for review instead of keep-wins** (suggested by the VOD merge plugin's maintainer).
  Safer on paper, but a held show is not written, so its existing folder becomes an orphan and
  orphan cleanup deletes it — the outcome this exists to prevent — unless a second protection
  for folders by TMDB id is added. Keep-wins avoids that, and its worst case is a re-exclusion.
- **Never delete a series folder whose `[tmdbid=…]` matches a show written in the same sync.**
  Rejected: two copies of one show carry the same TMDB id, so this would block the de-dup
  workflow itself — keeping "WeCrashed" and excluding "We Crashed" would leave the excluded
  copy's folder on disk for good. Names cannot tell a merge from a duplicate pair; recorded ids
  can.
- **Have the VOD merge plugin prefer the non-excluded id when merging.** Would cover only its
  own merges, not Dispatcharr's language reorganization or any later core change, and would
  couple it to this plugin's decision stores. Its maintainer agreed the general rule belongs
  here.

## Consequences

- Series decisions follow a show across any id change Dispatcharr can make, for every show that
  has a TMDB id, excluded ones included.
- Shows without a TMDB id keep only the name-based protections (the self-heal and ADR-F001's
  name-group propagation).
- `SeriesDecisionTmdbIdsJson` grows with the decision stores. Unlike the movie map, it shrinks as
  moved records retire.
- Each run logs what it did — moves carried, exclusions declined because a copy is kept,
  exclusions lifted (by name) — and the coverage of the record against the decisions it backs.
