using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;

namespace Emby.Xtream.Plugin.Service
{
    internal class XtreamLiveStream : ILiveStream, IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private HttpResponseMessage _response;
        private Stream _stream;
        private bool _disposed;
        private bool _needsReconnect;

        public XtreamLiveStream(MediaSourceInfo mediaSource, string tunerHostId, HttpClient httpClient, ILogger logger = null)
        {
            MediaSource = mediaSource;
            _httpClient = httpClient;
            _logger = logger;
            UniqueId = Guid.NewGuid().ToString("N");
            TunerHostId = tunerHostId;
            OriginalStreamId = mediaSource.Id;
            DateOpened = DateTimeOffset.UtcNow;
        }

        private int _consumerCount;

        public int ConsumerCount
        {
            get => Volatile.Read(ref _consumerCount);
            set => Volatile.Write(ref _consumerCount, Math.Max(0, value));
        }

        public string OriginalStreamId { get; set; }
        public string TunerHostId { get; }
        public bool EnableStreamSharing => false;
        public MediaSourceInfo MediaSource { get; set; }
        public string UniqueId { get; }
        public DateTimeOffset DateOpened { get; }
        public bool SupportsCopyTo => true;

        // Open() is a no-op: Emby calls it to register the tuner host lifecycle
        // (RequiresOpening/RequiresClosing), but for browser HLS playback ffmpeg
        // opens its own connection directly. Connecting here would create a second
        // idle connection in Dispatcharr. The actual HTTP connection is deferred to
        // the first CopyToAsync() call via ConnectAsync().
        public Task Open(CancellationToken openCancellationToken)
        {
            _logger?.Info("[XtreamLiveStream] Open called (deferred connect)");
            return Task.CompletedTask;
        }

        private async Task ConnectAsync(CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            _response = await _httpClient.GetAsync(
                MediaSource.Path,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            _logger?.Info("[stream-timing] Connect.HttpGet={0}ms status={1}", sw.ElapsedMilliseconds, (int)_response.StatusCode);
            sw.Restart();

            _response.EnsureSuccessStatusCode();
            _stream = await _response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            _logger?.Info("[stream-timing] Connect.StreamReady={0}ms", sw.ElapsedMilliseconds);
        }

        public Task Close()
        {
            Dispose();
            return Task.CompletedTask;
        }

        // Emby 4.10 added AddConsumer/RemoveConsumer to ILiveStream. They are compiled into every
        // build, not only the 4.10 one: on 4.9 nothing calls them, and on 4.10 the runtime matches
        // them to the interface by name and signature. Without them the 4.9 build fails to load on
        // 4.10 ("Method 'AddConsumer' ... does not have an implementation"). See ADR-017.
        public virtual void AddConsumer(string id)
        {
            Interlocked.Increment(ref _consumerCount);
        }

        public virtual void RemoveConsumer(string id)
        {
            while (true)
            {
                var current = Volatile.Read(ref _consumerCount);
                if (current <= 0) return;

                if (Interlocked.CompareExchange(ref _consumerCount, current - 1, current) == current)
                    return;
            }
        }

        // Reopen the HTTP connection to the upstream source. Called when a prior CopyToAsync
        // was cancelled mid-read, which aborts the underlying SSL connection and leaves _stream
        // in a disposed state. RequiresOpening=true means the same XtreamLiveStream is reused
        // across the iOS/Apple TV player's probe connection (~500 ms) and its real playback
        // connection, so we must be able to reconnect after the probe disconnects.
        private async Task ReopenStreamAsync(CancellationToken cancellationToken)
        {
            _stream?.Dispose();
            _response?.Dispose();
            _stream = null;
            _response = null;
            _needsReconnect = false;

            _response = await _httpClient.GetAsync(
                MediaSource.Path,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            _response.EnsureSuccessStatusCode();
            _stream = await _response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            _logger?.Info("[stream-timing] Reconnected to upstream after client disconnect");
        }

        public async Task CopyToAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            if (_stream == null && !_needsReconnect)
                await ConnectAsync(cancellationToken).ConfigureAwait(false);

            if (_needsReconnect)
                await ReopenStreamAsync(cancellationToken).ConfigureAwait(false);

            var buffer = new byte[262144];
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int bytesRead = await _stream.ReadAsync(
                        buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);

                    if (bytesRead == 0) break;

                    var writeBuffer = writer.GetMemory(bytesRead);
                    buffer.AsMemory(0, bytesRead).CopyTo(writeBuffer);
                    writer.Advance(bytesRead);

                    var flushResult = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (flushResult.IsCompleted) break;
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected mid-read. Mark for reconnect so the next CopyToAsync
                // call (e.g. the player's real connection after a probe) opens a fresh
                // upstream connection instead of reading from the now-aborted SSL stream.
                _needsReconnect = true;
                throw;
            }
            finally
            {
                await writer.CompleteAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Wait between attempts while the channel is not live yet.</summary>
        internal TimeSpan RecordingRetryInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Wait before reconnecting after a live stream dropped.</summary>
        internal TimeSpan RecordingReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>Longest an attempt may take to deliver headers and the live threshold.</summary>
        internal TimeSpan RecordingAttemptTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Bytes an attempt must deliver before any of it is written to the recording.</summary>
        internal int RecordingLiveThresholdBytes { get; set; } = 64 * 1024;

        private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

        // Only Emby's DirectRecorder calls this overload; live viewing uses the PipeWriter one.
        // A recording often starts before an event channel is live, so instead of one attempt
        // this keeps trying until the recording window ends, which is when Emby cancels the
        // token. Without it a timeout or an early end of stream ends the recording as
        // "Completed" with an empty file, and Emby never retries. See ADR-021.
        public async Task CopyToAsync(
            Stream writer,
            DateTimeOffset? wallClockStartTime,
            Action<SegmentedStreamSegmentInfo> onSegmentWritten,
            CancellationToken cancellationToken)
        {
            long totalWritten = 0;
            int attempt = 0;
            string lastReason = null;
            try
            {
                while (true)
                {
                    attempt++;
                    var result = await RecordAttemptAsync(writer, cancellationToken).ConfigureAwait(false);
                    totalWritten += result.Written;

                    TimeSpan delay;
                    if (result.Written > 0)
                    {
                        delay = RecordingReconnectDelay;
                        _logger?.Info("[XtreamLiveStream] Recording stream ended after {0} bytes ({1}), reconnecting", result.Written, result.Reason);
                    }
                    else
                    {
                        lastReason = result.Reason;
                        delay = ClampRetryDelay(result.RetryAfter);
                        if (attempt <= 6 || attempt % 6 == 0)
                        {
                            _logger?.Info("[XtreamLiveStream] Recording attempt {0}: stream not live yet ({1}), retrying in {2}s",
                                attempt, result.Reason, (int)delay.TotalSeconds);
                        }
                    }

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (totalWritten == 0)
                {
                    _logger?.Error("[XtreamLiveStream] Recording window ended after {0} attempts without the stream going live. Last result: {1}",
                        attempt, lastReason ?? "none");
                }

                throw;
            }
            finally
            {
                DisposeConnection();
            }
        }

        internal TimeSpan ClampRetryDelay(TimeSpan? retryAfter)
        {
            var delay = retryAfter ?? RecordingRetryInterval;
            if (delay < RecordingRetryInterval) delay = RecordingRetryInterval;
            if (delay > MaxRetryAfter) delay = MaxRetryAfter;
            return delay;
        }

        // One connection to the upstream. Bytes are held back until RecordingLiveThresholdBytes
        // have arrived, so an error packet from a channel that is not live never reaches the
        // file. Only the recording token's cancellation escapes as an exception; every other
        // failure comes back as a result so the caller can retry.
        private async Task<RecordAttemptResult> RecordAttemptAsync(Stream output, CancellationToken cancellationToken)
        {
            DisposeConnection();
            long written = 0;
            using (var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                attemptCts.CancelAfter(RecordingAttemptTimeout);
                try
                {
                    _response = await _httpClient.GetAsync(
                        MediaSource.Path,
                        HttpCompletionOption.ResponseHeadersRead,
                        attemptCts.Token).ConfigureAwait(false);

                    if (!_response.IsSuccessStatusCode)
                    {
                        return RecordAttemptResult.NotLive(
                            "HTTP " + (int)_response.StatusCode,
                            GetRetryAfter(_response));
                    }

                    _stream = await _response.Content.ReadAsStreamAsync().ConfigureAwait(false);

                    var buffer = new byte[262144];
                    var held = new MemoryStream();
                    var readToken = attemptCts.Token;
                    while (true)
                    {
                        int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, readToken).ConfigureAwait(false);
                        if (bytesRead == 0) break;

                        if (held == null)
                        {
                            await output.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                            written += bytesRead;
                            continue;
                        }

                        held.Write(buffer, 0, bytesRead);
                        if (held.Length < RecordingLiveThresholdBytes) continue;

                        held.Position = 0;
                        await held.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
                        written = held.Length;
                        held = null;
                        // Live from here on: only the end of the recording window bounds reads.
                        readToken = cancellationToken;
                        _logger?.Info("[XtreamLiveStream] Recording stream is live");
                    }

                    return written > 0
                        ? RecordAttemptResult.Ended(written, "end of stream")
                        : RecordAttemptResult.NotLive("stream ended after " + (held?.Length ?? 0) + " bytes", null);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // HttpClient.Timeout and the attempt timeout both surface as cancellation.
                    // Emby would read that as the recording being stopped, so it must not escape.
                    return written > 0
                        ? RecordAttemptResult.Ended(written, "timed out")
                        : RecordAttemptResult.NotLive("no data within " + (int)RecordingAttemptTimeout.TotalSeconds + "s", null);
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException)
                {
                    return written > 0
                        ? RecordAttemptResult.Ended(written, ex.Message)
                        : RecordAttemptResult.NotLive(ex.Message, null);
                }
            }
        }

        private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter == null) return null;
            if (retryAfter.Delta.HasValue) return retryAfter.Delta.Value;
            if (retryAfter.Date.HasValue) return retryAfter.Date.Value - DateTimeOffset.UtcNow;
            return null;
        }

        private void DisposeConnection()
        {
            _stream?.Dispose();
            _response?.Dispose();
            _stream = null;
            _response = null;
        }

        private struct RecordAttemptResult
        {
            public long Written;
            public string Reason;
            public TimeSpan? RetryAfter;

            public static RecordAttemptResult NotLive(string reason, TimeSpan? retryAfter) =>
                new RecordAttemptResult { Reason = reason, RetryAfter = retryAfter };

            public static RecordAttemptResult Ended(long written, string reason) =>
                new RecordAttemptResult { Written = written, Reason = reason };
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _stream?.Dispose();
                _response?.Dispose();
                _httpClient?.Dispose();
                _disposed = true;
            }
        }
    }
}
