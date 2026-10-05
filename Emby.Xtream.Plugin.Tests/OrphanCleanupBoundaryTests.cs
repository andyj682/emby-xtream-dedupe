using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// The edges of orphan cleanup and exclusion removal that mutation testing showed no test
    /// was checking once it could reach this code (issue #75): the safety threshold's limits,
    /// removing emptied folders, and the deleted count, which also decides whether Emby is told
    /// the library changed.
    /// </summary>
    public class OrphanCleanupBoundaryTests : SyncTestBase
    {
        private string MoviesRoot => Path.Combine(TempDir.Path, "Movies");

        private string MovieStrm(string name) => Path.Combine(MoviesRoot, name, name + ".strm");

        /// <summary>Movies 1..onDisk exist from an earlier sync; the provider now lists 1..listed.</summary>
        private void Seed(int onDisk, int listed)
        {
            for (var i = 1; i <= onDisk; i++)
            {
                var name = $"Movie {i:D2}";
                Directory.CreateDirectory(Path.Combine(MoviesRoot, name));
                File.WriteAllText(MovieStrm(name), $"http://fake-xtream/movie/user/pass/{i}.mkv");
            }

            Handler.RespondWith("get_vod_streams", VodStreamsJson(
                Enumerable.Range(1, listed).Select(i => VodStream(streamId: i, name: $"Movie {i:D2}", added: 1000)).ToArray()));
        }

        private int StrmsLeft() => Directory.GetFiles(MoviesRoot, "*.strm", SearchOption.AllDirectories).Length;

        [Fact]
        public async Task ThresholdZero_TurnsTheGuardOff()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            config.OrphanSafetyThreshold = 0;
            Seed(onDisk: 12, listed: 1);

            var svc = MakeService();
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(1, StrmsLeft());
            Assert.Equal(11, svc.MovieProgress.Deleted);
        }

        [Fact]
        public async Task SmallLibrary_IsCleanedEvenAboveTheThreshold()
        {
            // The ratio guard only applies above 10 files: in a small library one removal is
            // already a large share, and the guard would stop it from ever being cleaned.
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            config.OrphanSafetyThreshold = 0.2;
            Seed(onDisk: 3, listed: 2);

            var svc = MakeService();
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(2, StrmsLeft());
            Assert.Equal(1, svc.MovieProgress.Deleted);
        }

        [Fact]
        public async Task ExactlyTenFiles_IsStillASmallLibrary()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            config.OrphanSafetyThreshold = 0.2;
            Seed(onDisk: 10, listed: 5);

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(5, StrmsLeft());
        }

        [Fact]
        public async Task RatioEqualToThreshold_IsAllowed()
        {
            // 3 of 12 is exactly 25%. The guard refuses only above the threshold.
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            config.OrphanSafetyThreshold = 0.25;
            Seed(onDisk: 12, listed: 9);

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(9, StrmsLeft());
        }

        [Fact]
        public async Task OrphanCleanup_RemovesFoldersItEmptied_AndKeepsTheRest()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            Seed(onDisk: 2, listed: 1);

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(Directory.Exists(Path.Combine(MoviesRoot, "Movie 02")), "The emptied movie folder is removed");
            Assert.True(Directory.Exists(Path.Combine(MoviesRoot, "Movie 01")));
            Assert.True(Directory.Exists(MoviesRoot), "The library folder itself stays");
        }

        [Fact]
        public async Task OrphanCleanup_RemovesEmptiedSeasonAndShowFolders()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            var orphan = Path.Combine(TempDir.Path, "Shows", "Old Show", "Season 01", "Old Show - S01E01.strm");
            Directory.CreateDirectory(Path.GetDirectoryName(orphan));
            File.WriteAllText(orphan, "http://fake-xtream/series/user/pass/9.mp4");
            Handler.RespondWith("action=get_series", SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Old Show")));
            Assert.True(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Test Show")));
        }

        [Fact]
        public async Task OrphanCleanup_LeavesAFolderThatStillHasOtherFiles()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            Seed(onDisk: 2, listed: 1);
            var poster = Path.Combine(MoviesRoot, "Movie 02", "poster.jpg");
            File.WriteAllText(poster, "user artwork");

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(MovieStrm("Movie 02")));
            Assert.True(File.Exists(poster));
        }

        [Fact]
        public async Task ExcludedTitleWithNothingOfOurs_IsNotCountedAsDeleted()
        {
            // The deleted count also decides whether Emby is told the library changed.
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };
            var userFile = Path.Combine(MoviesRoot, "Drop Me", "notes.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(userFile));
            File.WriteAllText(userFile, "mine");
            Handler.RespondWith("get_vod_streams", VodStreamsJson(
                VodStream(streamId: 1, name: "Keep Me", added: 1000),
                VodStream(streamId: 2, name: "Drop Me", added: 1000)));

            var svc = MakeService();
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(userFile));
            Assert.Equal(0, svc.MovieProgress.Deleted);
        }

        [Fact]
        public async Task ExcludedTitle_IsCountedOnce()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };
            Directory.CreateDirectory(Path.Combine(MoviesRoot, "Drop Me"));
            File.WriteAllText(MovieStrm("Drop Me"), "http://fake-xtream/movie/user/pass/2.mkv");
            Handler.RespondWith("get_vod_streams", VodStreamsJson(
                VodStream(streamId: 1, name: "Keep Me", added: 1000),
                VodStream(streamId: 2, name: "Drop Me", added: 1000)));

            var svc = MakeService();
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(MovieStrm("Drop Me")));
            Assert.Equal(1, svc.MovieProgress.Deleted);
        }
    }
}
