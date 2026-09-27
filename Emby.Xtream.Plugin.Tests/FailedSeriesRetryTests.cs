using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// A series that failed must actually be processed again, by the next sync and by
    /// "Retry failed items", and in the same layout the normal sync writes.
    ///
    /// The watermark advances past a series before its detail is fetched, so after a failed
    /// fetch the series counts as unchanged. With smart skip on and its folder already on disk,
    /// every later sync skipped it without fetching, and the sync's reset of the failed list
    /// dropped it without a trace. The retry button wrote series in a layout of its own
    /// ("Season 1", "S1E01.strm", no folder modes), creating a second copy of the show.
    /// </summary>
    public class FailedSeriesRetryTests : SyncTestBase
    {
        private string EpisodePath(string show, int season, int episode, string title)
            => Path.Combine(TempDir.Path, "Shows", show, $"Season {season:D2}",
                $"{show} - S{season:D2}E{episode:D2} - {title}.strm");

        private static string TwoEpisodeDetailJson() =>
            System.Text.Json.JsonSerializer.Serialize(new
            {
                info = new { series_id = 1, name = "Test Show", tmdb = "" },
                seasons = new object[0],
                episodes = new System.Collections.Generic.Dictionary<string, object[]>
                {
                    ["1"] = new object[]
                    {
                        new { id = 101, episode_num = 1, title = "Episode Title", container_extension = "mp4", season = 1 },
                        new { id = 102, episode_num = 2, title = "New Episode", container_extension = "mp4", season = 1 }
                    }
                }
            });

        /// <summary>
        /// The show is on disk from an earlier sync; this run's detail fetch fails. Afterwards
        /// the watermark is past the show and the show is in the failed list.
        /// </summary>
        private async Task<Emby.Xtream.Plugin.Service.StrmSyncService> SyncWithFailedDetail(
            Emby.Xtream.Plugin.PluginConfiguration config)
        {
            var existing = EpisodePath("Test Show", 1, 1, "Episode Title");
            Directory.CreateDirectory(Path.GetDirectoryName(existing));
            File.WriteAllText(existing, "http://fake-xtream/series/user/pass/101.mp4");

            Handler.RespondWith("action=get_series", SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=1", "{}", HttpStatusCode.InternalServerError);

            var svc = MakeService();
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.Equal(1, svc.SeriesProgress.Failed);
            Assert.Contains(svc.FailedItems, i => i.ItemType == "Series" && i.StreamId == 1);
            Assert.Equal(1000, config.LastSeriesSyncTimestamp);
            return svc;
        }

        [Fact]
        public async Task NextSync_ProcessesSeriesThatFailedLastTime()
        {
            var config = DefaultConfig();
            config.SmartSkipExisting = true;
            var svc = await SyncWithFailedDetail(config);

            // Nothing about the show changed on the provider side.
            Handler.RespondWith("action=get_series", SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=1", TwoEpisodeDetailJson());
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(EpisodePath("Test Show", 1, 2, "New Episode")),
                "A series that failed last time must be fetched again, not skipped as unchanged");
            Assert.DoesNotContain(svc.FailedItems, i => i.ItemType == "Series");
        }

        [Fact]
        public async Task RetryFailed_WritesSeriesInTheSameLayoutAsTheSync()
        {
            var config = DefaultConfig();
            config.SmartSkipExisting = true;
            var svc = await SyncWithFailedDetail(config);

            Handler.RespondWith("action=get_series", SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=1", TwoEpisodeDetailJson());
            Assert.True(await svc.RetryFailedAsync(config, SaveConfig, None));

            Assert.True(File.Exists(EpisodePath("Test Show", 1, 2, "New Episode")));
            var showDir = Path.Combine(TempDir.Path, "Shows", "Test Show");
            Assert.False(Directory.Exists(Path.Combine(showDir, "Season 1")),
                "The retry must not write its own 'Season 1' layout");
            Assert.Empty(Directory.GetFiles(showDir, "S*E*.strm", SearchOption.AllDirectories));
            Assert.DoesNotContain(svc.FailedItems, i => i.ItemType == "Series");
        }

        /// <summary>
        /// A failed series that this run cannot see (its category failed to load) stays in the
        /// failed list instead of being dropped as if it had been dealt with.
        /// </summary>
        [Fact]
        public async Task FailedSeriesMissingFromCatalogue_StaysInFailedList()
        {
            var config = DefaultConfig();
            config.SmartSkipExisting = true;
            var svc = await SyncWithFailedDetail(config);

            config.SelectedSeriesCategoryIds = new[] { 1, 2 };
            Handler.RespondWith("get_series&category_id=1", SeriesListJson(Series(seriesId: 5, name: "Other Show", lastModified: "1000")));
            Handler.RespondWith("get_series&category_id=2", "{}", HttpStatusCode.InternalServerError);
            Handler.RespondWith("action=get_series_info&series_id=5", SeriesDetailJson(seriesId: 5));
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.Contains(svc.FailedItems, i => i.ItemType == "Series" && i.StreamId == 1);
        }
    }
}
