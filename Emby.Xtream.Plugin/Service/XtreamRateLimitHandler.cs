using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// DelegatingHandler that throttles outbound requests to the configured Xtream
    /// provider host to a maximum of <c>XtreamRequestsPerSecond</c> requests per
    /// second. Requests to other hosts (GitHub, TMDb, Dispatcharr, etc.) pass through.
    /// </summary>
    public class XtreamRateLimitHandler : DelegatingHandler
    {
        private static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private static DateTime _nextAllowedUtc = DateTime.MinValue;

        /// <summary>
        /// The handler chain every provider HttpClient is built on. Decompression is on because
        /// some providers gzip their JSON whether or not the request asked for it, and an
        /// undecoded body fails to parse with "'0x1F' is an invalid start of a value" (#83).
        /// </summary>
        public static XtreamRateLimitHandler CreateDefault()
        {
            return new XtreamRateLimitHandler { InnerHandler = CreateInnerHandler() };
        }

        internal static HttpClientHandler CreateInnerHandler()
        {
            return new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var config = Plugin.InstanceOrNull?.Configuration;
            int rps = config?.XtreamRequestsPerSecond ?? 0;

            if (rps <= 0 || request.RequestUri == null || !IsXtreamHost(request.RequestUri, config?.BaseUrl))
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }

            var minInterval = TimeSpan.FromMilliseconds(1000.0 / rps);
            TimeSpan delay = TimeSpan.Zero;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = DateTime.UtcNow;
                if (now < _nextAllowedUtc)
                {
                    delay = _nextAllowedUtc - now;
                }
                _nextAllowedUtc = (now > _nextAllowedUtc ? now : _nextAllowedUtc) + minInterval;
            }
            finally
            {
                _gate.Release();
            }

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        private static bool IsXtreamHost(Uri requestUri, string baseUrl)
        {
            if (string.IsNullOrEmpty(baseUrl)) return false;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)) return false;
            return string.Equals(requestUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
                && requestUri.Port == baseUri.Port;
        }
    }
}
