# ADR-022: Optional Dispatcharr Xtream URL for Playback

**Date**: 2026-10-16
**Status**: Accepted
**Affects**: `XtreamTunerHost.ResolveStreamUrl()`, `PluginConfiguration.DispatcharrStreamEndpoint`

---

## Context

A channel that Dispatcharr knows (it has a UUID) plays from `/proxy/ts/stream/{uuid}`.
Dispatcharr lists those connections as an anonymous client, so its stats cannot show which
Emby server is watching, and per-user stream limits do not apply. Reported on Discord by a
user who runs two Emby servers, each with its own Dispatcharr user, and whose Xtream
connection in the plugin already points at Dispatcharr.

Dispatcharr's Xtream URL (`/live/{user}/{pass}/{id}.ts`) is tied to a Dispatcharr user.
ADR-001 found it runs through the same proxy code (`ts_proxy` → `stream_manager` → Redis
buffer) as the proxy URL.

## Problem

ADR-001 switched playback to the Xtream URL to escape what looked like proxy bugs, and was
reverted with a note not to re-implement it. Its premise was wrong: the faults came from a
corrupted Dispatcharr install, and the Xtream URL had no reliability advantage. That leaves
the question of whether the Xtream URL can be offered for a different reason, user
attribution, without bringing back the confusion.

## Alternatives Considered

### 1. Turn the Dispatcharr integration off

The plugin then plays from the Xtream connection, which already reaches Dispatcharr in this
setup. **Rejected**: it also drops stream stats, profile codec detection, channel numbers and
profile filtering, all of which come from the Dispatcharr API.

### 2. Build the Xtream URL from the Dispatcharr URL and Dispatcharr login

**Rejected**: the REST login and the Xtream password are separate in Dispatcharr, and when the
Xtream connection points at the provider, the plugin's stream ID is the provider's, not
Dispatcharr's channel ID.

### 3. Always use the Xtream URL

**Rejected**: the proxy URL is what everyone runs today and works.

## Decision

An opt-in setting, *Plugin Config → Dispatcharr → Stream URL*, default **Dispatcharr proxy**.

- With **Xtream connection above**, a channel with a UUID plays from
  `{BaseUrl}/live/{Username}/{Password}/{streamId}.ts`, the Xtream connection's own URL.
- That URL is only used when `BaseUrl` and `DispatcharrUrl` have the same scheme, host, port and path (a reverse proxy can serve two services on one host).
  In that setup the plugin's stream ID is Dispatcharr's channel ID. Otherwise the URL would skip
  Dispatcharr (and any VPN or stream limits it applies), so the proxy URL is kept and a warning is
  logged once.
- The stream is still treated as a Dispatcharr stream: probing stays off (the teardown rule in
  AGENTS.md) and the stream profile's codec wins (ADR-016). Both are true of the Xtream URL
  because it runs through the same proxy code.
- The extension is always `.ts`, whatever *Stream Format* says, matching the proxy URL
  and the `mpegts` container the media source declares.
- Channels without a UUID behave as before: fallback to the direct Xtream URL, or skipped.

## Consequences

- Dispatcharr's stats show the Dispatcharr user of the plugin's Xtream connection, one per Emby
  server, not each Emby user.
- The Xtream password is in the stream URL. The plugin never logs the URL on this path, but Emby
  and ffmpeg may log the media source path, as they already do for direct Xtream setups.
- Native clients that direct-stream are handed `MediaSource.Path`, so on this route they receive
  the Dispatcharr user's Xtream username and password, which also work on `player_api.php`.
  Direct Xtream setups already hand clients the provider's credentials the same way, so this is
  not new exposure, but it is a reason the setting is opt-in.
- A user whose Xtream connection reaches Dispatcharr by a different hostname than the Dispatcharr
  URL (an IP for one, a name for the other) gets the proxy URL. The config page shows a warning
  under the setting when the two addresses differ, and the server log warns once. Using the same
  address in both fields fixes it.
- Not run against a real Dispatcharr yet.
