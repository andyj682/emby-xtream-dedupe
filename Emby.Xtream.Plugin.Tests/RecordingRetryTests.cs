using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using Emby.Xtream.Plugin.Tests.Fakes;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// A recording often starts before an event channel is live. The recording overload of
    /// CopyToAsync keeps trying until Emby cancels the token at the end of the recording window,
    /// and never writes the error packets a channel sends before it is live. See ADR-021.
    /// </summary>
    public class RecordingRetryTests
    {
        private const int Threshold = 1024;

        [Fact]
        public async Task ErrorStatusAndShortPacket_AreRetried_AndKeptOutOfTheRecording()
        {
            var live = Bytes(4096, 0x47);
            var handler = new ScriptedHandler(
                _ => Status(HttpStatusCode.ServiceUnavailable),
                _ => Body(Bytes(200, 0xEE)),
                _ => Body(live));

            var output = await RecordUntilRequests(handler, 4);

            Assert.Equal(live, output);
        }

        [Fact]
        public async Task HeaderTimeout_IsRetried_NotTreatedAsTheRecordingStopping()
        {
            var live = Bytes(4096, 0x47);
            var handler = new ScriptedHandler(
                async ct => { await Task.Delay(Timeout.Infinite, ct); return null; },
                _ => Body(live));

            var output = await RecordUntilRequests(handler, 3);

            Assert.Equal(live, output);
        }

        [Fact]
        public async Task LiveStreamThatDrops_IsReconnected_AndAppended()
        {
            var first = Bytes(2048, 0x01);
            var second = Bytes(2048, 0x02);
            var handler = new ScriptedHandler(_ => Body(first), _ => Body(second));

            var output = await RecordUntilRequests(handler, 3);

            Assert.Equal(first.Concat(second).ToArray(), output);
        }

        [Fact]
        public async Task StoppingDuringBackoff_ReturnsPromptly_WithoutAnotherRequest()
        {
            var handler = new ScriptedHandler(_ => Status(HttpStatusCode.ServiceUnavailable));
            using (var stream = CreateStream(handler, new RecordingLogger()))
            using (var cts = new CancellationTokenSource())
            {
                stream.RecordingRetryInterval = TimeSpan.FromMinutes(10);
                var output = new MemoryStream();
                var task = stream.CopyToAsync(output, null, null, cts.Token);

                await handler.WaitForRequests(1);
                cts.Cancel();

                var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
                Assert.Same(task, finished);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
                Assert.Equal(1, handler.RequestCount);
            }
        }

        [Fact]
        public async Task WindowEndingWithoutData_LogsAnError_AndLeavesTheFileEmpty()
        {
            var logger = new RecordingLogger();
            var handler = new ScriptedHandler(_ => Status(HttpStatusCode.NotFound));
            using (var stream = CreateStream(handler, logger))
            using (var cts = new CancellationTokenSource())
            {
                var output = new MemoryStream();
                var task = stream.CopyToAsync(output, null, null, cts.Token);

                await handler.WaitForRequests(3);
                cts.Cancel();

                // OperationCanceledException is what Emby expects when the window ends.
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
                Assert.Equal(0, output.Length);
                Assert.Contains(logger.Errors, e => e.Contains("without the stream going live") && e.Contains("HTTP 404"));
            }
        }

        [Fact]
        public async Task FailedWriteToTheRecordingFile_ReachesEmby_WithoutRetrying()
        {
            // A full disk must surface as an error, not be retried as if the provider failed.
            var handler = new ScriptedHandler(_ => Body(Bytes(4096, 0x47)));
            using (var stream = CreateStream(handler, new RecordingLogger()))
            using (var safety = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                // The safety token turns a regression (retrying forever) into a failure, not a hang.
                var output = new FailingStream();
                await Assert.ThrowsAsync<IOException>(() => stream.CopyToAsync(output, null, null, safety.Token));
                Assert.Equal(1, handler.RequestCount);
            }
        }

        [Fact]
        public async Task SourceThatSendsABurstAndCloses_IsNotReconnectedEverySecond()
        {
            // More than the threshold, then closed straight away: a slate, or a provider
            // dropping the session. Reconnects must follow the steady interval.
            var handler = new ScriptedHandler(_ => Body(Bytes(2048, 0x47)));
            using (var stream = CreateStream(handler, new RecordingLogger()))
            using (var cts = new CancellationTokenSource())
            {
                stream.RecordingReconnectDelay = TimeSpan.FromMilliseconds(1);
                stream.RecordingRetryInterval = TimeSpan.FromMilliseconds(300);
                var task = stream.CopyToAsync(new MemoryStream(), null, null, cts.Token);

                await Task.Delay(TimeSpan.FromMilliseconds(1000));
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

                // ~4 at a 300 ms interval; hundreds if the 1 ms reconnect delay were used.
                Assert.InRange(handler.RequestCount, 2, 6);
            }
        }

        [Theory]
        [InlineData(null, 10)]
        [InlineData(2, 10)]
        [InlineData(30, 30)]
        [InlineData(600, 60)]
        public void RetryAfter_IsHonoured_WithinTheRetryIntervalAndOneMinute(int? retryAfterSeconds, int expectedSeconds)
        {
            using (var stream = CreateStream(new ScriptedHandler(_ => Status(HttpStatusCode.OK)), null))
            {
                stream.RecordingRetryInterval = TimeSpan.FromSeconds(10);
                var retryAfter = retryAfterSeconds.HasValue ? TimeSpan.FromSeconds(retryAfterSeconds.Value) : (TimeSpan?)null;

                Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), stream.ClampRetryDelay(retryAfter));
            }
        }

        [Fact]
        public async Task LiveViewing_StillMakesASingleAttempt()
        {
            // Channel flipping must fail fast; only recordings retry.
            var handler = new ScriptedHandler(_ => Status(HttpStatusCode.ServiceUnavailable));
            using (var stream = CreateStream(handler, null))
            {
                var pipe = new Pipe();
                await Assert.ThrowsAsync<HttpRequestException>(() => stream.CopyToAsync(pipe.Writer, CancellationToken.None));
                Assert.Equal(1, handler.RequestCount);
            }
        }

        private static async Task<byte[]> RecordUntilRequests(ScriptedHandler handler, int requests)
        {
            using (var stream = CreateStream(handler, new RecordingLogger()))
            using (var cts = new CancellationTokenSource())
            {
                var output = new MemoryStream();
                var task = stream.CopyToAsync(output, null, null, cts.Token);

                await handler.WaitForRequests(requests);
                cts.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
                return output.ToArray();
            }
        }

        private static XtreamLiveStream CreateStream(ScriptedHandler handler, RecordingLogger logger)
        {
            return new XtreamLiveStream(
                new MediaSourceInfo { Id = "stream-1", Path = "http://provider.invalid/live/u/p/1.ts" },
                "tuner-1",
                new HttpClient(handler),
                logger)
            {
                RecordingRetryInterval = TimeSpan.FromMilliseconds(10),
                RecordingReconnectDelay = TimeSpan.FromMilliseconds(10),
                RecordingAttemptTimeout = TimeSpan.FromMilliseconds(200),
                RecordingLiveThresholdBytes = Threshold,
            };
        }

        private static byte[] Bytes(int length, byte value) => Enumerable.Repeat(value, length).ToArray();

        private static Task<HttpResponseMessage> Status(HttpStatusCode status) =>
            Task.FromResult(new HttpResponseMessage(status));

        private static Task<HttpResponseMessage> Body(byte[] body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });

        private sealed class FailingStream : MemoryStream
        {
            public override void Write(byte[] buffer, int offset, int count) => throw new IOException("disk full");

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                throw new IOException("disk full");

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
                throw new IOException("disk full");
        }

        /// <summary>
        /// Answers requests from a script in order. A one-entry script answers every request;
        /// a longer one holds every request after its end open until it is cancelled.
        /// </summary>
        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly Func<CancellationToken, Task<HttpResponseMessage>>[] _script;
            private readonly List<TaskCompletionSource<bool>> _waiters = new List<TaskCompletionSource<bool>>();
            private int _count;

            public ScriptedHandler(params Func<CancellationToken, Task<HttpResponseMessage>>[] script)
            {
                _script = script;
            }

            public int RequestCount => Volatile.Read(ref _count);

            public Task WaitForRequests(int n)
            {
                lock (_waiters)
                {
                    if (_count >= n) return Task.CompletedTask;
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters.Add(tcs);
                    return WaitUntil(tcs, n);
                }
            }

            private async Task WaitUntil(TaskCompletionSource<bool> tcs, int n)
            {
                while (RequestCount < n)
                {
                    var timeout = Task.Delay(TimeSpan.FromSeconds(10));
                    if (await Task.WhenAny(tcs.Task, timeout) == timeout)
                        throw new TimeoutException($"Expected {n} requests, saw {RequestCount}");
                    lock (_waiters)
                    {
                        _waiters.Remove(tcs);
                        tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _waiters.Add(tcs);
                    }
                }
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                int index;
                lock (_waiters)
                {
                    index = _count++;
                    foreach (var w in _waiters) w.TrySetResult(true);
                }

                if (_script.Length == 1)
                    return await _script[0](cancellationToken);

                if (index >= _script.Length)
                {
                    // Past the script: hold the request open so the test decides when it ends.
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    return null;
                }

                return await _script[index](cancellationToken);
            }
        }
    }
}
