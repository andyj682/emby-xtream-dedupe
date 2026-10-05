using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Excluding one catalogue entry must never delete a different entry the user kept.
    ///
    /// The exclusion filter splits the catalogue by stream ID, but the exclusion delete pass
    /// finds folders by cleaned name, ignoring case. Two entries whose names differ only in
    /// case (or that clean to the same name) share one folder, so excluding one deleted the
    /// folder the sync had just written for the other, on every sync. Found in
    /// andyj682/emby-xtream-dedupe (ADR-F007).
    /// </summary>
    public class ExclusionCollisionTests : SyncTestBase
    {
        private string MovieStrmPath(string movieName)
            => Path.Combine(TempDir.Path, "Movies", movieName, movieName + ".strm");

        private string ShowDir(string showName)
            => Path.Combine(TempDir.Path, "Shows", showName);

        private void RegisterVodStreams(string json)
            => Handler.RespondWith("get_vod_streams", json);

        /// <summary>
        /// An owned STRM from an earlier sync whose content is out of date and which cannot be
        /// overwritten, so this run's write of that movie fails.
        /// </summary>
        private string SeedReadOnlyMovieStrm(string movieName)
        {
            var path = MovieStrmPath(movieName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "http://fake-xtream/movie/user/pass/1.old");
            File.SetAttributes(path, FileAttributes.ReadOnly);
            return path;
        }

        private static bool HasStrm(string dir)
            => Directory.Exists(dir) && Directory.GetFiles(dir, "*.strm", SearchOption.AllDirectories).Any();

        [Fact]
        public async Task Movie_ExcludedTwinDiffersOnlyInCase_KeptTwinSurvivesRepeatedSyncs()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };
            // Twice: the reported symptom was the kept title vanishing on every sync. The fake
            // handler's responses are single-use, so each sync registers its own.
            RegisterTwinMovies();
            await MakeService().SyncMoviesAsync(config, None, SaveConfig);
            RegisterTwinMovies();
            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(MovieStrmPath("Twin Title")),
                "The included title shares a folder name with the excluded one and must not be deleted");
        }

        // FORK DIVERGENCE (ADR-F001). Upstream's ADR-018 tests here assume two series whose names
        // differ only in case are different titles, so excluding one must leave the other on disk.
        // This fork treats every case variant as the same show, everywhere: the de-dup review view
        // shows them as one row, and the sync propagates an exclusion across the whole name group.
        // There is no "kept twin" to protect. Upstream's Series_ExcludedTwinDiffersOnlyInCase_*
        // and Series_KeptTwinDetailFetchFails_* were replaced by this test, which pins that.
        // (The movie tests above still apply: movies are identified by stream id, not by name.)
        [Fact]
        public async Task Series_CaseVariantsOfOneName_AreOneShow_ExclusionCoversEveryVariant()
        {
            var config = DefaultConfig();
            config.ExcludedSeriesIds = new[] { 2 };
            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 1, name: "Twin Show", lastModified: "1000"),
                Series(seriesId: 2, name: "twin show", lastModified: "1000"),
                Series(seriesId: 3, name: "Twin show", lastModified: "1000"),
                Series(seriesId: 4, name: "twin Show", lastModified: "1000")));
            // Deliberately no get_series_info registered: an excluded show is never fetched, and
            // the fake handler throws on an unregistered request, which would count as a failure.

            // On disk under one of the casings from an earlier sync. Excluding any variant removes it.
            var existing = Path.Combine(ShowDir("Twin Show"), "Season 01", "Twin Show - S01E01.strm");
            Directory.CreateDirectory(Path.GetDirectoryName(existing));
            File.WriteAllText(existing, "http://fake-xtream/series/user/pass/55.mp4");

            var svc = MakeService();
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.DoesNotContain(Handler.ReceivedUrls, u => u.Contains("get_series_info"));
            Assert.Equal(0, svc.SeriesProgress.Failed);
            Assert.False(File.Exists(existing),
                "Excluding one case variant excludes the show, so its existing folder is removed");
        }


        /// <summary>
        /// The kept twin is only protected by having been written this run. When its category
        /// fails to answer it is not written, so the exclusion pass has to wait for a run that
        /// saw the whole catalogue, the same rule orphan cleanup follows (ADR-013).
        /// </summary>
        [Fact]
        public async Task Movie_KeptTwinInFailedCategory_NotDeletedByExclusion()
        {
            var config = DefaultConfig();
            config.SelectedVodCategoryIds = new[] { 1, 2 };
            config.ExcludedVodStreamIds = new[] { 2 };

            // The excluded entry is in the category that answers; the kept one is in the one
            // that fails, and is on disk from an earlier sync.
            Handler.RespondWith("category_id=1", VodStreamsJson(VodStream(streamId: 2, name: "twin title", added: 1000)));
            Handler.RespondWith("category_id=2", "{}", HttpStatusCode.InternalServerError);
            var keptPath = MovieStrmPath("Twin Title");
            Directory.CreateDirectory(Path.GetDirectoryName(keptPath));
            File.WriteAllText(keptPath, "http://fake-xtream/movie/user/pass/1.mkv");

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(keptPath),
                "A kept title in a category that failed to load must not be deleted by an exclusion");
        }

        [Fact]
        public async Task Series_KeptTwinInFailedCategory_NotDeletedByExclusion()
        {
            var config = DefaultConfig();
            config.SelectedSeriesCategoryIds = new[] { 1, 2 };
            config.ExcludedSeriesIds = new[] { 2 };

            Handler.RespondWith("get_series&category_id=1", SeriesListJson(Series(seriesId: 2, name: "twin show", lastModified: "1000")));
            Handler.RespondWith("get_series&category_id=2", "{}", HttpStatusCode.InternalServerError);
            Handler.RespondWith("get_series_info", SeriesDetailJson(seriesId: 2));
            var keptEpisode = Path.Combine(ShowDir("Twin Show"), "Season 01", "Twin Show - S01E01.strm");
            Directory.CreateDirectory(Path.GetDirectoryName(keptEpisode));
            File.WriteAllText(keptEpisode, "http://fake-xtream/series/user/pass/55.mp4");

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(keptEpisode),
                "A kept show in a category that failed to load must not be deleted by an exclusion");
        }

        /// <summary>
        /// A kept title whose write fails this run is protected through its existing files,
        /// not by postponing every exclusion: a stuck failed item would otherwise block the
        /// exclusion filter on every sync (ADR-012).
        /// </summary>
        [Fact]
        public async Task Movie_KeptTwinFailsToWrite_NotDeletedByExclusion()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };
            var keptPath = SeedReadOnlyMovieStrm("Twin Title");
            RegisterTwinMovies();

            try
            {
                var svc = MakeService();
                await svc.SyncMoviesAsync(config, None, SaveConfig);

                Assert.Equal(1, svc.MovieProgress.Failed);
                Assert.True(File.Exists(keptPath),
                    "A kept title that failed to write this run must not be deleted by an exclusion");
            }
            finally
            {
                File.SetAttributes(keptPath, FileAttributes.Normal);
            }
        }


        [Fact]
        public async Task Movie_UnrelatedItemFails_ExclusionStillApplied()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };
            var stuckPath = SeedReadOnlyMovieStrm("Stuck Movie");
            var stale = MovieStrmPath("Drop Me");
            Directory.CreateDirectory(Path.GetDirectoryName(stale));
            File.WriteAllText(stale, "http://fake-xtream/movie/user/pass/2.mkv");
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Stuck Movie", added: 1000),
                VodStream(streamId: 2, name: "Drop Me", added: 1000)));

            try
            {
                var svc = MakeService();
                await svc.SyncMoviesAsync(config, None, SaveConfig);

                Assert.Equal(1, svc.MovieProgress.Failed);
                Assert.False(File.Exists(stale),
                    "One failing item must not stop exclusions from being applied");
                Assert.True(File.Exists(stuckPath));
            }
            finally
            {
                File.SetAttributes(stuckPath, FileAttributes.Normal);
            }
        }

        /// <summary>
        /// Control: with no collision and a complete catalogue, the exclusion still removes the
        /// excluded title's existing folder. Guards against a fix that simply stops deleting.
        /// </summary>
        [Fact]
        public async Task Movie_NoCollision_ExcludedFolderStillRemoved()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };
            var stale = MovieStrmPath("Drop Me");
            Directory.CreateDirectory(Path.GetDirectoryName(stale));
            File.WriteAllText(stale, "http://fake-xtream/movie/user/pass/2.mkv");
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Keep Me", added: 1000),
                VodStream(streamId: 2, name: "Drop Me", added: 1000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(MovieStrmPath("Keep Me")));
        }


        private void RegisterTwinMovies()
        {
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Twin Title", added: 1000),
                VodStream(streamId: 2, name: "twin title", added: 1000)));
        }
    }
}
