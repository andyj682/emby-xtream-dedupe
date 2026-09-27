using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using Emby.Xtream.Plugin.Tests.Fakes;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// What the series sync says about a run has to match what it did. "N written" used to be
    /// Completed minus Skipped, which counted failures as writes, and a series that was skipped
    /// without a fetch and without a stored episode hash, or that returned no episodes and had
    /// no files, left no trace in the log at all. From andyj682/emby-xtream-dedupe (575cb64,
    /// 048d7f2).
    /// </summary>
    public class SyncReportingTests : SyncTestBase
    {
        private RecordingLogger Logger { get; } = new RecordingLogger();

        private StrmSyncService MakeRecordingService()
            => new StrmSyncService(Logger, HttpClient) { SeriesDetailRetryDelayMs = 0 };

        private static string EmptyDetail(int id) =>
            "{\"info\":{\"series_id\":" + id + ",\"name\":\"x\"},\"seasons\":[],\"episodes\":{}}";

        private void SeedEpisode(string show)
        {
            var path = Path.Combine(TempDir.Path, "Shows", show, "Season 01", show + " - S01E01 - Episode Title.strm");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "http://fake-xtream/series/user/pass/101.mp4");
        }

        /// <summary>Unchanged since the last sync, files on disk, smart skip on: skipped without a fetch.</summary>
        private PluginConfiguration SkippedSeriesConfig(string storedHashesJson)
        {
            var config = DefaultConfig();
            config.SmartSkipExisting = true;
            config.LastSeriesSyncTimestamp = 5000;
            config.SeriesEpisodeHashesJson = storedHashesJson;
            SeedEpisode("Test Show");
            Handler.RespondWith("action=get_series",
                SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "2000")));
            return config;
        }

        [Fact]
        public async Task SkippedSeriesWithNoStoredHash_IsNamedInAWarning()
        {
            var config = SkippedSeriesConfig(string.Empty);

            await MakeRecordingService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.Contains(Logger.Warnings, w => w.Contains("not checked this run") && w.Contains("'Test Show' (id=1)"));
        }

        [Fact]
        public async Task SkippedSeriesWithStoredHash_IsNotWarnedAbout()
        {
            // The ordinary skip. It has to stay quiet, or the warning is noise on every sync.
            var config = SkippedSeriesConfig("{\"1\":\"deadbeef\"}");

            await MakeRecordingService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.DoesNotContain(Logger.Warnings, w => w.Contains("not checked this run"));
        }

        [Fact]
        public async Task SeriesInUnmappedCategory_IsNotWarnedAbout()
        {
            // Custom folder mode leaves unmapped categories out on purpose. Those series never
            // get a hash, and warning about them on every sync would bury the real case.
            var config = DefaultConfig();
            config.SeriesFolderMode = "custom";
            config.SeriesFolderMappings = "Kids=1";
            Handler.RespondWith("get_series_categories",
                "[{\"category_id\":\"1\",\"category_name\":\"Kids\"},{\"category_id\":\"2\",\"category_name\":\"Other\"}]");
            Handler.RespondWith("action=get_series",
                "[{\"series_id\":1,\"name\":\"Kept Show\",\"last_modified\":\"1000\",\"category_id\":\"1\"}," +
                "{\"series_id\":2,\"name\":\"Unmapped Show\",\"last_modified\":\"1000\",\"category_id\":\"2\"}]");
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));

            await MakeRecordingService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.DoesNotContain(Logger.Warnings, w => w.Contains("Unmapped Show"));
        }

        [Fact]
        public async Task SeriesWithNoEpisodesAndNoFiles_AreNamedInOneWarning()
        {
            var config = DefaultConfig();
            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 1, name: "Empty One", lastModified: "1000"),
                Series(seriesId: 2, name: "Empty Two", lastModified: "1000")));
            Handler.RespondWithSequence("action=get_series_info&series_id=1", new[] { EmptyDetail(1), EmptyDetail(1) });
            Handler.RespondWithSequence("action=get_series_info&series_id=2", new[] { EmptyDetail(2), EmptyDetail(2) });

            await MakeRecordingService().SyncSeriesAsync(config, None, SaveConfig);

            var empty = Logger.Warnings.Where(w => w.Contains("returned no episodes and have no files")).ToList();
            Assert.Single(empty);
            Assert.Contains("'Empty One' (id=1)", empty[0]);
            Assert.Contains("'Empty Two' (id=2)", empty[0]);
            // Already reported above; the no-hash warning must not repeat them.
            Assert.DoesNotContain(Logger.Warnings, w => w.Contains("not checked this run"));
        }

        [Fact]
        public async Task CompletionLine_DoesNotCountFailuresAsWritten()
        {
            var config = DefaultConfig();
            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 1, name: "Good Show", lastModified: "1000"),
                Series(seriesId: 2, name: "Broken Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));
            Handler.RespondWith("action=get_series_info&series_id=2", "{}", HttpStatusCode.InternalServerError);

            await MakeRecordingService().SyncSeriesAsync(config, None, SaveConfig);

            var line = Assert.Single(Logger.Infos, i => i.StartsWith("Series STRM sync completed"));
            Assert.Contains("1 written", line);
            Assert.Contains("1 failed", line);
            // A failed series is reported as failed only, not also as unchecked.
            Assert.DoesNotContain(Logger.Warnings, w => w.Contains("not checked this run"));
        }
    

        [Fact]
        public async Task MovieCompletionLine_DoesNotCountFailuresAsWritten()
        {
            var config = DefaultConfig();
            // The second movie's folder name is taken by a file, so its write fails.
            Directory.CreateDirectory(Path.Combine(TempDir.Path, "Movies"));
            File.WriteAllText(Path.Combine(TempDir.Path, "Movies", "Broken Movie"), "not a folder");
            Handler.RespondWith("get_vod_streams", VodStreamsJson(
                VodStream(streamId: 1, name: "Good Movie", added: 1000),
                VodStream(streamId: 2, name: "Broken Movie", added: 1000)));

            await MakeRecordingService().SyncMoviesAsync(config, None, SaveConfig);

            var line = Assert.Single(Logger.Infos, i => i.StartsWith("Movie STRM sync completed"));
            Assert.Contains("1 written", line);
            Assert.Contains("1 failed", line);
        }
}
}
