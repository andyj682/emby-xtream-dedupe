using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// After a sync that added or removed files, Emby is told the Movies or Shows folder changed,
    /// so new content appears without waiting for a scheduled library scan. A sync that changed
    /// nothing must not trigger anything. From andyj682/emby-xtream-dedupe (771ac8c).
    /// </summary>
    public class LibraryRefreshTests : SyncTestBase
    {
        private readonly List<string> _notified = new List<string>();

        private StrmSyncService MakeNotifyingService()
        {
            var svc = MakeService();
            svc.LibraryChangedNotifier = path => _notified.Add(path);
            return svc;
        }

        private string MoviesRoot => Path.Combine(TempDir.Path, "Movies");
        private string ShowsRoot => Path.Combine(TempDir.Path, "Shows");

        private void RegisterOneMovie()
            => Handler.RespondWith("get_vod_streams", VodStreamsJson(VodStream(streamId: 1, name: "New Movie", added: 1000)));

        [Fact]
        public async Task MovieSyncThatAddsFiles_NotifiesMoviesFolder()
        {
            var config = DefaultConfig();
            RegisterOneMovie();

            await MakeNotifyingService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(new[] { MoviesRoot }, _notified);
        }

        [Fact]
        public async Task MovieSyncThatChangesNothing_DoesNotNotify()
        {
            var config = DefaultConfig();
            RegisterOneMovie();
            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            // Same catalogue again: the file is already there with the same content.
            RegisterOneMovie();
            await MakeNotifyingService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.Empty(_notified);
        }

        [Fact]
        public async Task SettingOff_DoesNotNotify()
        {
            var config = DefaultConfig();
            config.RefreshEmbyLibraryAfterSync = false;
            RegisterOneMovie();

            await MakeNotifyingService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.Empty(_notified);
        }

        [Fact]
        public async Task SeriesSyncThatAddsEpisodes_NotifiesShowsFolder()
        {
            var config = DefaultConfig();
            Handler.RespondWith("action=get_series", SeriesListJson(Series(seriesId: 1, name: "New Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));

            await MakeNotifyingService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.Equal(new[] { ShowsRoot }, _notified);
        }

        /// <summary>
        /// Removing an excluded series only deletes files. Emby should hear about that too, or
        /// the show stays in the library until the next scheduled scan.
        /// </summary>
        [Fact]
        public async Task SeriesSyncThatOnlyRemovesAnExcludedShow_NotifiesShowsFolder()
        {
            var config = DefaultConfig();
            config.ExcludedSeriesIds = new[] { 2 };
            var stale = Path.Combine(ShowsRoot, "Drop Show", "Season 01", "Drop Show - S01E01.strm");
            Directory.CreateDirectory(Path.GetDirectoryName(stale));
            File.WriteAllText(stale, "http://fake-xtream/series/user/pass/7.mp4");
            Handler.RespondWith("action=get_series", SeriesListJson(Series(seriesId: 2, name: "Drop Show", lastModified: "1000")));

            await MakeNotifyingService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(stale));
            Assert.Equal(new[] { ShowsRoot }, _notified);
        }

        [Fact]
        public async Task RetryThatWritesAMovie_NotifiesMoviesFolder()
        {
            var config = DefaultConfig();
            // A file where the movie's folder should be makes the first write fail.
            Directory.CreateDirectory(MoviesRoot);
            var blocker = Path.Combine(MoviesRoot, "New Movie");
            File.WriteAllText(blocker, "not a folder");
            RegisterOneMovie();
            var svc = MakeNotifyingService();
            await svc.SyncMoviesAsync(config, None, SaveConfig);
            Assert.Equal(1, svc.MovieProgress.Failed);
            _notified.Clear();

            File.Delete(blocker);
            Assert.True(await svc.RetryFailedAsync(config, SaveConfig, None));

            Assert.True(File.Exists(Path.Combine(MoviesRoot, "New Movie", "New Movie.strm")));
            Assert.Equal(new[] { MoviesRoot }, _notified);
        }

        /// <summary>
        /// A sync that wrote files and then failed on a later step still changed the library.
        /// </summary>
        [Fact]
        public async Task SyncThatFailsAfterWriting_StillNotifies()
        {
            var config = DefaultConfig();
            RegisterOneMovie();
            var svc = MakeNotifyingService();

            await Assert.ThrowsAnyAsync<IOException>(
                () => svc.SyncMoviesAsync(config, None, () => throw new IOException("disk full")));

            Assert.True(File.Exists(Path.Combine(MoviesRoot, "New Movie", "New Movie.strm")));
            Assert.Equal(new[] { MoviesRoot }, _notified);
        }
    }
}
