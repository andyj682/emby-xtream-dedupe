# ADR-019: Specials Go to Season 00, and Naming Changes Force One Full Re-Sync

**Date**: 2026-09-27
**Status**: Accepted
**Affects**: `StrmSyncService.SyncSeriesCoreAsync` (episode numbering), `StrmSyncService.CurrentStrmNamingVersion`

---

## Context

The episode write loop turned season 0 and episode 0 into 1. Providers list a show's specials
as season 0, so specials were written into `Season 01` and episode 0 onto `E01`. A special
carries a different title from the real episode, and the title is part of the filename, so it
became a second file beside the real one: a duplicate episode in Emby. Found and first fixed in
andyj682/emby-xtream-dedupe (4c3e0aa).

## Problem

Allowing 0 through is not enough on its own:

1. **Providers that omit the per-episode `season` field.** It then reads as 0, and with 0 allowed
   those shows would be written entirely into `Season 00`.
2. **Libraries that already have the misplaced files.** The episode hash that lets a sync skip an
   unchanged show covers episode IDs only. Moving specials does not change it, so affected shows
   would be skipped forever and never corrected.

## Alternatives Considered

### 1. Allow 0 and trust the per-episode field

**Rejected**: problem 1. A provider that leaves the field out would move whole shows into Specials.

### 2. Fix new writes only, leave existing libraries alone

**Rejected**: problem 2. Existing duplicates would never clear.

### 3. Include season numbers in the episode hash

**Rejected**: it only helps once, costs the same full re-fetch as a naming-version bump, and
changes a hash other code relies on for no lasting gain.

## Decision

- Season and episode 0 pass through. When the per-episode `season` field is 0, the season comes
  from the episodes map key, which is keyed by season, and only then falls back to 1.
- `CurrentStrmNamingVersion` goes from 1 to 2. `CheckAndUpgradeNamingVersion` then resets both
  sync watermarks and clears the episode hashes once, so the next sync rewrites every show at its
  corrected paths. The files at the old paths become orphans and are removed by orphan cleanup
  where it is enabled.

**The rule for future naming changes:** any change to where or under what name an existing item
is written must bump `CurrentStrmNamingVersion`, or libraries synced before the change keep the
old layout indefinitely.

## Consequences

- Specials appear under Specials instead of duplicating Season 1 episodes.
- The first sync after updating is a full one for movies and series. The bump resets both
  watermarks although only series paths changed, since the mechanism does not distinguish them.
- For a provider that omits the per-episode season field, every episode was in `Season 01`
  before and is now split across its real seasons. In a library where that is a large share of
  all episodes, the orphan ratio can exceed `OrphanSafetyThreshold` and cleanup refuses, every
  run, leaving the old `Season 01` copies as duplicates. That is the safe failure (ADR-013); the
  user clears it by raising the threshold for one sync.
- The "Retry failed items" path used to write series in a layout of its own. ADR-020 removed it:
  a series retry now runs the normal series sync.
