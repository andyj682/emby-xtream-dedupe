# ADR-017: Ship One DLL for Emby 4.9 and 4.10

**Date**: 2026-09-27
**Status**: Accepted (replaces the dual build from PR #50)
**Affects**: `XtreamLiveStream.AddConsumer/RemoveConsumer`, `.github/workflows/release.yml`, `.github/workflows/ci.yml`, `scripts/sdk-load-check/`

---

## Context

Emby 4.10.0.17 added `AddConsumer(string)` and `RemoveConsumer(string)` to `ILiveStream`. PR #50
answered that with two builds from one source tree:

- `Release`, compiled against the 4.9 SDK with System.Text.Json / System.IO.Pipelines v6,
  published as `Emby.Xtream.Plugin.dll`.
- `Release_4_10`, compiled against the 4.10 SDK with the v8 packages and `EMBY_4_10` defined,
  published as `Emby.Xtream.Plugin-4.10.dll`.

The two methods were only compiled into the 4.10 build, behind `#if EMBY_4_10`.

Emby 4.10.0.40 then became the public stable release (issue #70). Users whose server updated
itself were left with the 4.9 DLL, which fails to load:

```
Method 'AddConsumer' in type 'Emby.Xtream.Plugin.Service.XtreamLiveStream' from assembly
'Emby.Xtream.Plugin, Version=1.4.96.0' does not have an implementation.
```

## Problem

Two builds need every user to pick the right file, and to pick again whenever their server
crosses the 4.9 → 4.10 line. The in-plugin updater always downloads `Emby.Xtream.Plugin.dll`,
so a 4.10 user who pressed update was swapped back to the 4.9 build and broke.

Fixing the updater does not help the next update. The installed DLL decides which file to fetch,
and every released version fetches `Emby.Xtream.Plugin.dll`. Whichever build sits under that
name, one group breaks on the update that introduces the fix.

## Alternatives Considered

### 1. Each build updates with its own asset name

`#if EMBY_4_10` picks `Emby.Xtream.Plugin-4.10.dll` in `UpdateChecker`.

**Rejected**: it only takes effect for the update after the one that ships it, and it does
nothing for a server that moves from 4.9 to 4.10 while the plugin is installed.

### 2. Make the 4.10 build the default

Swap the asset names, since 4.10 is now stable.

**Rejected**: every 4.9 user who presses update gets a DLL built against the v8 packages, which
is the breakage #43 was about.

### 3. Pick the asset from the running server version

**Rejected**: same one-update delay as alternative 1, and still two files to keep apart.

## Decision

Compile `AddConsumer` and `RemoveConsumer` into every build, and ship the `Release` (4.9 SDK)
build as the only DLL.

This works because the runtime, not the compiler, matches a class's public virtual methods to
the interface it implements, by name and signature. On 4.9 `ILiveStream` has no such members,
so the methods are ordinary unused methods. On 4.10 the runtime finds them and the type loads.
The v6 package references resolve to the newer versions the 4.10 host already provides.

The release still attaches the file twice, as `Emby.Xtream.Plugin.dll` and
`Emby.Xtream.Plugin-4.10.dll`, so both older update paths and old download instructions land on
a DLL that loads.

## Verification

`scripts/sdk-load-check` loads a plugin DLL against one SDK directory, resolving only
`MediaBrowser.*` from it, then loads every type and JIT-compiles every method. A missing
interface member fails type loading; a removed or changed SDK member fails compilation of the
method that calls it.

| DLL | 4.9 SDK | 4.10 SDK |
|---|---|---|
| `Release` build before this change | not run | 1 failure: the `AddConsumer` error above |
| `Release` build after this change | 209 types, 1241 methods, 0 failures | 209 types, 1241 methods, 0 failures |

## Consequences

- One file for everyone. A server upgrade across 4.9 → 4.10 no longer breaks the plugin.
- Users already broken by the 4.10 upgrade need one manual download. Their plugin does not load,
  so it cannot update itself.
- The compiler no longer checks the shipped DLL against 4.10. CI and the release workflow cover
  that gap by compiling the `Release_4_10` configuration and running the load check against both
  SDKs; the release stops if either fails.
- The load check skips generic methods that are never instantiated, and it cannot see behaviour
  differences between Emby versions, only binding failures. Each release should still be tried on
  a real 4.10 server before it is promoted to stable.
- When a future Emby SDK changes an interface in a way the 4.9-compiled DLL cannot satisfy, the
  load check fails and the choice between one DLL and two has to be made again.
