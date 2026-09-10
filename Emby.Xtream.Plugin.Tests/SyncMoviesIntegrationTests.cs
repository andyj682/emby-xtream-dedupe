using System;
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
    /// Integration tests for <see cref="StrmSyncService.SyncMoviesAsync"/>.
    ///
    /// Path structure (MovieFolderMode = "single"):
    ///   {StrmLibraryPath}/Movies/{folderName}/{folderName}.strm
    ///
    /// URL pattern (no selected categories):
    ///   ...player_api.php?...&amp;action=get_vod_streams
    /// </summary>
    public class SyncMoviesIntegrationTests : SyncTestBase
    {
        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// Compute the expected STRM path for a movie with a plain name
        /// (no TMDB ID, so folderName == sanitizedName).
        /// </summary>
        private string MovieStrmPath(string movieName)
        {
            var folderName = movieName; // SanitizeFileName for plain ASCII names is identity
            return Path.Combine(TempDir.Path, "Movies", folderName, folderName + ".strm");
        }

        /// <summary>
        /// Register a successful get_vod_streams response.
        /// </summary>
        private void RegisterVodStreams(string json)
            => Handler.RespondWith("get_vod_streams", json);

        // -----------------------------------------------------------------
        // Test 1: HappyPath_WritesStrmFile
        // -----------------------------------------------------------------

        [Fact]
        public async Task HappyPath_WritesStrmFile()
        {
            var config = DefaultConfig();
            var json = VodStreamsJson(VodStream(streamId: 1, name: "Test Movie", added: 1000));
            RegisterVodStreams(json);

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var strmPath = MovieStrmPath("Test Movie");
            Assert.True(File.Exists(strmPath), $"Expected STRM file at: {strmPath}");
            Assert.Equal(1, SaveConfigCallCount);
        }

        // -----------------------------------------------------------------
        // Test 2: SmartSkip_ExistingFile_NotRewritten
        // -----------------------------------------------------------------

        [Fact]
        public async Task SmartSkip_ExistingFile_NotRewritten()
        {
            var config = DefaultConfig();
            config.SmartSkipExisting = true;
            config.LastMovieSyncTimestamp = 9999; // movie.Added (5000) < 9999 → existing

            var strmPath = MovieStrmPath("Test Movie");
            Directory.CreateDirectory(Path.GetDirectoryName(strmPath));
            File.WriteAllText(strmPath, "SENTINEL");

            // added=5000 < lastTs=9999 → treated as existing, should be skipped
            var json = VodStreamsJson(VodStream(streamId: 1, name: "Test Movie", added: 5000));
            RegisterVodStreams(json);

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal("SENTINEL", File.ReadAllText(strmPath));
        }

        // -----------------------------------------------------------------
        // Test 3: NamingVersionUpgrade_BypassesSmartSkip_OverwritesSentinel
        // -----------------------------------------------------------------

        [Fact]
        public async Task NamingVersionUpgrade_BypassesSmartSkip_OverwritesSentinel()
        {
            var config = DefaultConfig();
            config.SmartSkipExisting = true;
            config.LastMovieSyncTimestamp = 9999;
            config.StrmNamingVersion = 0; // stale version → triggers upgrade → resets timestamps

            var strmPath = MovieStrmPath("Test Movie");
            Directory.CreateDirectory(Path.GetDirectoryName(strmPath));
            File.WriteAllText(strmPath, "SENTINEL");

            // After upgrade, LastMovieSyncTimestamp is reset to 0, so movie is treated as new
            var json = VodStreamsJson(VodStream(streamId: 1, name: "Test Movie", added: 5000));
            RegisterVodStreams(json);

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var content = File.ReadAllText(strmPath);
            Assert.NotEqual("SENTINEL", content);
            // At least 2 saves: one from naming-version upgrade, one from timestamp update
            Assert.True(SaveConfigCallCount >= 2, $"Expected >= 2 saves, got {SaveConfigCallCount}");
        }

        // -----------------------------------------------------------------
        // Test 4: AddedZero_AllStreams_FileStillWrittenWhenNoSmartSkip
        // -----------------------------------------------------------------

        [Fact]
        public async Task AddedZero_AllStreams_FileStillWrittenWhenNoSmartSkip()
        {
            // SmartSkipExisting = false (default) → always write even if added==0
            // LastMovieSyncTimestamp = 100 and movie.Added = 0 → 0 is NOT > 100 → no timestamp save
            var config = DefaultConfig();
            config.LastMovieSyncTimestamp = 100;
            config.SmartSkipExisting = false;

            var json = VodStreamsJson(VodStream(streamId: 1, name: "Test Movie", added: 0));
            RegisterVodStreams(json);

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var strmPath = MovieStrmPath("Test Movie");
            Assert.True(File.Exists(strmPath), $"Expected STRM file at: {strmPath}");
            // maxAdded (0) is NOT > LastMovieSyncTimestamp (100) → saveConfig not called for timestamp
            Assert.Equal(0, SaveConfigCallCount);
        }

        // -----------------------------------------------------------------
        // Test 5: OrphanCleanup_RemovesStaleFile
        // -----------------------------------------------------------------

        [Fact]
        public async Task OrphanCleanup_RemovesStaleFile()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;

            // Pre-write an orphan for "Old Movie"
            var orphanPath = MovieStrmPath("Old Movie");
            Directory.CreateDirectory(Path.GetDirectoryName(orphanPath));
            File.WriteAllText(orphanPath, "http://fake-xtream/movie/user/pass/99.mkv");

            // Provider returns a different movie only
            var json = VodStreamsJson(VodStream(streamId: 2, name: "New Movie", added: 1000));
            RegisterVodStreams(json);

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(orphanPath), "Orphan STRM file should have been deleted");
            Assert.True(File.Exists(MovieStrmPath("New Movie")));
        }

        // -----------------------------------------------------------------
        // Test 6: OrphanThreshold_AboveThreshold_CleanupSkipped
        // -----------------------------------------------------------------

        [Fact]
        public async Task OrphanThreshold_AboveThreshold_CleanupSkipped()
        {
            // Threshold fires when: existingStrms.Length > 10 AND orphanRatio > safetyThreshold
            // Write 12 existing STRM files; provider returns only 1 movie → 11/12 orphaned (91.7%)
            // safetyThreshold = 0.5 → 91.7% > 50% → cleanup skipped
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            config.OrphanSafetyThreshold = 0.5;

            var moviesRoot = Path.Combine(TempDir.Path, "Movies");

            // Write 12 pre-existing STRM files for movies 1–12
            for (int i = 1; i <= 12; i++)
            {
                var name = $"Movie {i:D2}";
                var dir = Path.Combine(moviesRoot, name);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, name + ".strm"), $"http://fake-xtream/movie/user/pass/{i}.mkv");
            }

            // Provider returns only movie 1 → 11 orphans out of 12 = 91.7%
            var json = VodStreamsJson(VodStream(streamId: 1, name: "Movie 01", added: 1000));
            RegisterVodStreams(json);

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            // All 12 files should be preserved (threshold blocked cleanup)
            var remaining = Directory.GetFiles(moviesRoot, "*.strm", SearchOption.AllDirectories);
            Assert.Equal(12, remaining.Length);
        }

        // -----------------------------------------------------------------
        // Test 7: OrphanThreshold_BelowThreshold_CleanupProceeds
        // -----------------------------------------------------------------

        [Fact]
        public async Task OrphanThreshold_BelowThreshold_CleanupProceeds()
        {
            // Write 12 existing STRM files; provider returns 10 movies → 2/12 orphaned (16.7%)
            // safetyThreshold = 0.5 → 16.7% < 50% → cleanup proceeds
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            config.OrphanSafetyThreshold = 0.5;

            var moviesRoot = Path.Combine(TempDir.Path, "Movies");

            for (int i = 1; i <= 12; i++)
            {
                var name = $"Movie {i:D2}";
                var dir = Path.Combine(moviesRoot, name);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, name + ".strm"), $"http://fake-xtream/movie/user/pass/{i}.mkv");
            }

            // Provider returns movies 1–10 → movies 11 and 12 become orphans
            var streams = new object[10];
            for (int i = 0; i < 10; i++)
            {
                streams[i] = VodStream(streamId: i + 1, name: $"Movie {(i + 1):D2}", added: 1000);
            }
            RegisterVodStreams(VodStreamsJson(streams));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var remaining = Directory.GetFiles(moviesRoot, "*.strm", SearchOption.AllDirectories);
            Assert.Equal(10, remaining.Length);
        }

        // -----------------------------------------------------------------
        // Test 8: HttpError_SyncThrows
        // -----------------------------------------------------------------

        [Fact]
        public async Task HttpError_SyncThrows()
        {
            var config = DefaultConfig();
            Handler.RespondWith("get_vod_streams", "Service Unavailable", HttpStatusCode.ServiceUnavailable);

            await Assert.ThrowsAnyAsync<Exception>(
                () => MakeService().SyncMoviesAsync(config, None, SaveConfig))
                ;
        }

        // -----------------------------------------------------------------
        // Test 9: EmptyResponse_NoFilesWritten
        // -----------------------------------------------------------------

        [Fact]
        public async Task EmptyResponse_NoFilesWritten()
        {
            var config = DefaultConfig();
            RegisterVodStreams("[]");

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var moviesRoot = Path.Combine(TempDir.Path, "Movies");
            var files = Directory.Exists(moviesRoot)
                ? Directory.GetFiles(moviesRoot, "*.strm", SearchOption.AllDirectories)
                : Array.Empty<string>();

            Assert.Empty(files);
            Assert.Equal(0, SaveConfigCallCount);
        }

        /// <summary>
        /// Multiple Folders (<c>custom</c>) with no category→folder mappings used to fetch every VOD
        /// stream then skip each one — confusing UX. Abort early with no HTTP calls.
        /// </summary>
        [Fact]
        public async Task CustomMode_EmptyMappings_AbortsWithoutHttp()
        {
            var config = DefaultConfig();
            config.MovieFolderMode = "custom";
            config.MovieFolderMappings = string.Empty;

            var svc = MakeService();
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.Empty(Handler.ReceivedUrls);
            Assert.False(string.IsNullOrEmpty(svc.MovieProgress.AbortReason));
            Assert.Equal(0, svc.MovieProgress.Total);
            Assert.Equal(0, SaveConfigCallCount);
        }

        // -----------------------------------------------------------------
        // Review gate — RequireReviewBeforeSync (ADR-F002)
        // -----------------------------------------------------------------

        [Fact]
        public async Task ReviewGate_Off_ByDefault_EverythingSyncs()
        {
            // The gate inverts the sync's normal contract, so it must stay opt-in.
            var config = DefaultConfig();
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Unreviewed Movie", added: 1000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(MovieStrmPath("Unreviewed Movie")));
        }

        [Fact]
        public async Task ReviewGate_UnreviewedAndNotOnDisk_HeldButNotExcluded()
        {
            var config = DefaultConfig();
            config.RequireReviewBeforeSync = true;
            config.ReviewedVodStreamIdsJson = "[1]";
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Reviewed Movie", added: 1000),
                VodStream(streamId: 2, name: "New Movie", added: 1000)));

            var svc = MakeService();
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(MovieStrmPath("Reviewed Movie")));
            Assert.False(File.Exists(MovieStrmPath("New Movie")));
            // Held is not excluded: the blocklist must be untouched, or the de-dup view would
            // show the title as dealt with and RemoveExcludedContent would delete its folder.
            Assert.Empty(config.ExcludedVodStreamIds);
            Assert.Equal(0, svc.MovieProgress.Failed);
        }

        [Fact]
        public async Task ReviewGate_UnreviewedButTmdbIdOnDisk_SyncedAndMarkedReviewed()
        {
            // A title the user already keeps, back under a new StreamId. Withholding it would
            // strip an established film out of the library; instead it syncs and its new id is
            // folded into the checkpoint so the drift heals itself.
            var config = DefaultConfig();
            config.RequireReviewBeforeSync = true;
            config.EnableTmdbFolderNaming = true;
            config.ReviewedVodStreamIdsJson = "[]";

            var existing = Path.Combine(TempDir.Path, "Movies", "Old Name [tmdbid=603]");
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "Old Name [tmdbid=603].strm"), "http://fake-xtream/movie/user/pass/99.mkv");

            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 42, name: "Renamed Title", added: 1000, tmdbId: "603")));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(MovieStrmPath("Renamed Title [tmdbid=603]")));
            Assert.Contains("42", config.ReviewedVodStreamIdsJson);
        }

        [Fact]
        public async Task ReviewGate_UnreviewedButFolderNameOnDisk_Synced()
        {
            // The legacy case: a folder written before TMDB folder naming was enabled carries no
            // [tmdbid=], so only the stripped name identifies it. Missing this would hold a title
            // whose files are on disk, and a held title's files are not in the written set — so
            // orphan cleanup would then delete them.
            var config = DefaultConfig();
            config.RequireReviewBeforeSync = true;
            config.ReviewedVodStreamIdsJson = "[]";

            var existing = Path.Combine(TempDir.Path, "Movies", "Legacy Movie");
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "Legacy Movie.strm"), "http://fake-xtream/movie/user/pass/7.mkv");

            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 55, name: "Legacy Movie", added: 1000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(MovieStrmPath("Legacy Movie")));
            Assert.Contains("55", config.ReviewedVodStreamIdsJson);
        }

        [Fact]
        public async Task ReviewGate_UnparseableReviewedStore_StandsDownRatherThanHoldingEverything()
        {
            // Reading an unreadable checkpoint as "nothing is reviewed" would withhold the entire
            // catalogue on the strength of a field we failed to parse. Fail open, loudly.
            var config = DefaultConfig();
            config.RequireReviewBeforeSync = true;
            config.ReviewedVodStreamIdsJson = "[1,2,3";
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Movie One", added: 1000),
                VodStream(streamId: 2, name: "Movie Two", added: 1000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(MovieStrmPath("Movie One")));
            Assert.True(File.Exists(MovieStrmPath("Movie Two")));
        }

        /// <summary>
        /// The failure mode this whole design guards against: a held title's files are never
        /// added to the written set, so if an on-disk title were ever held, orphan cleanup would
        /// delete the library out from under the user. Exercised with the orphan guard disabled
        /// (threshold 0) so cleanup genuinely runs.
        /// </summary>
        [Fact]
        public async Task ReviewGate_OnDiskUnreviewedTitle_SurvivesOrphanCleanup()
        {
            var config = DefaultConfig();
            config.RequireReviewBeforeSync = true;
            config.CleanupOrphans = true;
            config.OrphanSafetyThreshold = 0.0;
            config.ReviewedVodStreamIdsJson = "[]";

            // Written by an earlier sync, and never reviewed.
            var existing = MovieStrmPath("Established Movie");
            Directory.CreateDirectory(Path.GetDirectoryName(existing));
            File.WriteAllText(existing, "http://fake-xtream/movie/user/pass/8.mkv");

            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 8, name: "Established Movie", added: 1000),
                VodStream(streamId: 9, name: "Brand New Movie", added: 1000)));

            var svc = MakeService();
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(existing), "an on-disk title must never be held, or cleanup eats it");
            Assert.False(File.Exists(MovieStrmPath("Brand New Movie")));
            Assert.Equal(0, svc.MovieProgress.Failed);
        }

        // -----------------------------------------------------------------
        // Per-item exclusion (issue #57)
        // -----------------------------------------------------------------

        [Fact]
        public async Task ExcludedMovie_NotWritten_OthersUnaffected()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Keep Me", added: 1000),
                VodStream(streamId: 2, name: "Drop Me", added: 1000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(MovieStrmPath("Keep Me")));
            Assert.False(File.Exists(MovieStrmPath("Drop Me")));
        }

        [Fact]
        public async Task ExcludedMovie_ExistingFolderDeleted_WithoutOrphanCleanup()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = false;
            config.ExcludedVodStreamIds = new[] { 2 };

            // Simulate a previous sync having written "Drop Me"
            var staleStrm = MovieStrmPath("Drop Me");
            Directory.CreateDirectory(Path.GetDirectoryName(staleStrm));
            File.WriteAllText(staleStrm, "http://fake-xtream/movie/user/pass/2.mkv");

            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Keep Me", added: 1000),
                VodStream(streamId: 2, name: "Drop Me", added: 1000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, "Movies", "Drop Me")));
            Assert.True(File.Exists(MovieStrmPath("Keep Me")));
        }

        /// <summary>
        /// A folder that matches an excluded title by name but holds no STRM was not written
        /// by this plugin. Recursively deleting it would destroy user data (codex review of
        /// PR #58, [P1]). Nothing in it may be touched.
        /// </summary>
        [Fact]
        public async Task ExcludedMovie_FolderWithNoStrm_LeftCompletelyUntouched()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };

            // A user's own folder that happens to share the provider's title.
            var userDir = Path.Combine(TempDir.Path, "Movies", "Drop Me");
            Directory.CreateDirectory(userDir);
            var userFile = Path.Combine(userDir, "my-own-movie.mkv");
            File.WriteAllText(userFile, "not written by the plugin");

            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 2, name: "Drop Me", added: 1000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(Directory.Exists(userDir), "user folder must survive");
            Assert.True(File.Exists(userFile), "user file must survive");
        }

        /// <summary>
        /// When the folder IS plugin-written but the user has also put their own file in it,
        /// remove the plugin's files and leave theirs, keeping the folder. Same semantics as
        /// CleanupOrphans, which only ever deletes STRMs and prunes emptied directories.
        /// </summary>
        [Fact]
        public async Task ExcludedMovie_FolderWithForeignFile_RemovesStrmKeepsRest()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };

            var dir = Path.Combine(TempDir.Path, "Movies", "Drop Me");
            Directory.CreateDirectory(dir);
            var strm = Path.Combine(dir, "Drop Me.strm");
            var poster = Path.Combine(dir, "poster.jpg");
            File.WriteAllText(strm, "http://fake-xtream/movie/user/pass/2.mkv");
            File.WriteAllText(poster, "user artwork");

            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 2, name: "Drop Me", added: 1000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(strm), "plugin STRM should be removed");
            Assert.True(File.Exists(poster), "user artwork must survive");
            Assert.True(Directory.Exists(dir), "folder must survive because it still has content");
        }

        [Fact]
        public async Task ExcludedMovie_FolderWithTmdbSuffix_Deleted()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };

            var staleDir = Path.Combine(TempDir.Path, "Movies", "Drop Me [tmdbid=550]");
            Directory.CreateDirectory(staleDir);
            File.WriteAllText(Path.Combine(staleDir, "Drop Me [tmdbid=550].strm"), "http://fake-xtream/movie/user/pass/7.mkv");

            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 2, name: "Drop Me", added: 1000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(Directory.Exists(staleDir));
        }

        /// <summary>
        /// ADR-012 claims re-ticking a title recreates it without any forced re-sync, and that
        /// claim is why no delta watermark reset was added. Pins the full journey: sync, exclude,
        /// re-include — with smart skip on and the watermark already past the movie.
        /// </summary>
        [Fact]
        public async Task ReIncludedMovie_Recreated_WithSmartSkipAndStaleWatermark()
        {
            var config = DefaultConfig();
            config.SmartSkipExisting = true;

            // Three syncs below, and RespondWith is single-shot — queue one body per sync.
            var vodJson = VodStreamsJson(VodStream(streamId: 2, name: "Drop Me", added: 1000));
            Handler.RespondWithSequence("get_vod_streams", new[] { vodJson, vodJson, vodJson });

            // Phase 1: normal sync writes it and advances the watermark past it.
            await MakeService().SyncMoviesAsync(config, None, SaveConfig);
            Assert.True(File.Exists(MovieStrmPath("Drop Me")));
            Assert.Equal(1000, config.LastMovieSyncTimestamp);

            // Phase 2: exclude it — folder goes away.
            config.ExcludedVodStreamIds = new[] { 2 };
            await MakeService().SyncMoviesAsync(config, None, SaveConfig);
            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, "Movies", "Drop Me")));

            // Phase 3: re-include. The movie is delta-unchanged and smart skip is on, so the
            // only thing that can rescue it is the File.Exists guard on the skip path.
            config.ExcludedVodStreamIds = new int[0];
            await MakeService().SyncMoviesAsync(config, None, SaveConfig);
            Assert.True(File.Exists(MovieStrmPath("Drop Me")));
        }

        [Fact]
        public async Task ExcludedMovie_DoesNotStallDeltaWatermark()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Keep Me", added: 1000),
                VodStream(streamId: 2, name: "Drop Me", added: 5000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(5000, config.LastMovieSyncTimestamp);
        }

        // -----------------------------------------------------------------
        // Orphan cleanup names what it deleted (ADR-F004 stage 1, backlog item 16)
        // -----------------------------------------------------------------

        /// <summary>Writes a movie STRM directly to disk, bypassing the sync.</summary>
        private string SeedMovieStrm(string movieName)
        {
            var path = MovieStrmPath(movieName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "http://fake-xtream/movie/user/pass/9999.mkv");
            return path;
        }

        [Fact]
        public async Task OrphanCleanup_NamesTheFilesItDeleted()
        {
            // A successful deletion was previously logged nowhere, at any level — only the count.
            // That made "what did the sync just remove?" unanswerable after the fact, and it is
            // exactly the question a churned provider id raises: a dead-id .strm is not in
            // writtenPaths, so it is swept as an orphan even though the user still wants it.
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));

            SeedMovieStrm("Gone Movie A");
            SeedMovieStrm("Gone Movie B");

            var logger = new RecordingLogger();
            await new StrmSyncService(logger, HttpClient).SyncMoviesAsync(config, None, SaveConfig);

            var summary = logger.Infos.Single(i => i.Contains("orphaned STRM files"));

            // Named, and relative to the library root rather than repeating it on every entry.
            Assert.Contains("Gone Movie A" + Path.DirectorySeparatorChar + "Gone Movie A.strm", summary);
            Assert.Contains("Gone Movie B" + Path.DirectorySeparatorChar + "Gone Movie B.strm", summary);

            // The root appears once, in "from {root}" — not once per sample entry.
            Assert.Equal(1, summary.Split(new[] { TempDir.Path }, StringSplitOptions.None).Length - 1);

            // Two files is well under the sample cap, so nothing should claim truncation.
            Assert.DoesNotContain(", ...", summary);
        }

        [Fact]
        public async Task OrphanCleanup_TruncatesTheSampleAndSaysSo()
        {
            // The sample exists to make the log answerable, not to dump a library into it. Past
            // the cap it must say it truncated — a silently-clipped list would be worse than a
            // bare count, because it reads as complete.
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));

            for (var i = 0; i < 16; i++)
            {
                SeedMovieStrm(string.Format("Gone Movie {0:D2}", i));
            }

            var logger = new RecordingLogger();
            await new StrmSyncService(logger, HttpClient).SyncMoviesAsync(config, None, SaveConfig);

            var summary = logger.Infos.Single(i => i.Contains("orphaned STRM files"));

            Assert.Contains("Removed 16 orphaned STRM files", summary);
            Assert.Contains(", ...", summary);
            // Sorted, so the sample is stable between runs rather than following readdir order.
            Assert.Contains("Gone Movie 00", summary);
            Assert.DoesNotContain("Gone Movie 15", summary);
        }
    }
}
