# Changelog

All notable changes to this fork are documented here. This fork follows its own version line
starting at 1.0.0 — independent of upstream
[firestaerter3/emby-xtream](https://github.com/firestaerter3/emby-xtream) — and aims to follow
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- **New "Require review before sync" option for movies, off by default.** Normally anything
  you haven't excluded gets synced, which is fine until your provider adds content in bulk —
  one overnight addition here was 5,974 titles, and they would all have landed in the library
  before there was any chance to look at them. With this on, a movie is written once you have
  reviewed it or excluded it, so new arrivals wait in the de-dup view instead. Holding a title
  is **not** the same as excluding it: nothing is added to your exclusion list, no folder is
  removed, and it stays in the unreviewed list until you decide. A film you already have on
  disk is never held — if your provider reissues it under a new ID, it is recognised from its
  folder, synced as before, and quietly marked reviewed under the new ID, so your review
  decisions survive the provider renumbering things. If the reviewed-list setting is ever
  unreadable the option switches itself off for that run and says so in the log, rather than
  treating everything as unreviewed and holding your whole library back.

### Fixed

- **An excluded show no longer comes back when you enable another category.** Exclusions are
  stored as provider series IDs, and your provider gives the same show a different ID in every
  category it appears in — so excluding a show only covered the copies that existed at the time.
  Enable a category later and the show arrived under a fresh ID and synced again. The de-dup view
  already repaired this, but only if you opened it and saved, which a scheduled sync never does.
  The sync already groups duplicate copies of a show that would share a folder, keeping one to
  write; it now applies your exclusions to those whole groups instead of to individual IDs, so a
  new copy of an excluded show is recognised as the same show and skipped. Nothing to migrate and
  nothing new to tick — your existing exclusions gain this on the next sync. Copies whose names
  differ ("WeCrashed" vs "We Crashed", or a quality prefix) are still separate titles and still
  need excluding individually.
- **A cross-listed series keeps the same copy as its representative between syncs.** When one show
  is listed under several provider IDs, the sync writes one of them and skips the rest — but which
  one depended on the order the provider happened to answer in. If it changed, the episode
  bookkeeping was attached to the old ID, and the show could sit indefinitely with no record of
  its episodes (and so never be checked for new ones) until some later sync happened to look at it
  broadly. The choice is now fixed: whichever copy already has episode records keeps the job, and
  otherwise the lowest ID wins.

## [1.3.0] - 2026-08-25

### Added

- **The Emby library is refreshed after a sync that changed files.** New content used to
  wait for Emby's next scheduled scan, which could be hours. The sync now tells Emby that
  the Movies or Shows folder changed, and only when it actually added or removed something
  — a sync that changed nothing triggers nothing. Particularly useful with real-time
  monitoring switched off, a common choice since watching the folder can stop a drive
  spinning down. Emby coalesces the notification, so content appears a minute or two after
  the sync rather than instantly. New "Refresh the Emby library after a sync that changed
  files" toggle in Sync Settings, on by default.
- **Series that quietly do nothing are now named in the log.** A series whose provider
  returns no episodes, with no files already on disk, used to finish in complete silence
  while the sync reported success. It now says so and suggests excluding it, which also
  saves the retry attempts it costs on every run. A second warning covers the more general
  case: any series that ends a sync with no record of the episodes it should hold.

### Fixed

- **The sync summary no longer counts failures as writes.** "Written" was derived by
  subtracting skips from completions, and the failure path counted towards both — so a run
  where 604 series failed reported 877 written when it had written 273. Writes are now
  counted where the write happens. The skip total is also split by reason, separating
  "never fetched, unchanged since last sync" from "fetched, episodes identical", which are
  different answers when a series isn't picking up episodes you expect.

## [1.2.0] - 2026-08-25

### Changed

- **Episode filenames no longer include the provider's episode title.** Files are now named
  `Show Name - S01E02.strm`, keyed on the episode code alone. Providers hand back different
  titles for the same episode between refreshes — and sometimes none at all — so with the
  title in the name, a re-fetch wrote a *new* file beside the old one instead of replacing
  it. Every title change left a duplicate episode behind, and a full re-sync could produce
  tens of thousands at once. Emby matches episodes on the `SxxExx` code and its metadata
  providers rather than on filename text, so nothing is lost by dropping it.

  **Existing libraries are migrated automatically** on the next series sync: files are
  renamed in place, and where both the old and new names already exist the duplicate is
  removed. Nothing is orphaned, no settings need changing, and watched state is preserved.
  A one-line summary of what was renamed appears in the log. On a ~60,000 episode library
  this took about a second.

### From upstream

- Movie NFO files now carry a TMDB ID even when metadata IDs in folder names are switched
  off — the two settings were coupled, so turning off folder naming silently emptied the
  NFOs (upstream issue #63).
- Dispatcharr API token refresh is now serialised, so several requests hitting an expired
  token no longer trigger simultaneous re-authentication.

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

[Unreleased]: https://github.com/andyj682/emby-xtream-dedupe/compare/dedupe-v1.3.0...HEAD
[1.3.0]: https://github.com/andyj682/emby-xtream-dedupe/compare/dedupe-v1.2.0...dedupe-v1.3.0
[1.2.0]: https://github.com/andyj682/emby-xtream-dedupe/compare/dedupe-v1.1.1...dedupe-v1.2.0
[1.1.1]: https://github.com/andyj682/emby-xtream-dedupe/compare/dedupe-v1.1.0...dedupe-v1.1.1
[1.1.0]: https://github.com/andyj682/emby-xtream-dedupe/compare/v1.0.0...dedupe-v1.1.0
[1.0.0]: https://github.com/andyj682/emby-xtream-dedupe/releases/tag/v1.0.0
