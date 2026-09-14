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

        [Fact]
        public async Task ExcludedMovie_MatchingAFolderJustWrittenForAnIncludedTitle_KeepsIt()
        {
            // Two provider entries for what the provider reports as different titles, differing
            // only in case. Exclusion is stored per StreamId, but folder matching is
            // case-insensitive -- so excluding one used to delete the other's folder moments
            // after the write loop created it. The library ends up missing a title the user
            // included, the next run writes and deletes it again, and nothing says why.
            //
            // Both must remain individually selectable: excluding one is not a statement about
            // the other. Series get this for free because ADR-F001 propagates exclusion across
            // the collapse group, whose key uses the same normalization as the deletion index,
            // so the pair is excluded together and never written. Movies have no such
            // propagation -- and should not, since it would make "sync just this one"
            // inexpressible.
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };

            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Some Film", added: 1000),
                VodStream(streamId: 2, name: "SOME FILM", added: 1000)));

            var svc = MakeService();
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(MovieStrmPath("Some Film")),
                "the included title's folder was deleted by the excluded title's name match");
            Assert.Equal(0, svc.MovieProgress.Failed);
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

        // -----------------------------------------------------------------
        // Decision store sizes are reported every sync (ADR-F005)
        // -----------------------------------------------------------------

        [Fact]
        public async Task SyncReportsTheSizeOfAllFourDecisionStores()
        {
            // The stores are the irreplaceable part of this plugin's state and nothing used to
            // surface their size, so a shrink was invisible until the review queue looked wrong.
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 10, 11, 12 };
            config.ExcludedSeriesIds = new[] { 20, 21 };
            config.ReviewedVodStreamIdsJson = "[30,31,32,33]";
            config.ReviewedSeriesIdsJson = "[40]";
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));

            var logger = new RecordingLogger();
            await new StrmSyncService(logger, HttpClient).SyncMoviesAsync(config, None, SaveConfig);

            var line = logger.Infos.Single(i => i.StartsWith("Decision stores:"));
            Assert.Contains("3 excluded movies", line);
            Assert.Contains("2 excluded series", line);
            Assert.Contains("4 reviewed movies", line);
            Assert.Contains("1 reviewed series", line);
        }

        [Fact]
        public async Task SyncReportsAnUnreadableStoreAsUnparseableNotZero()
        {
            // The entire point of the line. DeserializeIdSet returns an empty set for a genuinely
            // empty store and null for one it cannot read, and the sync fails OPEN on null — so a
            // store reported as 0 because it could not be parsed is the most alarming thing this
            // diagnostic can describe, and it must not be able to describe it as "0".
            var config = DefaultConfig();
            config.ReviewedVodStreamIdsJson = "[30,31,3";   // truncated
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));

            var logger = new RecordingLogger();
            await new StrmSyncService(logger, HttpClient).SyncMoviesAsync(config, None, SaveConfig);

            var line = logger.Infos.Single(i => i.StartsWith("Decision stores:"));
            Assert.Contains("UNPARSEABLE reviewed movies", line);
            Assert.DoesNotContain("0 reviewed movies", line);
        }

        [Fact]
        public async Task SyncReportsAGenuinelyEmptyStoreAsZero()
        {
            // The other half of the contract: empty must stay 0, or every fresh install reads as
            // an alarm and the distinction stops meaning anything.
            var config = DefaultConfig();
            config.ReviewedVodStreamIdsJson = string.Empty;
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));

            var logger = new RecordingLogger();
            await new StrmSyncService(logger, HttpClient).SyncMoviesAsync(config, None, SaveConfig);

            var line = logger.Infos.Single(i => i.StartsWith("Decision stores:"));
            Assert.Contains("0 reviewed movies", line);
            Assert.DoesNotContain("UNPARSEABLE", line);
        }

        [Fact]
        public async Task SyncReportsStoreSizesAfterItsOwnWriteBack()
        {
            // The review gate adds auto-reviewed ids to the reviewed set during the run. Logging
            // before that write-back would report a number that was already stale by the time it
            // was printed, which defeats using the line as a trend.
            var config = DefaultConfig();
            config.RequireReviewBeforeSync = true;
            config.EnableTmdbFolderNaming = true;
            config.ReviewedVodStreamIdsJson = string.Empty;

            // On disk under its TMDB id, so the gate exempts it and records its StreamId.
            var dir = Path.Combine(TempDir.Path, "Movies", "Kept Movie [tmdbid=555]");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Kept Movie [tmdbid=555].strm"),
                "http://fake-xtream/movie/user/pass/1.mkv");

            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Kept Movie", added: 1000, tmdbId: "555")));

            var logger = new RecordingLogger();
            await new StrmSyncService(logger, HttpClient).SyncMoviesAsync(config, None, SaveConfig);

            var line = logger.Infos.Single(i => i.StartsWith("Decision stores:"));
            Assert.Contains("1 reviewed movies", line);
        }

        // -----------------------------------------------------------------
        // Configuration rollback copies (ADR-F005 mechanism 3)
        // -----------------------------------------------------------------

        /// <summary>A stand-in for the plugin's config XML; production reads Plugin.ConfigPath.</summary>
        private string SeedConfigFile(string contents = "<PluginConfiguration><A>1</A></PluginConfiguration>")
        {
            var path = Path.Combine(TempDir.Path, "cfg", "Emby.Xtream.Plugin.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, contents);
            return path;
        }

        private string[] Rollbacks() => Directory.Exists(Path.Combine(TempDir.Path, "cfg", "xtream-rollback"))
            ? Directory.GetFiles(Path.Combine(TempDir.Path, "cfg", "xtream-rollback"), "*.xml")
            : new string[0];

        /// <summary>
        /// Registers the same VOD payload for several consecutive syncs.
        /// <c>RespondWith</c> enqueues exactly one response and the handler dequeues it, so a
        /// test that syncs twice gets "no registered response" on the second call.
        /// </summary>
        private void RegisterVodStreamsTimes(string json, int times)
            => Handler.RespondWithSequence("get_vod_streams", Enumerable.Repeat(json, times));

        private StrmSyncService ServiceWithRollback(string configPath, RecordingLogger logger = null) =>
            new StrmSyncService(logger ?? new RecordingLogger(), HttpClient)
            {
                ConfigRollbackSourcePath = configPath
            };

        [Fact]
        public async Task Rollback_CopiesTheConfigurationBeforeTheSyncWrites()
        {
            var config = DefaultConfig();
            var cfgPath = SeedConfigFile();
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));

            await ServiceWithRollback(cfgPath).SyncMoviesAsync(config, None, SaveConfig);

            var copy = Rollbacks().Single();
            Assert.Equal(File.ReadAllText(cfgPath), File.ReadAllText(copy));
        }

        [Fact]
        public async Task Rollback_SkipsWhenTheConfigurationHasNotChanged()
        {
            // A user syncing hourly and editing nothing would otherwise churn megabytes a day
            // and push the real last-good state out of the retention window — which would defeat
            // the entire mechanism rather than merely waste disk.
            var config = DefaultConfig();
            var cfgPath = SeedConfigFile();
            RegisterVodStreamsTimes(
                VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)), 2);

            await ServiceWithRollback(cfgPath).SyncMoviesAsync(config, None, SaveConfig);
            await ServiceWithRollback(cfgPath).SyncMoviesAsync(config, None, SaveConfig);

            Assert.Single(Rollbacks());
        }

        [Fact]
        public async Task Rollback_TakesAFreshCopyWhenTheConfigurationChanged()
        {
            var config = DefaultConfig();
            var cfgPath = SeedConfigFile();
            RegisterVodStreamsTimes(
                VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)), 2);

            await ServiceWithRollback(cfgPath).SyncMoviesAsync(config, None, SaveConfig);

            // Simulate a save between runs. No sleep needed: the copy filename carries
            // milliseconds precisely so back-to-back copies do not collide.
            File.WriteAllText(cfgPath, "<PluginConfiguration><A>2</A></PluginConfiguration>");

            await ServiceWithRollback(cfgPath).SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(2, Rollbacks().Length);
            // The PREVIOUS state has to be recoverable — that is the whole point.
            Assert.Contains(Rollbacks(), f => File.ReadAllText(f).Contains("<A>1</A>"));
        }

        [Fact]
        public async Task Rollback_BackToBackCopiesDoNotOverwriteEachOther()
        {
            // The movie sync saves the configuration and the series sync follows immediately, so
            // two copies within the same second are the normal case, not an edge one. At second
            // resolution the second overwrote the first — silently destroying the state the copy
            // existed to preserve.
            var config = DefaultConfig();
            var cfgPath = SeedConfigFile();
            RegisterVodStreamsTimes(
                VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)), 3);

            await ServiceWithRollback(cfgPath).SyncMoviesAsync(config, None, SaveConfig);
            File.WriteAllText(cfgPath, "<PluginConfiguration><A>2</A></PluginConfiguration>");
            await ServiceWithRollback(cfgPath).SyncMoviesAsync(config, None, SaveConfig);
            File.WriteAllText(cfgPath, "<PluginConfiguration><A>3</A></PluginConfiguration>");
            await ServiceWithRollback(cfgPath).SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(3, Rollbacks().Length);
            var bodies = Rollbacks().Select(File.ReadAllText).ToList();
            Assert.Contains(bodies, b => b.Contains("<A>1</A>"));
            Assert.Contains(bodies, b => b.Contains("<A>2</A>"));
            Assert.Contains(bodies, b => b.Contains("<A>3</A>"));
        }

        [Fact]
        public async Task Rollback_DisabledWhenCountIsZero()
        {
            var config = DefaultConfig();
            config.ConfigRollbackCount = 0;
            var cfgPath = SeedConfigFile();
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));

            await ServiceWithRollback(cfgPath).SyncMoviesAsync(config, None, SaveConfig);

            Assert.Empty(Rollbacks());
        }

        [Fact]
        public async Task Rollback_PrunesToTheConfiguredCount()
        {
            var config = DefaultConfig();
            config.ConfigRollbackCount = 3;
            var cfgPath = SeedConfigFile();
            var dir = Path.Combine(TempDir.Path, "cfg", "xtream-rollback");
            Directory.CreateDirectory(dir);
            for (var i = 1; i <= 6; i++)
            {
                File.WriteAllText(Path.Combine(dir, string.Format("2025010{0}-000000.xml", i)), "old");
            }
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));

            await ServiceWithRollback(cfgPath).SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(3, Rollbacks().Length);
            // The one just written must survive the prune, not merely leave the right count.
            Assert.Contains(Rollbacks(), f => File.ReadAllText(f).Contains("<A>1</A>"));
        }

        [Fact]
        public async Task Rollback_DoesNotFailTheSyncWhenItCannotBeWritten()
        {
            // A safety copy that can break the thing it protects is worse than none.
            var config = DefaultConfig();
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));

            var svc = ServiceWithRollback(Path.Combine(TempDir.Path, "nope", "missing.xml"));
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(MovieStrmPath("Kept Movie")));
            Assert.Equal(0, svc.MovieProgress.Failed);
        }

        // -----------------------------------------------------------------
        // The scheduled configuration backup (ADR-F005 mechanism 5)
        // -----------------------------------------------------------------

        private string[] Backups(string root = null)
        {
            var dir = Path.Combine(root ?? Path.Combine(TempDir.Path, "cfg", "xtream-backups"), "config");
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.xml") : new string[0];
        }

        [Fact]
        public void Backup_CopiesTheConfigurationUnderTheRecordsRoot()
        {
            var config = DefaultConfig();
            var cfgPath = SeedConfigFile();

            var written = ServiceWithRollback(cfgPath).BackupConfiguration(config);

            Assert.NotNull(written);
            Assert.Equal(File.ReadAllText(cfgPath), File.ReadAllText(Assert.Single(Backups())));
        }

        [Fact]
        public void Backup_SkipsWhenTheConfigurationIsUnchanged()
        {
            // A daily task against an unedited setup would otherwise churn the retention window
            // and push the genuinely interesting older copies out of it — defeating the
            // mechanism rather than merely wasting disk, exactly as for the rollback.
            var config = DefaultConfig();
            var cfgPath = SeedConfigFile();
            var svc = ServiceWithRollback(cfgPath);

            Assert.NotNull(svc.BackupConfiguration(config));
            Assert.Null(svc.BackupConfiguration(config));
            Assert.Single(Backups());
        }

        [Fact]
        public void Backup_DisabledWhenCountIsZero()
        {
            var config = DefaultConfig();
            config.ConfigBackupCount = 0;
            var cfgPath = SeedConfigFile();

            Assert.Null(ServiceWithRollback(cfgPath).BackupConfiguration(config));
            Assert.Empty(Backups());
        }

        [Fact]
        public void Backup_HonoursAConfiguredRecordsPath()
        {
            // The setting RELOCATES the root; it does not enable the feature. Pointing it at
            // another volume is the only thing that turns a rollback-grade copy into a real
            // backup, so it has to actually move the files rather than duplicate them.
            var config = DefaultConfig();
            var elsewhere = Path.Combine(TempDir.Path, "elsewhere");
            config.RecordsPath = elsewhere;
            var cfgPath = SeedConfigFile();

            ServiceWithRollback(cfgPath).BackupConfiguration(config);

            Assert.Single(Backups(elsewhere));
            Assert.Empty(Backups());
        }

        [Fact]
        public void Backup_PrunesToTheConfiguredCount()
        {
            var config = DefaultConfig();
            config.ConfigBackupCount = 2;
            var cfgPath = SeedConfigFile();
            var svc = ServiceWithRollback(cfgPath);

            for (var i = 0; i < 4; i++)
            {
                // Content must differ each time or the unchanged-skip above suppresses the copy.
                File.WriteAllText(cfgPath, "<PluginConfiguration><A>" + i + "</A></PluginConfiguration>");
                svc.BackupConfiguration(config);
            }

            Assert.Equal(2, Backups().Length);
        }

        [Fact]
        public async Task RecordsPath_RelocatesTheSnapshotAndCountsLogToo()
        {
            // One root holds all three artifacts, so relocating it must move every one of them —
            // a recovery that has to look in two places is the split this design exists to avoid.
            var config = DefaultConfig();
            var elsewhere = Path.Combine(TempDir.Path, "elsewhere");
            config.RecordsPath = elsewhere;
            var cfgPath = SeedConfigFile();
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));

            await ServiceWithRollback(cfgPath).SyncMoviesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(Path.Combine(elsewhere, "counts.log")));
            Assert.Single(Directory.GetFiles(Path.Combine(elsewhere, "snapshots"), "catalogue-ids-*.tsv"));
        }

        // -----------------------------------------------------------------
        // The durable store-size history (ADR-F005 mechanism 7)
        // -----------------------------------------------------------------

        private string CountsLogPath()
            => Path.Combine(TempDir.Path, "cfg", "xtream-backups", "counts.log");

        [Fact]
        public async Task CountsLog_AppendsOneLineInTheExternalCanaryFormat()
        {
            // Byte-compatible with what scripts/config-counts-canary.py has been appending to
            // users' own logs for months: full field names, local time to the minute,
            // single-spaced, no trailing path — and the two EXCLUSION stores first, which is not
            // the order the stores are declared in. A tidier format would split an existing
            // history into two series that cannot be compared.
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 7, 8, 9 };
            config.ExcludedSeriesIds = new[] { 11 };
            config.ReviewedVodStreamIdsJson = "[1,2]";
            config.ReviewedSeriesIdsJson = string.Empty;
            var cfgPath = SeedConfigFile();
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));

            await ServiceWithRollback(cfgPath).SyncMoviesAsync(config, None, SaveConfig);

            var line = Assert.Single(File.ReadAllLines(CountsLogPath()));
            Assert.Matches(
                @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2} ExcludedVodStreamIds=3 ExcludedSeriesIds=1 "
                + @"ReviewedVodStreamIdsJson=2 ReviewedSeriesIdsJson=0$",
                line);
        }

        [Fact]
        public async Task CountsLog_DoesNotRepeatAByteIdenticalLine()
        {
            // The movie and series syncs run back to back, so identical counts land twice inside
            // one minute — and at minute resolution that is literally the same line.
            var config = DefaultConfig();
            var cfgPath = SeedConfigFile();
            RegisterVodStreamsTimes(
                VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)), 2);

            var svc = ServiceWithRollback(cfgPath);
            await svc.SyncMoviesAsync(config, None, SaveConfig);
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.Single(File.ReadAllLines(CountsLogPath()));
        }

        [Fact]
        public async Task CountsLog_KeepsAppendingWhenTheCountsChange()
        {
            // The mirror of the test above: de-duplication must not be so eager that a real
            // change goes unrecorded. A fresh service each time also stands in for a restart —
            // the file is the history, so it must be extended rather than replaced.
            var config = DefaultConfig();
            var cfgPath = SeedConfigFile();
            RegisterVodStreamsTimes(
                VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)), 2);

            await ServiceWithRollback(cfgPath).SyncMoviesAsync(config, None, SaveConfig);
            config.ExcludedSeriesIds = new[] { 42 };
            await ServiceWithRollback(cfgPath).SyncMoviesAsync(config, None, SaveConfig);

            var lines = File.ReadAllLines(CountsLogPath());
            Assert.Equal(2, lines.Length);
            Assert.Contains("ExcludedSeriesIds=0", lines[0]);
            Assert.Contains("ExcludedSeriesIds=1", lines[1]);
        }

        [Fact]
        public async Task CountsLog_ReportsUnparseableRatherThanZero()
        {
            // 0 and "could not read it" are opposite conditions that look identical as a number.
            // Reporting a broken store as 0 hides exactly the failure this history exists to
            // catch — the same contract DeserializeIdSet and the external canary both hold.
            var config = DefaultConfig();
            config.ReviewedVodStreamIdsJson = "[1,2";
            var cfgPath = SeedConfigFile();
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));

            await ServiceWithRollback(cfgPath).SyncMoviesAsync(config, None, SaveConfig);

            Assert.Contains(
                "ReviewedVodStreamIdsJson=UNPARSEABLE",
                Assert.Single(File.ReadAllLines(CountsLogPath())));
        }

        [Fact]
        public async Task CountsLog_DoesNotFailTheSyncWhenItCannotBeWritten()
        {
            // Same rule as the rollback copy: a record that can break the thing it documents is
            // worse than no record.
            //
            // The root has to be genuinely unwritable to test this. A merely absent directory is
            // not — CreateDirectory builds intermediates, so pointing at a missing folder would
            // succeed and the test would pass without exercising anything. Rooting it under a
            // regular FILE cannot succeed.
            var config = DefaultConfig();
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));

            var blocker = Path.Combine(TempDir.Path, "blocker");
            File.WriteAllText(blocker, "a file, not a directory");

            var svc = ServiceWithRollback(Path.Combine(blocker, "Emby.Xtream.Plugin.xml"));
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(Directory.Exists(Path.Combine(blocker, "xtream-backups")));

            Assert.True(File.Exists(MovieStrmPath("Kept Movie")));
            Assert.Equal(0, svc.MovieProgress.Failed);
        }

        // -----------------------------------------------------------------
        // The dated catalogue snapshot (ADR-F005 mechanism 6)
        // -----------------------------------------------------------------

        private string SnapshotPath() => Path.Combine(
            TempDir.Path, "cfg", "xtream-backups", "snapshots",
            "catalogue-ids-" + DateTime.Now.ToString("yyyy-MM-dd") + ".tsv");

        [Fact]
        public async Task Snapshot_WritesRowsInTheExternalScriptFormat()
        {
            // repair-id-churn.py must read a plugin-written file with no flags and no changes,
            // so the header, the tab layout and the empty-not-zero TMDB field all have to match
            // catalogue-snapshot.py exactly.
            var config = DefaultConfig();
            var cfgPath = SeedConfigFile();
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 42, name: "Some Film", added: 1000, tmdbId: "603"),
                VodStream(streamId: 43, name: "No Tmdb Film", added: 1000)));

            await ServiceWithRollback(cfgPath).SyncMoviesAsync(config, None, SaveConfig);

            var raw = File.ReadAllText(SnapshotPath());
            Assert.DoesNotContain("\r", raw); // readers strip only '\n'; a stray CR lands in a field

            var lines = raw.Split('\n');
            Assert.Equal("#kind\tid\ttmdb\tname\tcategory", lines[0]);
            Assert.Contains("movie\t42\t603\tSome Film\t", lines);
            Assert.Contains("movie\t43\t\tNo Tmdb Film\t", lines);
        }

        [Fact]
        public async Task Snapshot_FirstWriteOfTheDayIsNeverOverwritten()
        {
            // The whole safety property. A snapshot is the only record of what a now-dead id used
            // to be, so a sync running several times a day that rewrote today's file would
            // destroy the morning's pre-event copy every afternoon — turning the artifact that
            // makes a churn event recoverable into the one that makes it unrecoverable.
            var config = DefaultConfig();
            var cfgPath = SeedConfigFile();
            RegisterVodStreamsTimes(
                VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)), 2);

            var svc = ServiceWithRollback(cfgPath);
            await svc.SyncMoviesAsync(config, None, SaveConfig);
            var first = File.ReadAllText(SnapshotPath());

            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(first, File.ReadAllText(SnapshotPath()));
        }

        [Fact]
        public async Task Snapshot_SkippedWhenTheCatalogueFetchWasPartial()
        {
            // A short listing written first would be locked in for the rest of the day by the
            // rule above, and a snapshot missing exactly the titles that later go dead is worse
            // than no snapshot — it reads as authoritative.
            var config = DefaultConfig();
            config.SelectedVodCategoryIds = new[] { 1, 2 };
            var cfgPath = SeedConfigFile();
            Handler.RespondWith("category_id=1",
                VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));
            Handler.RespondWith("category_id=2", "{}", HttpStatusCode.InternalServerError);

            await ServiceWithRollback(cfgPath).SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(SnapshotPath()));
        }

        [Fact]
        public async Task Snapshot_AddsToTheDayFileWithoutDisplacingTheOtherKind()
        {
            // The movie and series syncs each contribute their own rows to ONE file, which is why
            // "already recorded today" is judged per kind rather than per file. A movie sync must
            // extend a file the series sync started, not replace it — repair-id-churn.py resolves
            // both stores from a single snapshot.
            var config = DefaultConfig();
            var cfgPath = SeedConfigFile();
            var path = SnapshotPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "#kind\tid\ttmdb\tname\tcategory\nseries\t900\t\tSome Show\t7\n");

            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));
            await ServiceWithRollback(cfgPath).SyncMoviesAsync(config, None, SaveConfig);

            var lines = File.ReadAllText(path).Split('\n');
            Assert.Equal("#kind\tid\ttmdb\tname\tcategory", lines[0]);
            Assert.Contains("series\t900\t\tSome Show\t7", lines);
            Assert.Contains("movie\t1\t\tKept Movie\t", lines);
        }

        // -----------------------------------------------------------------
        // The full deleted-path record (ADR-F005 mechanism 2)
        // -----------------------------------------------------------------

        /// <summary>Somewhere for the deletion record to go; production asks Emby for its log dir.</summary>
        private string RecordDir()
        {
            var dir = Path.Combine(TempDir.Path, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string[] Records(string dir) => Directory.GetFiles(dir, "xtream-deleted-*.txt");

        [Fact]
        public async Task OrphanCleanup_WritesEveryDeletedPath_WhenItExceedsTheSample()
        {
            // The log carries 15 paths; the events that prompt the question are far larger than
            // that — 360 files under Shows and 126 under Movies, neither answerable afterwards.
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));

            for (var i = 0; i < 20; i++)
            {
                SeedMovieStrm(string.Format("Gone Movie {0:D2}", i));
            }

            var logger = new RecordingLogger();
            var svc = new StrmSyncService(logger, HttpClient) { DeletionRecordDirectory = RecordDir() };
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            var record = Records(RecordDir()).Single();
            var body = File.ReadAllText(record);

            // ALL of them, not the sample — including entries past the 15th, which is the
            // entire point of the file.
            Assert.Contains("Gone Movie 00", body);
            Assert.Contains("Gone Movie 19", body);
            Assert.Equal(20, File.ReadAllLines(record).Count(l => l.StartsWith("Gone Movie ")));

            // And the log has to say where it went, or nobody finds it.
            var summary = logger.Infos.Single(i => i.Contains("orphaned STRM files"));
            Assert.Contains(record, summary);
        }

        [Fact]
        public async Task OrphanCleanup_WritesNoRecord_WhenTheSampleAlreadyCoversIt()
        {
            // A routine sweep of two files is fully described by the log line. Writing a file
            // for every cleanup would bury the ones that matter.
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));

            SeedMovieStrm("Gone Movie A");
            SeedMovieStrm("Gone Movie B");

            var svc = new StrmSyncService(new RecordingLogger(), HttpClient)
            {
                DeletionRecordDirectory = RecordDir()
            };
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.Empty(Records(RecordDir()));
        }

        [Fact]
        public async Task OrphanCleanup_PrunesOldRecords_OldestFirst()
        {
            // Unbounded diagnostics become their own problem. Name sort is chronological because
            // the timestamp leads the filename — if that ever stops being true this prunes the
            // wrong files, which is why the ordering is asserted rather than assumed.
            var dir = RecordDir();
            for (var i = 1; i <= 12; i++)
            {
                File.WriteAllText(
                    Path.Combine(dir, string.Format("xtream-deleted-202501{0:D2}-000000-Movies.txt", i)),
                    "seed");
            }
            var seeded = Records(dir).Length;

            var config = DefaultConfig();
            config.CleanupOrphans = true;
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));
            for (var i = 0; i < 20; i++)
            {
                SeedMovieStrm(string.Format("Gone Movie {0:D2}", i));
            }

            var svc = new StrmSyncService(new RecordingLogger(), HttpClient) { DeletionRecordDirectory = dir };
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            var after = Records(dir);
            Assert.True(seeded > 10, "the seed must exceed the retention or this asserts nothing");
            Assert.Equal(10, after.Length);

            // The survivor set must include the one just written, not merely be the right size.
            Assert.Contains(after, f => Path.GetFileName(f).StartsWith("xtream-deleted-2026"));
        }

        [Fact]
        public async Task OrphanCleanup_StillDeletes_WhenTheRecordCannotBeWritten()
        {
            // Recording what happened must never become a reason for the cleanup to fail: the
            // files are already gone by the time the record is written.
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));
            for (var i = 0; i < 20; i++)
            {
                SeedMovieStrm(string.Format("Gone Movie {0:D2}", i));
            }

            // A path that cannot be created: an existing FILE standing where the directory goes.
            var blocked = Path.Combine(TempDir.Path, "blocked");
            File.WriteAllText(blocked, "not a directory");

            var logger = new RecordingLogger();
            var svc = new StrmSyncService(logger, HttpClient) { DeletionRecordDirectory = blocked };
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(MovieStrmPath("Gone Movie 00")), "the orphans must still be removed");
            Assert.Equal(0, svc.MovieProgress.Failed);
            var summary = logger.Infos.Single(i => i.Contains("orphaned STRM files"));
            Assert.Contains("could not be written", summary);
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
