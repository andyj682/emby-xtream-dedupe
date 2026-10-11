using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Some providers gzip their JSON even when the request did not ask for it. Without
    /// decompression the sync reads the raw gzip bytes and fails on the first category request
    /// with "'0x1F' is an invalid start of a value" (#83). These run against a real loopback
    /// socket because <see cref="FakeHttpHandler"/> replaces the handler that does the decoding.
    /// </summary>
    public class GzipProviderResponseTests
    {
        private const string CategoriesJson = "[{\"category_id\":\"1\",\"category_name\":\"Movies\"}]";

        [Fact]
        public async Task ProviderHandler_DecodesGzipBody()
        {
            using (var server = new GzipServer(CategoriesJson))
            using (var client = new HttpClient(XtreamRateLimitHandler.CreateDefault()))
            {
                var body = await client.GetStringAsync(server.Url);
                Assert.Equal(CategoriesJson, body);
            }
        }

        [Fact]
        public async Task PlainHandler_ReturnsRawGzipBytes()
        {
            // Control: proves the server really sends gzip, so the test above is not passing
            // on an uncompressed body.
            using (var server = new GzipServer(CategoriesJson))
            using (var client = new HttpClient(new HttpClientHandler()))
            {
                var bytes = await client.GetByteArrayAsync(server.Url);
                Assert.Equal(0x1F, bytes[0]);
            }
        }

        /// <summary>Answers one request with a gzipped body, ignoring Accept-Encoding.</summary>
        private sealed class GzipServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Task _serve;

            public GzipServer(string json)
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/player_api.php";
                _serve = ServeOnceAsync(json);
            }

            public string Url { get; }

            private async Task ServeOnceAsync(string json)
            {
                using (var tcp = await _listener.AcceptTcpClientAsync())
                using (var stream = tcp.GetStream())
                {
                    var reader = new StreamReader(stream, Encoding.ASCII);
                    string line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) { }

                    byte[] gz;
                    using (var ms = new MemoryStream())
                    {
                        using (var gzip = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
                        {
                            var raw = Encoding.UTF8.GetBytes(json);
                            gzip.Write(raw, 0, raw.Length);
                        }
                        gz = ms.ToArray();
                    }

                    var header = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: application/json\r\n" +
                        "Content-Encoding: gzip\r\n" +
                        $"Content-Length: {gz.Length}\r\n" +
                        "Connection: close\r\n\r\n");
                    await stream.WriteAsync(header, 0, header.Length);
                    await stream.WriteAsync(gz, 0, gz.Length);
                }
            }

            public void Dispose()
            {
                _listener.Stop();
                try { _serve.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            }
        }
    }
}
