# Changelog

All notable changes to this fork are documented here. This fork follows its own version line
starting at 1.0.0 — independent of upstream
[firestaerter3/emby-xtream](https://github.com/firestaerter3/emby-xtream) — and aims to follow
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- **Two new diagnostic scripts**, written while recovering from a provider that reissued every
  stream ID in its catalog. `config-counts-canary.py` reports how many exclusions and reviewed
  marks a config actually holds — on the live config, a test rig's, or a proposed repair — and
  deliberately distinguishes "empty" from "unreadable", which look identical in a count and mean
  opposite things. `check-repair-safety.py` runs before you install a `repair-id-churn.py`
  candidate and refuses one that would exclude a title you currently have on disk, checking both
  the stream ID and the folder name, because exclusions are stored per ID but enforced per name.

### Changed

- **Clearer guidance on which DLL to install now that Emby 4.10 has left beta.** Each release
  ships two builds, one for Emby 4.9.x and one for 4.10.0.17 and later, and the release page
  described the second as beta-only — true when it was written, misleading now that 4.10 is the
  general release. The README and the release notes now show both plainly, with the caveat that
  they are not interchangeable and you should install one, not both.

### Fixed

- **The Emby 4.10 download has to be renamed before you install it, and nothing said so.** Each
  release ships two builds, and because they cannot share a filename the 4.10 one carries a
  `-4.10` suffix. Emby names each plugin's settings file after the DLL, so installing it under
  that name gives it a *separate* settings file: the plugin loads, the settings page opens, and
  everything you had configured appears blank — no error, no warning. Nothing is actually lost,
  but there was no way to know that. The README and the release page now say to rename it, and
  explain why. **If you hit this, rename the file to `Emby.Xtream.Plugin.dll` and your
  configuration comes back.**

- **The build-from-source instructions cloned the wrong repository.** They pointed at the
  upstream project rather than this fork, so anyone following them built a plugin without any of
  the de-duplication or review features. They also now mention how to produce the Emby 4.10 build.

### Added

- **The sync now says which files it deleted, not just how many.** Orphan cleanup used to report
  a bare count — "Removed 360 orphaned STRM files" — and nothing anywhere recorded *which* ones,
  at any log level. So a run that removed a few hundred episodes was impossible to explain after
  the fact, and you could not tell a provider genuinely dropping a show from a title whose ID had
  changed underneath it. The summary now lists up to 15 of the removed paths, relative to your
  library folder, and says plainly when there were more.

- **A diagnostic naming which copy of a duplicated show the sync actually used.** When the same
  show appears under several IDs, the sync picks one to work from and ignores the rest. Which one
  it picked was invisible from outside the plugin, which made a missing episode very hard to
  investigate: the ID you can see from a catalogue listing is usually *not* the one the sync acts
  on, so checking it tells you nothing. At Debug level the sync now logs the chosen ID and the
  ones it set aside, for each show that collapses.

## [1.6.0] - 2026-09-09

### Added

- **A `LICENSE` file.** The README and badge have always said MIT, but there was no license text
  in the repository, which meant GitHub detected no license at all — the default for that is all
  rights reserved, contradicting the badge. The MIT text is now present, with a copyright notice
  naming both the upstream project this is built on and this fork's additions, under the same
  terms.

- **A "Video codec for Dispatcharr channels" setting**, in the Dispatcharr section of the plugin
  config page. **Automatic (recommended)** is the default and is what fixes the playback problem
  below; the other choices are escape hatches. **Use the codec Dispatcharr reports** restores the
  old behavior if profile detection ever reads one of your profiles wrongly, and **Always H.264**
  / **Always HEVC** are for setups where the plugin cannot reach your stream profiles at all but
  you know what they output. Existing installs upgrade to Automatic without any config change.
  Picked up from upstream.

### Fixed

- **Live TV channels no longer fail to play when your Dispatcharr stream profile re-encodes the
  video.** Dispatcharr reports the codec it *receives* from your provider, which is not the codec
  it *sends* to Emby if the profile transcodes. A channel arriving as HEVC and leaving as H.264
  was therefore announced to Emby as HEVC, Emby chose the wrong decoder, and playback died before
  it started. The plugin now reads the channel's stream profile to learn what it actually outputs,
  and falls back to the reported codec for profiles that pass video through untouched — so
  pass-through setups behave exactly as before. Resolution, frame rate, bitrate and audio details
  are still taken from Dispatcharr either way; the more codec-specific details (profile, level,
  bit depth, reference frames) are now only declared when the codec being announced really is the
  one Dispatcharr reported, since they describe the incoming stream rather than the outgoing one.
  Profile data is read during the normal channel refresh, not at the moment you tune, so this adds
  nothing to the time it takes a channel to start. Picked up from upstream (issues #66 and #67).

  *Not independently verified here: this fork's testing covers the `.strm` sync rather than Live
  TV, so this fix rides on upstream's.*

### Changed

- **This fork's decision records now live in `docs/decisions/fork/` and are numbered separately**,
  as ADR-F001, ADR-F002 and ADR-F003 — previously ADR-016, ADR-017 and ADR-018. Upstream and this
  fork were both numbering decision records from the same sequence, so they had begun to collide:
  upstream's own ADR-016 arrived alongside ours. Keeping the two sets apart means a reference like
  "ADR-016" points at exactly one document again. Contributor-facing only; nothing about how the
  plugin behaves changes. The 1.5.0 entry below has been repointed at the moved file so the link
  still resolves; references in commit messages and git history keep the numbers they were
  written with.

## [1.5.0] - 2026-08-29

### Added

- **The de-dup view's category filter now shows how many titles each category contributes.**
  With the Reviewed filter set to Unreviewed, this turns one undifferentiated backlog into a
  breakdown you can plan against — you can see which categories your unreviewed titles are
  actually in and work through them one at a time, and a category showing (0) is one you've
  finished. The counts follow your search and Show/Reviewed filters but deliberately ignore the
  category ticks themselves, so unticking a category doesn't blank its own count and you can
  always tick it back knowing what's behind it. A title listed in several categories counts in
  each, so the numbers overlap rather than dividing the total up.

### Fixed

- **Reviewing or excluding a single title now updates the counts immediately.** Previously the
  count line only caught up on the next search, filter change or reload, so it could sit there
  disagreeing with what you'd just done.

### Removed

- **The "Refresh Dispatcharr episode data" setting has been removed.** It asked Dispatcharr to
  refresh episode data for the duplicate copies of a show that don't get written to disk. Testing
  since showed it cannot affect anything you actually watch: where Dispatcharr has linked a show's
  copies together, the normal sync already refreshes all of them, and where it hasn't, the copies
  are separate records whose episodes nothing in your library points at. Leaving it switched on
  also made a server-side sweep permanently slower for no benefit. **If you had it enabled**, the
  replacement is a server-side episode sweep such as
  [dispatcharr_vod_episode_sweep](https://github.com/andyj682/dispatcharr_vod_episode_sweep) —
  see the new "Related projects" section in the README. Full reasoning in ADR-F003
  (filed as ADR-018 at the time; renumbered 2026-09-07).

### Changed

- **The README now says plainly that a `.strm` generator alone will not keep episodes up to date.**
  Nothing in a normal sync makes Dispatcharr look for new episodes of shows you already have, so
  without a server-side sweep they can silently never appear. That surprises people, and it is a
  property of how this works rather than a bug, so it now has its own section.

## [1.4.1] - 2026-08-28

### Fixed

- **Shows you have already reviewed no longer drift back into the unreviewed queue.** Your
  provider gives the same show a separate ID in every category it appears in, and it gains a
  new one every day or two as categories and provider relations shuffle. The de-dup view used
  to require *every* one of a title's IDs to be reviewed, so each new ID quietly undid a
  review you had already done — and because the drift never stops, the same handful of shows
  reappeared in the queue every morning. A title now counts as reviewed once **any** of its
  IDs is. Reviewing a title still records every ID it has, so nothing about the stored list
  changes; only titles that gained an ID *after* you reviewed them are read differently.
  Movies are unaffected either way — a movie has exactly one ID by construction. Syncing was
  always correct here; this was the display catching up with it.
- **The config page can no longer wipe your exclusion and reviewed lists when it fails to read
  them.** If one of those four lists came back in a form the page could not parse, it was
  treated as empty — and the next save, from any tab, wrote that emptiness back over the real
  thing. On a mature install that is tens of thousands of decisions gone, with no error
  message and nothing in the log. The page now tells the difference between "empty" and
  "unreadable": an unreadable list produces a warning naming it — one you have to dismiss, plus
  a banner that stays at the top of the page until the file is repaired — and is left strictly
  alone on save rather than overwritten. If you have made review decisions on the page while a list is
  unreadable, saving offers to replace the stored value rather than silently dropping your
  work. The sync side already made this distinction; the config page was the last place that
  did not.

- **The de-dup view's count line no longer appears to change on its own.** Two different pieces
  of code wrote that line and counted on two different bases, so clicking a bulk action could
  move the "reviewed" number even when the click provably changed nothing. The line also paired
  a filtered count with unfiltered totals, so with a search or filter active its two halves were
  describing different sets of titles. All the numbers on it now come from the same set, and the
  line says which set that is when a filter has narrowed it.

- **The "shows already on disk" line in the sync log no longer counts season folders.** It walks
  the library recursively, so every `Season 01`, `Season 02` and so on was counted as though it
  were a show — one run reported 934 shows against 881 real ones. Only the number was wrong;
  nothing about which titles the review gate recognised has changed, and it still finds shows
  however your folder mode nests them.

### Changed

- **Bulk actions in the de-dup view now ask before rewriting a very large batch.** "Mark all
  matching", "Select all matching" and their inverses apply to the entire filtered list, not
  just the rows on screen — so with no search active, one click could rewrite every stored
  decision, with no way to undo it from the page. Batches over 500 titles now confirm first
  and say how many titles they will affect. Normal use — search for a show, act on a few — is
  unchanged.
- **"Mark all matching reviewed" and its inverse now leave the titles on screen.** They used to
  re-filter immediately, so with the Unreviewed filter on the batch you just marked vanished the
  instant you clicked — which is exactly when you would want to check it, since none of these
  actions can be undone from the page. They now behave like the bulk include/exclude buttons,
  which have always kept the batch visible. The next search, filter change or reload clears it.

## [1.4.0] - 2026-08-28

### Added

- **New "Only sync movies and series you have reviewed" option in Sync Settings, off by default.** Normally anything
  you haven't excluded gets synced, which is fine until your provider adds content in bulk —
  one overnight addition here was 5,974 titles, and they would all have landed in the library
  before there was any chance to look at them. With this on, a title is written once you have
  reviewed it or excluded it, so new arrivals wait in the de-dup view instead. Holding a title
  is **not** the same as excluding it: nothing is added to your exclusion list, no folder is
  removed, and it stays in the unreviewed list until you decide. A title you already have on
  disk is never held — if your provider reissues it under a new ID, it is recognised from its
  folder, synced as before, and quietly marked reviewed under the new ID, so your review
  decisions survive the provider renumbering things. For series, an existing record of their
  episodes counts as recognition too, so a series your provider has renamed is not withheld
  either. If the reviewed-list setting is ever unreadable the option switches itself off for
  that run and says so in the log, rather than treating everything as unreviewed and holding
  your whole library back.

### Fixed

- **The De-dup view now notices review marks the sync made.** The sync can add to the reviewed
  list on its own (the option above marks a returning film reviewed once it recognises it), and
  the view was only reading that list when the page first opened — so those titles kept showing
  as unreviewed until you reloaded. Pressing Load now picks them up. Anything you have marked or
  excluded on the page but not yet saved is preserved.
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

[Unreleased]: https://github.com/andyj682/emby-xtream-dedupe/compare/dedupe-v1.6.0...HEAD
[1.6.0]: https://github.com/andyj682/emby-xtream-dedupe/compare/dedupe-v1.5.0...dedupe-v1.6.0
[1.5.0]: https://github.com/andyj682/emby-xtream-dedupe/compare/dedupe-v1.4.1...dedupe-v1.5.0
[1.4.1]: https://github.com/andyj682/emby-xtream-dedupe/compare/dedupe-v1.4.0...dedupe-v1.4.1
[1.4.0]: https://github.com/andyj682/emby-xtream-dedupe/compare/dedupe-v1.3.0...dedupe-v1.4.0
[1.3.0]: https://github.com/andyj682/emby-xtream-dedupe/compare/dedupe-v1.2.0...dedupe-v1.3.0
[1.2.0]: https://github.com/andyj682/emby-xtream-dedupe/compare/dedupe-v1.1.1...dedupe-v1.2.0
[1.1.1]: https://github.com/andyj682/emby-xtream-dedupe/compare/dedupe-v1.1.0...dedupe-v1.1.1
[1.1.0]: https://github.com/andyj682/emby-xtream-dedupe/compare/v1.0.0...dedupe-v1.1.0
[1.0.0]: https://github.com/andyj682/emby-xtream-dedupe/releases/tag/v1.0.0
