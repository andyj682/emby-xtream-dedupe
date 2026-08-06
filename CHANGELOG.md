# Changelog

All notable changes to this fork are documented here. This fork follows its own version line
starting at 1.0.0 — independent of upstream
[firestaerter3/emby-xtream](https://github.com/firestaerter3/emby-xtream) — and aims to follow
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[Semantic Versioning](https://semver.org/).

## [Unreleased]

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

[Unreleased]: https://github.com/andyj682/emby-xtream-dedupe/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/andyj682/emby-xtream-dedupe/releases/tag/v1.0.0
