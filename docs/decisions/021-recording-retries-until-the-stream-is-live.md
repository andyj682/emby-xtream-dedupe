# ADR-021: A Recording Keeps Trying Until the Stream Is Live

**Date**: 2026-10-13
**Status**: Accepted
**Affects**: `XtreamLiveStream.CopyToAsync(Stream, …)` (the recording path)

---

## Context

Sports and PPV event channels often have no live stream until the event begins, and a
recording usually starts a little before that. The plugin made exactly one connection attempt
per recording, so these recordings failed or came out almost empty even when the stream went
live a few minutes into the recording window. Channels that were already live recorded fine.
Reported on Discord.

Decompiling Emby 4.10.1 (`Emby.LiveTV`) showed how Emby handles that one attempt:

- `XtreamLiveStream.SupportsCopyTo` is `true`, so `EmbyTV.GetRecorder` always picks
  `DirectRecorder`, which calls `CopyToAsync(Stream, wallClockStartTime, …)`. That is the only
  caller of this overload. Live viewing goes through `LiveStreamWriter` and the `PipeWriter`
  overload.
- Emby creates the output file (`onStarted`) before it calls `CopyToAsync`.
- The recorder's token is linked to a `CancellationTokenSource(recordingEnd - now)`, so its
  cancellation marks the end of the recording window.
- `EmbyTV` treats an `OperationCanceledException` as "Recording stopped" and marks the recording
  Completed. Only other exceptions get Emby's own retry (after 60 s, up to 10 times, each with
  a new file).

## Problem

All three ways a not-yet-live channel fails ended the recording as Completed, without any retry:

1. **No answer within the header timeout.** The stream HttpClient has a 10 s timeout, which
   surfaces as `TaskCanceledException`, an `OperationCanceledException`. Emby reads that as the
   recording being stopped.
2. **A 200 with a short error packet, then end of stream.** `CopyToAsync` returned normally
   after writing the packet into the file.
3. **A non-2xx status** was the only case that reached Emby's 60 s retry.

## Alternatives Considered

### 1. Rely on Emby's own retry

**Rejected**: it only runs for non-2xx statuses, and it starts a new file every time.

### 2. A fixed number of attempts

**Rejected**: an event can go live any time within the recording window, and no fixed count
fits every window.

### 3. Retry in both CopyToAsync overloads

**Rejected**: the `PipeWriter` overload is live viewing. Someone flipping channels should get a
fast failure on a dead channel, not minutes of retries.

## Decision

The recording overload keeps trying until its token is cancelled:

- Each attempt is limited to `RecordingAttemptTimeout` (30 s) to deliver headers and the first
  data. A timeout, a non-2xx status, a connection error, or an end of stream before the
  threshold counts as "not live yet". The next attempt follows after `RecordingRetryInterval`
  (10 s). A `Retry-After` header is honoured, kept between that interval and 60 s.
- An attempt is live only once it has delivered `RecordingLiveThresholdBytes` (64 KB). Until then
  its bytes are held back, so an error packet never reaches the file.
- When a live stream drops before the window ends, it reconnects after
  `RecordingReconnectDelay` (1 s) and appends to the same file.
- A cancellation that does not come from the recording token is turned into a retry, never let
  through to Emby.
- When the window ends with no data at all, an Error is logged with the attempt count and the
  last reason, and the cancellation is rethrown as Emby expects. Emby then deletes the empty file.
- Retries log at Info for the first minute, then once a minute.

## Consequences

- A recording that starts before the event goes live picks it up within about 10 s of it going
  live, in the same file.
- A brief upstream drop during a recording no longer ends it.
- Live viewing is unchanged.
- **Dispatcharr risk, not tested.** A retry against a Dispatcharr proxy URL means a connection
  attempt every 10 s. The project notes record that a client timing out on
  `/proxy/ts/stream/<uuid>` can leave the channel stuck in CLOSE-WAIT. The interval is fixed
  (there are no quick first attempts) and every failed attempt is disposed before the next, but
  this has not been run against Dispatcharr with a channel that is not live. If it causes stuck
  channels, the first thing to try is a longer interval for proxy URLs.
- The 10 s `HttpClient.Timeout` on the stream client still applies to each attempt's headers,
  as it already does for live viewing. `RecordingAttemptTimeout` only matters beyond it.
