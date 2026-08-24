# Changelog

All notable changes to this fork are documented here. This fork follows its own version line
starting at 1.0.0 — independent of upstream
[firestaerter3/emby-xtream](https://github.com/firestaerter3/emby-xtream) — and aims to follow
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.1.1] - 2026-08-24

### Fixed

- **Season 0 and episode 0 specials no longer collide with Season 01 / E01.** Episodes
  reporting a season or episode number of 0 were forced to 1, so a show's specials were
  written onto real Season-1 slots. Because specials carry different episode titles, each one
  landed as a *second* `.strm` beside the genuine episode rather than replacing it — showing
  up as duplicate episodes in Emby. They now write to `Season 00` / `E00`, which Emby treats
  as Specials. The `.strm` URL is keyed on episode ID rather than the season/episode number,
  so this only relocates files; no streams change.
- **Providers that omit the per-episode season are handled correctly.** Where an episode
  reports no season of its own, the season number is now taken from the episodes map key
  instead of defaulting to season 1, so those shows are no longer flattened into `Season 01`.

Existing duplicates clear once each affected show is re-processed, on a sync that finishes
with zero failures — orphan cleanup is gated on that.

## [1.1.0] - 2026-08-10

### Changed

- **Bulk exclude/include keep their titles on screen.** "Select all matching" and "Deselect
  all matching" now restyle the affected rows in place instead of clearing them, so you can
  tick back the handful you want to keep before the list refreshes.
- **The de-dup view opens on "Browse by category" each time** instead of restoring the
  last-used mode, so you land on a populated view (and can spot newly-added categories)
  rather than a blank list.
- Renamed "Mark all shown reviewed/unreviewed" to "**Mark all matching**…", since they act
  on the whole filtered set, not just the rows currently visible.

### Fixed

- **Dispatcharr episode refresh no longer churns the series sync.** Each relation is now
  re-refreshed at most weekly (new relations are still covered on first sight) rather than on
  every sync, and episode change-detection ignores the container extension — Dispatcharr
  resolves streams by episode ID and its reported extension can flip (mkv↔mp4) between
  refreshes, which was causing needless rewrites and duplicate `.strm` files. Episodes now
  stay stable from one sync to the next.

## [1.0.0] - 2026-08-05

First release of the fork: a title-level de-duplication and review workflow layered on upstream's
Xtream `.strm` generator, tuned for Dispatcharr-proxied providers.

### Added

- **De-duplicated review view** — one row per unique title across your selected categories (movies
  collapse by stream ID, series by name); search, category filter, and title-level exclusion.
- **Reviewed checkpoint** — mark titles reviewed (a bookmark, separate from excluding), with
  segmented **Show** (All / Included / Excluded) and **Reviewed** (All / Reviewed / Unreviewed)
  view filters; per-title and bulk.
- **Browse ⇄ De-duplicated review toggle** — switch between the per-category tree and the de-dup
  list; both edit the same exclusion list, so switching is lossless, and the choice is remembered.
- **Title-level series exclusion** — extends an exclusion to cover every cross-listed copy of a
  title, so excluded shows stay excluded as categories change.
- **Sync robustness for duplicates** — cross-listed series collapse to one folder instead of
  writing duplicate per-episode files; series whose episode list returns empty under load are retried.
- **Opt-in Dispatcharr episode refresh on sync** — refreshes Dispatcharr's episode streams for
  every copy of a synced series (not just the one written to disk), so alternate versions such as a
  4K copy in another category become available to Dispatcharr's stream selection; throttled to once
  per copy per day.

Built on upstream firestaerter3/emby-xtream (MIT); all upstream install, Live TV, Dispatcharr
integration, and credential-safety features are included.

[Unreleased]: https://github.com/andyj682/emby-xtream-dedupe/compare/dedupe-v1.1.1...HEAD
[1.1.1]: https://github.com/andyj682/emby-xtream-dedupe/compare/dedupe-v1.1.0...dedupe-v1.1.1
[1.1.0]: https://github.com/andyj682/emby-xtream-dedupe/compare/v1.0.0...dedupe-v1.1.0
[1.0.0]: https://github.com/andyj682/emby-xtream-dedupe/releases/tag/v1.0.0
