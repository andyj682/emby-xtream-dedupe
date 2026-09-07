# ADR-F003: Retire the sync-time Dispatcharr episode refresh

*(Fork ADR. Numbered in the fork's own `F` sequence so it can never collide with an
upstream ADR — see `docs/decisions/fork/README.md`. Was ADR-018 before 2026-09-07.)*

**Status:** Accepted (2026-08-29)

## Context

`RefreshDispatcharrEpisodes` was an opt-in setting that, after each series sync, called XC
`get_series_info` for every collapsed-away sibling of a synced show — the copies our
collapse-by-name discards — purely to trigger Dispatcharr's per-relation episode refresh as a side
effect. The response body was discarded. It was throttled per relation via
`DispatcharrEpisodeRefreshLogJson`.

The intent was sound when it was written: Dispatcharr fetches episode streams lazily, our sync only
ever fetches the representative of a collapse group, and so a cross-listed copy's episode streams
could stay invisible to Dispatcharr's stream selection indefinitely. At the time, a sync-time poke
was the only lever available.

Three things established since have removed the justification.

**1. `get_series_info` returns the union across the relations behind one series record.**
Confirmed 2026-08-27 on a discriminating case: a show carried by two providers with materially
different season counts returned all available episodes. So for copies Dispatcharr has linked into
a single series record, the representative's own fetch already refreshes and returns everything —
poking siblings adds nothing.

**2. The server-side sweep covers every relation of any series it knows about**, and it learns
which series to care about from the `get_series_info` calls this plugin makes. So for any show we
sync, the sweep already reaches all of that record's relations.

**3. Stream selection is scoped per episode record.** When Dispatcharr picks a stream for an
episode, it considers only streams attached to *that* episode record — not streams on a same-named
episode under a different series record.

Point 3 is what closes the remaining case. Where Dispatcharr has *not* linked two copies, they are
separate series records with separate episode records. Our `.strm` files point at the
representative's episodes. Refreshing an unlinked sibling therefore freshens streams that stream
selection will never consider for anything in the library.

## Decision

Remove the feature: both config fields, the refresh pass, its throttle log and helpers, the
`refreshOnlyIds` collection, the UI checkbox, and the README section.

The reasoning is not "the sweep makes this redundant" — it is stronger than that. **The feature is
inert.** Where copies are linked it duplicates work already done; where they are not, it refreshes
records nothing points at. Neither path reaches playback or the `.strm` library.

It also has a standing cost that only became visible once the sweep was cron-driven and
list-based: because the sweep learns its targets from our `get_series_info` calls, enabling this
feature would permanently add every sibling id to the sweep's working set, making every subsequent
cron cycle do more work — on a job that already takes ~15 minutes — for no benefit.

Keeping it "reframed" was rejected for that reason. A checkbox a user might tick is worse than no
checkbox when ticking it has a real cost and no upside.

## Consequences

- Anyone who had the setting **on** loses a Dispatcharr-side refresh. The CHANGELOG says so
  explicitly and points at `dispatcharr_vod_episode_sweep` as the replacement, rather than removing
  it silently.
- Old configs keep `<RefreshDispatcharrEpisodes>` and `<DispatcharrEpisodeRefreshLogJson>` elements
  harmlessly; Emby's deserializer ignores unknown elements, and both disappear from the file on the
  next save.
- Separation of concerns improves: refreshing Dispatcharr is now owned by one component that does
  it properly, rather than split between a cron-driven sweep and a best-effort sync-time poke.
- The integration test asserting that collapsed-away siblings are **not** fetched is kept, and made
  unconditional. That invariant used to be one branch of a toggle; it is now simply true, and the
  test guards against reintroducing per-sibling calls.

## What would reverse this

If Dispatcharr's stream selection ever matched episodes *across* series records rather than within
one, the unlinked-sibling case would regain value and this decision should be revisited. The
mechanism would still be better placed in the sweep than in a `.strm` generator.
