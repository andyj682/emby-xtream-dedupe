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
