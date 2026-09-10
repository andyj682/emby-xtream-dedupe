<p align="center">
  <img src="logo.svg" width="180" alt="Xtream Tuner" />
</p>

<h1 align="center">Xtream Tuner</h1>

<p align="center">
  An Emby Server plugin that turns any Xtream-compatible IPTV service into a full Live TV, Movies, and Series library — with EPG, metadata matching, and a built-in dashboard.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Emby-4.8%2B-52B54B?style=flat-square&logo=emby" alt="Emby 4.8+" />
  <img src="https://img.shields.io/badge/.NET-Standard%202.0-512BD4?style=flat-square" alt=".NET Standard 2.0" />
  <img src="https://img.shields.io/badge/License-MIT-blue?style=flat-square" alt="MIT License" />
</p>

---

> **This is a fork of [firestaerter3/emby-xtream](https://github.com/firestaerter3/emby-xtream).**
> It tracks upstream, adding a companion interface that de-duplicates titles across categories
> and tracks reviewed/unreviewed items to make processing large VOD libraries more efficient.
> It's currently most useful if you use Dispatcharr as your XC provider: Dispatcharr normalizes
> titles across providers before they reach Emby, giving movies a single stream ID and series
> an identical name across providers. Those are the two factors that drive this fork's
> de-duplication. While this approach is unlikely to help de-duplicate libraries imported
> directly from IPTV providers, the reviewed/unreviewed aspect of the companion interface will
> function regardless. Extending de-duplication to raw provider libraries is possible future
> work. Everything in the upstream README below still applies.

## What this fork adds

When a provider lists the same title under many categories, a simple per-category view means
reviewing the same movie or show multiple times. This fork adds a title-level review workflow
on top of the existing per-title exclusion:

- **De-duplicated review view** — one row per unique title across your selected categories
  (movies collapse by stream ID, series by name), so a title listed in five categories appears
  once. Search, filter by category, and exclude at the title level.
- **Reviewed checkpoint** — mark titles "reviewed" (a bookmark, separate from excluding), with
  segmented *Show* (All / Included / Excluded) and *Reviewed* (All / Reviewed / Unreviewed) view
  filters — pull up just your worklist (unreviewed), audit everything excluded, and so on. After an
  initial pass through your library, "unreviewed" doubles as "new since I last looked." Per-title
  and bulk.
- **Browse ⇄ De-duplicated review toggle** — switch between the classic per-category tree and
  the de-dup list; both edit the same exclusion list, so switching is lossless, and your choice
  is remembered.
- **Title-level series exclusion** — Dispatcharr gives the same show a distinct ID per category,
  so excluding it in one place can leave copies elsewhere. The sync applies your exclusions to a
  title's whole group of duplicate copies, not just the IDs you ticked, so a copy that appears
  later under a fresh ID is skipped without any action from you. This is based on the fact that Dispatcharr uses the exact same title for the series for every merged copy of it. Titles that Dispatcharr hasn't merged and have different names will appear as different items in the review interface.
- **Only sync movies and series you have reviewed** — normally anything you have not excluded gets
  synced, which is fine until a provider adds content in bulk; one addition during development was
  5,974 movies overnight. With this option on, a title is written only once you have reviewed it or
  excluded it, so new arrivals wait in the de-dup view instead of landing in your library.
  If a title already on disk reappears with a different ID it is automatically marked as reviewed
  and continues to sync. For series, an existing record of their episodes counts as recognition too,
  so a rename is fine as long as the ID is stable. Turning on **metadata IDs in folder names** makes
  this recognition considerably more reliable. If the reviewed-list setting is ever unreadable the
  option disables itself for that run and says so in the log, rather than treating every title as
  unreviewed and holding back your whole library.
- **Sync robustness for duplicates** — cross-listed series collapse to one folder instead of
  writing duplicate per-episode files, and series whose episode list returns empty under load are
  retried so a batch of new titles lands in one sync.
## Related projects

This plugin is one of four small projects that together run a Dispatcharr-backed VOD library in
Emby. Each is useful on its own, but they were built to fit together, and one dependency is worth
stating plainly before you rely on this one.

- **[dispatcharr_vod_concurrency_fix](https://github.com/andyj682/dispatcharr_vod_concurrency_fix)**
  — coalesces the near-simultaneous range requests some clients (notably Emby) make when playing
  MKV VOD files, so they share one provider slot instead of failing over to a different file and
  corrupting playback.
- **[dispatcharr_vod_preferences](https://github.com/andyj682/dispatcharr_vod_preferences)**
  — control over which VOD stream Dispatcharr serves through its proxy for a given title, which
  clients cannot otherwise reach. Composable options to prefer better video or audio quality, or
  to remember a specific stream or provider.
- **[dispatcharr_vod_episode_sweep](https://github.com/andyj682/dispatcharr_vod_episode_sweep)**
  — learns which VOD series a client syncs, then once a day refreshes all of each watched show's
  provider and category relations, so new episodes stop going missing. Scoped to only the series
  you sync; runs in the background.
- **emby-xtream-dedupe** *(this repo)* — generates `.strm` files for Emby from a Dispatcharr
  catalog, de-duplicating titles cross-listed across categories and holding new arrivals for
  review before they reach your library.

### A `.strm` generator alone will not keep your episodes up to date

A `.strm` generator writes files for the episodes Dispatcharr already knows about. It does not —
and cannot — make Dispatcharr go and look for new ones. Dispatcharr refreshes a series' episode
data lazily, and nothing in a normal sync forces that refresh: a healthy, settled library makes no
episode-detail calls at all, precisely because nothing has changed from its point of view.

So without a server-side sweep, new episodes of shows you already have can simply never appear.
The library looks healthy, the syncs report success, and the missing episodes are invisible
because nothing ever asked for them.

[dispatcharr_vod_episode_sweep](https://github.com/andyj682/dispatcharr_vod_episode_sweep) exists
to close exactly that gap, and is designed to run alongside a `.strm` generator like this one. It
learns which series a client actually syncs, so it stays scoped to your library rather than
hammering the whole catalog, and refreshes every provider and category relation of each show
once a day. Any equivalent server-side refresh will do — the requirement is that *something*
periodically makes Dispatcharr re-check its providers for new episodes. If you run this plugin
without one, treat missing episodes as expected rather than as a bug here.

## Features

### Live TV & EPG

Full Live TV integration with Emby's native TV guide.

- **M3U playlist generation** with channel metadata, logos, and EPG channel IDs
- **XMLTV electronic program guide** with configurable fetch window (1-14 days)
- **Category-based filtering** — select which channel groups to include
- **Stream format selection** — MPEG-TS or HLS (M3U8)
- **Adult content filtering** — opt-in toggle for adult-flagged channels
- **DVB subtitle declaration** — optional toggle that surfaces DVB subtitle tracks for live TV without re-enabling stream probing (see [ADR-009](docs/decisions/009-dvb-subtitle-static-declaration.md))
- **Automatic caching** — M3U (15 min) and EPG (30 min) with thread-safe invalidation

### VOD Movie Library

Sync on-demand movies as STRM files that Emby treats as a native movie library.

- **STRM file generation** — one file per movie, Emby handles metadata and artwork
- **Folder organization modes:**
  - **Single folder** — all movies in one `Movies/` directory
  - **Multiple folders** — auto-organized by provider category name
  - **Custom mapping** — define your own folders and assign categories to each
- **TMDB metadata matching** — appends `[tmdbid=123]` to folder names for instant Emby identification
- **TMDB fallback lookup** — queries Emby's metadata providers when the Xtream source lacks a TMDB ID
- **Category selection** — pick specific VOD categories to sync, or sync all
- **Per-title selection** — expand any category (Single Folder mode) to tick individual movies. Unticked titles stop syncing and their folder is removed on the next sync, whether or not orphan cleanup is on
- **Danger zone** — one-click delete of all synced movie content

### TV Series Library

Full series support with proper season/episode structure.

- **Season/Episode STRM files** — `Show Name/Season 01/Show Name - S01E01 - Episode Title.strm`
- **Series detail fetching** — pulls episode lists per series from the Xtream API
- **TVDb / TMDB ID folder naming** — `Show Name [tvdbid=81189]` for reliable metadata matching
- **Manual ID overrides** — force a specific TVDb ID for shows that don't auto-match
- **Metadata fallback lookup** — searches Emby's providers when no ID is available
- **Same folder modes as movies** — single, multiple, or custom category mapping
- **Per-title selection** — same as movies, at whole-series granularity rather than per-episode

### Smart Sync Engine

Efficient sync that doesn't re-download what you already have.

- **Smart skip** — skips writing STRM files that already exist on disk
- **Configurable parallelism** — 1-10 concurrent operations (default 3)
- **Orphan cleanup** — automatically removes STRM files for content no longer in the source
- **Cross-listing deduplication** — movies/series appearing in multiple categories are synced once
- **Content name cleaning** — strips provider prefix tags (e.g. `|UK|`, `|FR|`) and custom terms from titles
- **Real-time progress** — Phase, Total, Completed, Skipped, Failed counters polled every 500ms

### Dispatcharr Integration

Optional integration with [Dispatcharr](https://github.com/Dispatcharr/Dispatcharr) for IPTV stream management.

- **Stream proxy routing** — routes Live TV through Dispatcharr's proxy for connection management
- **Pre-populated media info** — fetches codec, resolution, and bitrate from Dispatcharr's stream stats (requires [Streamflow](https://github.com/krinkuto11/streamflow) configured in Dispatcharr to generate per-channel metadata)
- **FFprobe bypass** — skips Emby's stream analysis when stats are available (faster channel switching)
- **JWT authentication** — automatic token refresh with retry and exponential backoff
- **Graceful fallback** — reverts to direct Xtream URLs if Dispatcharr is unavailable

### Built-in Dashboard

A configuration UI embedded in Emby's plugin settings with five tabs.

- **Dashboard** — last sync status, sync history (last 10), library stats, live progress bar
- **Settings** — server connection, sync tuning, name cleaning, metadata matching
- **Movies** — enable/disable, folder mode, category selection with search, sync button
- **Series** — same layout as movies with series-specific options
- **Live TV** — stream format, EPG settings, catch-up, Dispatcharr, category filtering

---

## Installation

### Step 1: Download the Plugin

Download the DLL matching your Emby Server version from the [latest release](../../releases/latest):

| Your Emby Server | Download |
| --- | --- |
| 4.9.x | `Emby.Xtream.Plugin.dll` |
| 4.10.0.17 and later | `Emby.Xtream.Plugin-4.10.dll` |

Emby 4.10 left beta and reached general release in September 2026, so most installs now want the
second one. The two builds target different Emby SDKs and are not interchangeable — check your
version under **Dashboard → Help → About** if you are unsure. **Install one, not both.**

> Only the single DLL file is needed — no other dependencies.

<details>
<summary><strong>Build from source (alternative)</strong></summary>

Requires .NET SDK 6.0+:

```bash
git clone https://github.com/andyj682/emby-xtream-dedupe.git
cd emby-xtream-dedupe/Emby.Xtream.Plugin
bash build.sh
```

The compiled DLL will be at `Emby.Xtream.Plugin/out/Emby.Xtream.Plugin.dll`. That is the 4.9.x
build; for Emby 4.10 use `dotnet publish -c Release_4_10` from the repository root instead.

</details>

### Step 2: Install the Plugin

Copy the DLL to your Emby Server's plugins directory and restart.

**Docker (most common):**
```bash
docker cp Emby.Xtream.Plugin.dll emby:/config/plugins/
docker restart emby
```

**Bare metal (Linux):**
```bash
cp Emby.Xtream.Plugin.dll /var/lib/emby/plugins/
systemctl restart emby-server
```

**Bare metal (macOS/Windows):**
Copy `Emby.Xtream.Plugin.dll` to your Emby data directory under `plugins/`, then restart Emby Server.

### Step 3: Configure the Plugin

1. Open Emby's web UI
2. Go to **Settings > Plugins > Xtream Tuner**
3. Enter your Xtream server details:
   - **Server URL** — e.g. `http://your-provider:port`
   - **Username** and **Password**
4. Click **Test Connection** to verify
5. Click **Save**

### Step 4: Set Up Live TV

1. Switch to the **Live TV** tab
2. Choose your **Stream Format** (MPEG-TS recommended)
3. Click **Refresh Categories** to load channel groups
4. Select the categories you want
5. Configure **EPG** settings (days to fetch, cache duration)
6. Click **Save**
7. Go to **Emby Settings > Live TV** and add a new tuner:
   - Type: **Xtream Tuner**
   - It will auto-discover the plugin's M3U and EPG endpoints

### Step 5: Set Up Movies (Optional)

1. Switch to the **Movies** tab
2. Check **Enable VOD Movies**
3. Click **Refresh Categories** to load VOD categories
4. Choose a **Folder Organization** mode:
   - **Single Folder** — select categories, all movies go to `Movies/`
   - **Multiple Folders** — one folder per category, auto-named
   - **Custom** — click "Add Folder", name it, assign categories
5. Click **Sync Movies Now**
6. In Emby, add a new **Movies** library pointing to the STRM output path (default: `/config/xtream/Movies`)

### Step 6: Set Up Series (Optional)

1. Switch to the **Series** tab
2. Check **Enable Series / TV Shows**
3. Same workflow as Movies — refresh categories, select, choose folder mode
4. Click **Sync Series Now**
5. In Emby, add a new **TV Shows** library pointing to `/config/xtream/Shows`

### Step 7: Dispatcharr Integration (Optional)

If you use [Dispatcharr](https://github.com/Dispatcharr/Dispatcharr) for stream management:

1. Go to **Live TV** tab > **Dispatcharr** section
2. Check **Enable Dispatcharr**
3. Enter Dispatcharr URL, username, and password
4. Click **Test Dispatcharr** to verify
5. Save — Live TV streams will now route through Dispatcharr's proxy

> **Note:** For the plugin to receive codec/resolution metadata and skip FFprobe, [Streamflow](https://github.com/krinkuto11/streamflow) must be enabled and configured in Dispatcharr. Without Streamflow generating per-channel stream stats, the plugin falls back to standard stream handling.

### Updating the Plugin

Download the latest DLL from [Releases](../../releases/latest), replace the file in your plugins directory, and restart Emby. Your configuration is preserved across updates.

---

## Configuration Reference

| Setting | Default | Description |
|---|---|---|
| **Stream Format** | MPEG-TS | Live TV container format (`ts` or `m3u8`) |
| **Enable DVB Subtitles** | Off | Declare DVB subtitle tracks on every live channel so they appear without stream probing (non-DVB sources will show empty subtitle options) |
| **EPG Cache** | 30 min | How long to cache EPG data (5-1440 min) |
| **EPG Days** | 2 | Days of guide data to fetch (1-14) |
| **M3U Cache** | 15 min | How long to cache channel playlists (1-1440 min) |
| **STRM Library Path** | `/config/xtream` | Where STRM files are written |
| **Smart Skip** | On | Skip existing STRM files during sync |
| **Sync Parallelism** | 3 | Concurrent operations during sync (1-10) |
| **Cleanup Orphans** | Off | Remove STRM files not in source |
| **Only sync movies and series you have reviewed** | Off | Hold titles that are neither reviewed nor excluded instead of writing them. Titles already on disk are exempt and marked reviewed automatically. Works best with metadata IDs in folder names |
| **Refresh Emby libraries after a sync** | On | Tell Emby a library changed once a sync has actually added or removed files |
| **TMDB Folder Naming** | Off | Append `[tmdbid=X]` to movie/series folders |
| **Fallback Lookup** | Off | Query Emby's metadata providers for missing IDs |
| **Name Cleaning** | Off | Strip prefix tags and custom terms from titles |

---

## Diagnostics and recovery scripts

`scripts/` holds standalone Python tools for answering "what does my live data actually say"
without a rebuild or a deploy. They read the plugin's config XML and the provider's **list**
endpoints only — never `get_series_info`, which trips Dispatcharr's gated episode refresh — so
they are safe to run against a working setup. Nothing writes to the live config.

| Script | What it answers |
|---|---|
| `catalogue-snapshot.py` | Records which provider ID was which title, today, plus a census of how many stored IDs no longer exist. Run it periodically; the others consume its output |
| `audit-strm-links.py` | Which `.strm` files point at a stream ID the provider no longer has, split into "the title came back under a new ID" and "the content is gone" |
| `analyse-new-arrivals.py` | Given two snapshots, splits a batch of new arrivals into already-excluded / already-reviewed / already-on-disk / genuinely new — i.e. how much of a review queue is actually new work |
| `repair-id-churn.py` | Re-points exclusions and reviewed marks after a provider reassigns IDs. Dry run by default; `--write` emits a *candidate* config for you to diff and install yourself |
| `find-crosslisted-series.py` | Finds a cross-listed series, reports titles that are only partly excluded, and can emit a minimal test config |
| `xtream_catalogue.py` | Shared helpers (not run directly) |

They assume Docker and a throwaway `python:3-alpine` container, e.g.:

```bash
docker run --rm --network container:emby --memory=512m \
  -v /path/to/emby/config:/cfg:ro \
  -v "$PWD/scripts":/scripts:ro \
  -v "$HOME/xtream-snapshots":/out \
  python:3-alpine python3 /scripts/catalogue-snapshot.py --out /out
```

`--network container:emby` matters if your Xtream base URL is a hostname on a Docker network —
the default bridge cannot resolve it, and the failure looks like a DNS error rather than a
credentials problem. Note also that snapshots contain titles, and the config XML they read
contains your provider credentials in plaintext (see below), so keep both local.

---

## Security Note: Credentials in STRM Files and M3U Output

The Xtream Codes protocol requires the username and password to appear directly in every stream URL — there is no token or session form. As a result, your Xtream credentials end up embedded in plaintext in two places:

- The `.strm` files written under your **STRM Library Path** (default `/config/xtream`)
- The M3U playlist served by the plugin's tuner endpoint

This is a property of the protocol, not a plugin defect — the same is true of any Xtream-based tooling. Anything that can read those files (other containers sharing the volume, backup tools, indexers, users with shell access on the host) can harvest the credentials.

**How to keep credentials off disk:**

- **Use Dispatcharr.** When [Dispatcharr](https://github.com/Dispatcharr/Dispatcharr) is enabled, Live TV and multi-provider VOD URLs route through Dispatcharr's credential-free proxy URLs (`/proxy/ts/stream/<uuid>`, `/proxy/vod/movie/<uuid>`) instead of the raw Xtream form. Single-provider VOD movies and series episodes still fall back to the Xtream URL today.
- **Restrict filesystem permissions.** Make sure the STRM library path is not readable by other users or containers on the host. For Docker, avoid mounting `/config/xtream` into unrelated services.
- **Use a dedicated provider account.** If your Xtream provider supports multiple credential sets per subscription, use a separate one for Emby so it can be rotated independently.

If credential exposure on disk is unacceptable for your environment, Dispatcharr in front of the plugin is currently the only complete answer. Improving credential-free routing for single-provider VOD and series episodes is tracked as a future enhancement.

---

## License

MIT — see [LICENSE](LICENSE).

This is a fork, so the copyright notice carries two lines: one for
[firestaerter3/emby-xtream](https://github.com/firestaerter3/emby-xtream), whose code this is built
on and which declares MIT, and one for the additions made here. Both are under the same terms.
