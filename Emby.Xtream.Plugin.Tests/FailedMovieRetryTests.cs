using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// "Retry failed items" must write a movie exactly as the sync would have. It had a writer of
    /// its own that drifted: it never fetched category names, so in category-folder mode it wrote
    /// nothing and still dropped the movie from the failed list, and it never wrote Dispatcharr's
    /// extra versions. The same drift happened for series before (ADR-020).
    /// </summary>
    public class FailedMovieRetryTests : SyncTestBase
    {
        private string MoviesRoot => Path.Combine(TempDir.Path, "Movies");

        private static string OneMovieInCategory(int categoryId) =>
            System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new { stream_id = 1, name = "New Movie", added = 1000L, tmdb_id = "", container_extension = "mkv", category_id = (int?)categoryId }
            });

        /// <summary>A file where the movie's folder should be makes the sync's write fail.</summary>
        private async Task<Emby.Xtream.Plugin.Service.StrmSyncService> SyncWithBlockedFolder(
            Emby.Xtream.Plugin.PluginConfiguration config, string blockerPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(blockerPath));
            File.WriteAllText(blockerPath, "not a folder");
            var svc = MakeService();
            await svc.SyncMoviesAsync(config, None, SaveConfig);
            Assert.Equal(1, svc.MovieProgress.Failed);
            File.Delete(blockerPath);
            return svc;
        }

        [Fact]
        public async Task RetryFailed_InCategoryFolderMode_WritesIntoTheCategoryFolder()
        {
            var config = DefaultConfig();
            config.MovieFolderMode = "multiple";
            Handler.RespondWith("get_vod_categories", "[{\"category_id\":\"7\",\"category_name\":\"Drama\"}]");
            Handler.RespondWith("get_vod_streams", OneMovieInCategory(7));
            var svc = await SyncWithBlockedFolder(config, Path.Combine(MoviesRoot, "Drama", "New Movie"));

            Handler.RespondWith("get_vod_categories", "[{\"category_id\":\"7\",\"category_name\":\"Drama\"}]");
            Assert.True(await svc.RetryFailedAsync(config, SaveConfig, None));

            Assert.True(File.Exists(Path.Combine(MoviesRoot, "Drama", "New Movie", "New Movie.strm")),
                "The retry must write where the sync writes");
            Assert.DoesNotContain(svc.FailedItems, i => i.ItemType == "Movie");
        }

        [Fact]
        public async Task RetryFailed_WhenCategoriesCannotLoad_KeepsTheMovieFailed_InsteadOfGuessingAFolder()
        {
            var config = DefaultConfig();
            config.MovieFolderMode = "multiple";
            Handler.RespondWith("get_vod_categories", "[{\"category_id\":\"7\",\"category_name\":\"Drama\"}]");
            Handler.RespondWith("get_vod_streams", OneMovieInCategory(7));
            var svc = await SyncWithBlockedFolder(config, Path.Combine(MoviesRoot, "Drama", "New Movie"));

            Handler.RespondWith("get_vod_categories", "{}", System.Net.HttpStatusCode.InternalServerError);
            Assert.True(await svc.RetryFailedAsync(config, SaveConfig, None));

            Assert.Contains(svc.FailedItems, i => i.ItemType == "Movie" && i.StreamId == 1);
            Assert.Empty(Directory.GetFiles(MoviesRoot, "*.strm", SearchOption.AllDirectories));
        }

        [Fact]
        public async Task RetryFailed_ThatWritesNothing_StaysInTheFailedList()
        {
            // Multiple Folders mode, and the movie's category is no longer assigned to a folder
            // when the retry runs: nothing is written, so the retry must not count as done.
            var config = DefaultConfig();
            config.MovieFolderMode = "custom";
            config.MovieFolderMappings = "Drama=7";
            Handler.RespondWith("get_vod_categories", "[{\"category_id\":\"7\",\"category_name\":\"Drama\"}]");
            Handler.RespondWith("get_vod_streams", OneMovieInCategory(7));
            var svc = await SyncWithBlockedFolder(config, Path.Combine(MoviesRoot, "Drama", "New Movie"));

            config.MovieFolderMappings = "Comedy=8";
            Assert.True(await svc.RetryFailedAsync(config, SaveConfig, None));

            Assert.Contains(svc.FailedItems, i => i.ItemType == "Movie" && i.StreamId == 1);
            Assert.Empty(Directory.GetFiles(MoviesRoot, "*.strm", SearchOption.AllDirectories));
        }

        [Fact]
        public async Task RetryFailed_WithFolderNaming_UsesTheTmdbFolderLikeTheSync()
        {
            var config = DefaultConfig();
            config.EnableTmdbFolderNaming = true;
            Handler.RespondWith("get_vod_streams", System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new { stream_id = 1, name = "New Movie", added = 1000L, tmdb_id = "603", container_extension = "mkv", category_id = (int?)null }
            }));
            var svc = await SyncWithBlockedFolder(config, Path.Combine(MoviesRoot, "New Movie [tmdbid=603]"));

            Assert.True(await svc.RetryFailedAsync(config, SaveConfig, None));

            Assert.True(File.Exists(Path.Combine(MoviesRoot, "New Movie [tmdbid=603]", "New Movie [tmdbid=603].strm")));
            Assert.False(Directory.Exists(Path.Combine(MoviesRoot, "New Movie")),
                "The retry must not split the movie into an unsuffixed folder");
        }

        [Fact]
        public async Task RetryFailed_WithDispatcharr_WritesEveryVersion()
        {
            var config = DefaultConfig();
            Handler.RespondWith("get_vod_streams", OneMovieInCategory(7));
            var svc = await SyncWithBlockedFolder(config, Path.Combine(MoviesRoot, "New Movie"));

            // Dispatcharr turned on before the retry, with two provider streams for the movie.
            config.EnableDispatcharr = true;
            config.DispatcharrUrl = "http://fake-dispatcharr";
            config.DispatcharrUser = "admin";
            config.DispatcharrPass = "secret";
            Handler.RespondWith("/api/accounts/token/", "{\"access\":\"tok\",\"refresh\":\"ref\"}");
            Handler.RespondWith("api/vod/movies/1/providers/", "[{\"id\":1,\"stream_id\":11,\"name\":\"A\"},{\"id\":2,\"stream_id\":12,\"name\":\"B\"}]");
            Handler.RespondWith("api/vod/movies/1/", "{\"id\":1,\"uuid\":\"movie-uuid\"}");
            svc.DispatcharrHandler = Handler;

            Assert.True(await svc.RetryFailedAsync(config, SaveConfig, None));

            var dir = Path.Combine(MoviesRoot, "New Movie");
            Assert.Equal("http://fake-dispatcharr/proxy/vod/movie/movie-uuid?stream_id=11", File.ReadAllText(Path.Combine(dir, "New Movie.strm")));
            Assert.Equal("http://fake-dispatcharr/proxy/vod/movie/movie-uuid?stream_id=12", File.ReadAllText(Path.Combine(dir, "New Movie - Version 2.strm")));
        }

        [Fact]
        public async Task RetryFailed_StillFailing_StaysInTheFailedList()
        {
            var config = DefaultConfig();
            Handler.RespondWith("get_vod_streams", OneMovieInCategory(7));
            var blocker = Path.Combine(MoviesRoot, "New Movie");
            var svc = await SyncWithBlockedFolder(config, blocker);
            File.WriteAllText(blocker, "still not a folder");

            Assert.True(await svc.RetryFailedAsync(config, SaveConfig, None));

            Assert.Contains(svc.FailedItems, i => i.ItemType == "Movie" && i.StreamId == 1);
        }
    }
}
