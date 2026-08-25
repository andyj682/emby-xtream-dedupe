using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using Emby.Xtream.Plugin.Service;
using Emby.Xtream.Plugin.Tests.Fakes;
using MediaBrowser.Model.Logging;

namespace Emby.Xtream.Plugin.Tests
{
    public abstract class SyncTestBase : IDisposable
    {
        protected readonly FakeHttpHandler Handler;
        protected readonly HttpClient HttpClient;
        protected readonly TempDirectory TempDir;
        protected int SaveConfigCallCount;

        protected SyncTestBase()
        {
            Handler = new FakeHttpHandler();
            HttpClient = new HttpClient(Handler);
            TempDir = new TempDirectory();
            SaveConfigCallCount = 0;
        }

        protected Action SaveConfig => () => SaveConfigCallCount++;

        protected PluginConfiguration DefaultConfig() => new PluginConfiguration
        {
            BaseUrl               = "http://fake-xtream",
            Username              = "user",
            Password              = "pass",
            StrmLibraryPath       = TempDir.Path,
            SmartSkipExisting     = false,
            CleanupOrphans        = false,
            OrphanSafetyThreshold = 0.0,
            StrmNamingVersion     = StrmSyncService.CurrentStrmNamingVersion,
            EpisodeFilenameMigrationVersion = StrmSyncService.CurrentEpisodeFilenameVersion,
            SyncParallelism       = 1,
            MovieFolderMode       = "single",
            SeriesFolderMode      = "single",
            EnableNfoFiles        = false,
            EnableTmdbFolderNaming       = false,
            EnableContentNameCleaning    = false,
        };

        protected StrmSyncService MakeService() =>
            new StrmSyncService(new NullLogger(), HttpClient);

        // ----- JSON factory helpers -----

        protected static string VodStreamsJson(params object[] streams) =>
            JsonSerializer.Serialize(streams);

        protected static object VodStream(int streamId = 1, string name = "Test Movie",
            long added = 1000, string tmdbId = "", string ext = "mkv") =>
            new
            {
                stream_id = streamId,
                name,
                added,
                tmdb_id = tmdbId,
                container_extension = ext,
                category_id = (int?)null
            };

        protected static string SeriesListJson(params object[] series) =>
            JsonSerializer.Serialize(series);

        protected static object Series(int seriesId = 1, string name = "Test Show",
            string lastModified = "2000", string tmdbId = "") =>
            new
            {
                series_id = seriesId,
                name,
                last_modified = lastModified,
                tmdb = tmdbId,
                category_id = (int?)null
            };

        protected static string SeriesDetailJson(int seriesId = 1, int seasonNum = 1,
            int episodeNum = 1, string title = "Episode Title", string ext = "mp4") =>
            JsonSerializer.Serialize(new
            {
                info = new { series_id = seriesId, name = "Test Show", tmdb = "" },
                seasons = new object[0],
                episodes = new System.Collections.Generic.Dictionary<string, object[]>
                {
                    [seasonNum.ToString()] = new object[]
                    {
                        new { id = 101, episode_num = episodeNum, title, container_extension = ext, season = seasonNum }
                    }
                }
            });

        protected static readonly CancellationToken None = CancellationToken.None;

        public void Dispose()
        {
            HttpClient.Dispose();
            TempDir.Dispose();
        }

        private class NullLogger : ILogger
        {
            public void Info(string message, params object[] paramList) { }
            public void Error(string message, params object[] paramList) { }
            public void Warn(string message, params object[] paramList) { }
            public void Debug(string message, params object[] paramList) { }
            public void Fatal(string message, params object[] paramList) { }
            public void FatalException(string message, Exception exception, params object[] paramList) { }
            public void ErrorException(string message, Exception exception, params object[] paramList) { }
            public void LogMultiline(string message, LogSeverity severity, StringBuilder additionalContent) { }
            public void Log(LogSeverity severity, string message, params object[] paramList) { }
            public void Info(ReadOnlyMemory<char> message) { }
            public void Error(ReadOnlyMemory<char> message) { }
            public void Warn(ReadOnlyMemory<char> message) { }
            public void Debug(ReadOnlyMemory<char> message) { }
            public void Log(LogSeverity severity, ReadOnlyMemory<char> message) { }
        }
    }
}
