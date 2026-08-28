using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using Emby.Xtream.Plugin.Tests.Fakes;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Integration tests for <see cref="StrmSyncService.SyncSeriesAsync"/>.
    ///
    /// Path structure (SeriesFolderMode = "single"):
    ///   {StrmLibraryPath}/Shows/{seriesName}/Season {N:D2}/{seriesName} - S{N:D2}E{N:D2} - {title}.strm
    ///
    /// URL patterns (no selected categories):
    ///   ...player_api.php?...&amp;action=get_series
    ///   ...player_api.php?...&amp;action=get_series_info&amp;series_id={id}
    ///
    /// Note: get_series_categories is NOT called when SeriesFolderMode = "single".
    /// </summary>
    public class SyncSeriesIntegrationTests : SyncTestBase
    {
        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// Compute the expected STRM path for a plain-ASCII episode
        /// (no TMDB/TVDb IDs in folder name).
        ///
        /// <paramref name="title"/> is accepted but deliberately NOT part of the path:
        /// filenames are keyed on the SxxExx code alone, so a provider re-titling an
        /// episode overwrites in place instead of creating a second file. Call sites keep
        /// passing the provider's title so each fixture still documents what its payload
        /// contained.
        /// </summary>
        private string EpisodeStrmPath(
            string seriesName, int season, int episode, string title = null)
        {
            var seasonFolder = $"Season {season:D2}";
            var fileName = $"{seriesName} - S{season:D2}E{episode:D2}.strm";
            return Path.Combine(TempDir.Path, "Shows", seriesName, seasonFolder, fileName);
        }

        /// <summary>
        /// Register both the series list and detail responses needed for a single series.
        /// </summary>
        private void RegisterSeriesResponses(string seriesListJson, string seriesDetailJson, int seriesId = 1)
        {
            Handler.RespondWith("action=get_series", seriesListJson);
            Handler.RespondWith($"action=get_series_info&series_id={seriesId}", seriesDetailJson);
        }

        // -----------------------------------------------------------------
        // Test 1: HappyPath_WritesEpisodeFile
        // -----------------------------------------------------------------

        [Fact]
        public async Task HappyPath_WritesEpisodeFile()
        {
            var config = DefaultConfig();
            var list = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "2000"));
            var detail = SeriesDetailJson(seriesId: 1, seasonNum: 1, episodeNum: 1,
                title: "Episode Title", ext: "mp4");
            RegisterSeriesResponses(list, detail, seriesId: 1);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var strmPath = EpisodeStrmPath("Test Show", season: 1, episode: 1, title: "Episode Title");
            Assert.True(File.Exists(strmPath), $"Expected STRM file at: {strmPath}");

            var content = File.ReadAllText(strmPath);
            // URL format: {BaseUrl}/series/{Username}/{Password}/{episodeId}.{ext}
            Assert.Equal("http://fake-xtream/series/user/pass/101.mp4", content);

            // flag-tracking save + timestamp save + episode hashes save → 3 saves
            Assert.Equal(3, SaveConfigCallCount);
        }

        // -----------------------------------------------------------------
        // Test 2: SmartSkip_ExistingEpisode_NotRewritten
        // -----------------------------------------------------------------

        [Fact]
        public async Task SmartSkip_ExistingEpisode_NotRewritten()
        {
            // lastSeriesTs = 9999, series.lastModified = "2000" → 2000 < 9999 → isChangedSeries = false
            // SmartSkipExisting = true AND directory with .strm exists → skip
            var config = DefaultConfig();
            config.SmartSkipExisting = true;
            config.LastSeriesSyncTimestamp = 9999;

            // Pre-write a sentinel episode to trigger smart-skip
            var strmPath = EpisodeStrmPath("Test Show", season: 1, episode: 1, title: "Episode Title");
            Directory.CreateDirectory(Path.GetDirectoryName(strmPath));
            File.WriteAllText(strmPath, "SENTINEL");

            var list = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "2000"));
            // Pre-fetch skip: lastModified (2000) < lastSeriesSyncTimestamp (9999) → isChangedSeries = false
            // → folder found in directory index with existing .strm → return BEFORE calling get_series_info.
            // Register the detail response anyway in case the test ever regresses (FakeHttpHandler only
            // throws on unmatched URLs that ARE actually called).
            Handler.RespondWith("action=get_series", list);
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson());

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.Equal("SENTINEL", File.ReadAllText(strmPath));
        }

        // -----------------------------------------------------------------
        // Test 3: SmartSkip_ChangedSeries_EpisodeRewritten
        // -----------------------------------------------------------------

        [Fact]
        public async Task SmartSkip_ChangedSeries_EpisodeRewritten()
        {
            // lastSeriesTs = 1000, series.lastModified = "5000" → 5000 > 1000 → isChangedSeries = true
            // Even with SmartSkipExisting = true, changed series are always written
            var config = DefaultConfig();
            config.SmartSkipExisting = true;
            config.LastSeriesSyncTimestamp = 1000;

            // Pre-write a sentinel — it must be overwritten because series has changed
            var strmPath = EpisodeStrmPath("Test Show", season: 1, episode: 1, title: "Episode Title");
            Directory.CreateDirectory(Path.GetDirectoryName(strmPath));
            File.WriteAllText(strmPath, "SENTINEL");

            var list = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "5000"));
            var detail = SeriesDetailJson(seriesId: 1, seasonNum: 1, episodeNum: 1,
                title: "Episode Title", ext: "mp4");
            RegisterSeriesResponses(list, detail, seriesId: 1);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var content = File.ReadAllText(strmPath);
            Assert.NotEqual("SENTINEL", content);
            Assert.Contains("http://fake-xtream/series/user/pass/101.mp4", content);
        }

        // -----------------------------------------------------------------
        // Test 4: NamingVersionUpgrade_ResetsTimestamp_EpisodeRewritten
        // -----------------------------------------------------------------

        [Fact]
        public async Task NamingVersionUpgrade_ResetsTimestamp_EpisodeRewritten()
        {
            // StrmNamingVersion = 0 → upgrade resets LastSeriesSyncTimestamp to 0
            // With lastSeriesTs = 0 → isChangedSeries = true → episode is always written
            var config = DefaultConfig();
            config.SmartSkipExisting = true;
            config.LastSeriesSyncTimestamp = 9999; // Would normally cause smart-skip
            config.StrmNamingVersion = 0;          // Stale version → triggers upgrade → resets to 0

            var strmPath = EpisodeStrmPath("Test Show", season: 1, episode: 1, title: "Episode Title");
            Directory.CreateDirectory(Path.GetDirectoryName(strmPath));
            File.WriteAllText(strmPath, "SENTINEL");

            var list = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "2000"));
            var detail = SeriesDetailJson(seriesId: 1, seasonNum: 1, episodeNum: 1,
                title: "Episode Title", ext: "mp4");
            RegisterSeriesResponses(list, detail, seriesId: 1);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var content = File.ReadAllText(strmPath);
            Assert.NotEqual("SENTINEL", content);
            // At least 2 saves: naming-version upgrade + timestamp update
            Assert.True(SaveConfigCallCount >= 2, $"Expected >= 2 saves, got {SaveConfigCallCount}");
        }

        // -----------------------------------------------------------------
        // Test 5: OrphanInSeasonSubdir_FileAndEmptyDirsDeleted
        // -----------------------------------------------------------------

        [Fact]
        public async Task OrphanInSeasonSubdir_FileAndEmptyDirsDeleted()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = true;

            // Pre-write an orphan episode for "Old Show"
            var orphanStrm = EpisodeStrmPath("Old Show", season: 1, episode: 1, title: "Gone");
            Directory.CreateDirectory(Path.GetDirectoryName(orphanStrm));
            File.WriteAllText(orphanStrm, "http://fake-xtream/series/user/pass/99.mp4");

            // Provider returns only "New Show"
            var list = SeriesListJson(Series(seriesId: 2, name: "New Show", lastModified: "3000"));
            var detail = SeriesDetailJson(seriesId: 2, seasonNum: 1, episodeNum: 1,
                title: "Ep One", ext: "mp4");
            Handler.RespondWith("action=get_series", list);
            Handler.RespondWith("action=get_series_info&series_id=2", detail);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            // Orphan STRM must be deleted
            Assert.False(File.Exists(orphanStrm), "Orphan STRM file should have been deleted");

            // Empty Season 01 dir must also be removed (CleanupOrphans walks up)
            var orphanSeasonDir = Path.GetDirectoryName(orphanStrm);
            Assert.False(Directory.Exists(orphanSeasonDir),
                "Empty season subdirectory should have been removed");

            // New show episode must exist
            var newEpisode = EpisodeStrmPath("New Show", season: 1, episode: 1, title: "Ep One");
            Assert.True(File.Exists(newEpisode), $"Expected new episode at: {newEpisode}");
        }

        // -----------------------------------------------------------------
        // Test 6: AddedZeroProvider_SeriesNotUpdated_FileStillWrittenNoSmartSkip
        // -----------------------------------------------------------------

        [Fact]
        public async Task AddedZeroProvider_SeriesNotUpdated_FileStillWrittenNoSmartSkip()
        {
            // lastModified = "0" → seriesLm = 0 → maxSeriesTs stays at lastSeriesTs (100)
            // SmartSkipExisting = false → always write
            var config = DefaultConfig();
            config.LastSeriesSyncTimestamp = 100;
            config.SmartSkipExisting = false;

            var list = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "0"));
            var detail = SeriesDetailJson(seriesId: 1, seasonNum: 1, episodeNum: 1,
                title: "Ep One", ext: "mp4");
            RegisterSeriesResponses(list, detail, seriesId: 1);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var strmPath = EpisodeStrmPath("Test Show", season: 1, episode: 1, title: "Ep One");
            Assert.True(File.Exists(strmPath), $"Expected STRM file at: {strmPath}");
            // flag-tracking save + episode hashes save → 2 saves (no timestamp save: maxSeriesTs 0 < 100)
            Assert.Equal(2, SaveConfigCallCount);
        }

        // -----------------------------------------------------------------
        // Test 7: SeriesWithNoEpisodes_NoCrashNoDirRequired
        // -----------------------------------------------------------------

        [Fact]
        public async Task SeriesWithNoEpisodes_NoCrashNoDirRequired()
        {
            var config = DefaultConfig();
            var list = SeriesListJson(Series(seriesId: 1, name: "Empty Show", lastModified: "1000"));
            // Detail returns no episodes
            var emptyDetail = System.Text.Json.JsonSerializer.Serialize(new
            {
                info = new { series_id = 1, name = "Empty Show", tmdb = "" },
                seasons = new object[0],
                episodes = new System.Collections.Generic.Dictionary<string, object[]>()
            });
            Handler.RespondWith("action=get_series", list);
            Handler.RespondWith("action=get_series_info&series_id=1", emptyDetail);

            // Must not throw
            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var showsRoot = Path.Combine(TempDir.Path, "Shows");
            var files = Directory.Exists(showsRoot)
                ? Directory.GetFiles(showsRoot, "*.strm", SearchOption.AllDirectories)
                : Array.Empty<string>();
            Assert.Empty(files);
        }

        // -----------------------------------------------------------------
        // Test 8: EpisodeTitleDeduplication_TitleNotDuplicatedInFilename
        // -----------------------------------------------------------------

        [Fact]
        public async Task ProviderEmbeddedTitle_DoesNotAppearInFilename()
        {
            // Provider embeds series name + episode code in the episode title:
            // title = "Breaking Bad - S01E01". Episode titles are not part of the
            // filename at all, so the name is keyed on the episode code alone and
            // nothing from the title can leak into it.
            var config = DefaultConfig();
            var list = SeriesListJson(Series(seriesId: 1, name: "Breaking Bad", lastModified: "2000"));
            var detail = System.Text.Json.JsonSerializer.Serialize(new
            {
                info = new { series_id = 1, name = "Breaking Bad", tmdb = "" },
                seasons = new object[0],
                episodes = new System.Collections.Generic.Dictionary<string, object[]>
                {
                    ["1"] = new object[]
                    {
                        new { id = 101, episode_num = 1, title = "Breaking Bad - S01E01",
                              container_extension = "mp4", season = 1 }
                    }
                }
            });
            RegisterSeriesResponses(list, detail, seriesId: 1);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var expectedPath = EpisodeStrmPath("Breaking Bad", season: 1, episode: 1);
            Assert.True(File.Exists(expectedPath), $"Expected STRM at: {expectedPath}");

            // Exactly one file for the episode — no title-derived variant beside it.
            var seasonDir = Path.Combine(TempDir.Path, "Shows", "Breaking Bad", "Season 01");
            Assert.Single(Directory.GetFiles(seasonDir, "*.strm"));
        }

        // -----------------------------------------------------------------
        // A re-titled episode must overwrite in place, not duplicate
        // -----------------------------------------------------------------

        [Fact]
        public async Task EpisodeRetitledOnRefetch_OverwritesInPlace_NoDuplicate()
        {
            // Providers hand back different episode titles across refreshes. When the
            // title was part of the filename, a re-fetch wrote a NEW file beside the old
            // one instead of replacing it — one duplicate per re-titled episode.
            //
            // A title change alone never reaches the write loop (the change-hash is keyed
            // on episode IDs, so an identical ID set hash-skips), so this models what
            // actually happened in the wild: a new episode arrives AND the rest of the
            // payload comes back re-titled in the same refresh.
            //
            // Orphan cleanup is off so the duplicate is observed rather than swept: under
            // the old naming the stale-titled file would be deleted as an orphan and the
            // count would come out right regardless. That is also what happened live —
            // cleanup was gated off by failures, which is why duplicates accumulated.
            var config = DefaultConfig();
            config.CleanupOrphans = false;

            var listV1 = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "2000"));
            var listV2 = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "3000"));

            string Detail(params object[] episodes) => System.Text.Json.JsonSerializer.Serialize(new
            {
                info = new { series_id = 1, name = "Test Show", tmdb = "" },
                seasons = new object[0],
                episodes = new System.Collections.Generic.Dictionary<string, object[]> { ["1"] = episodes }
            });

            var detailV1 = Detail(
                new { id = 101, episode_num = 1, title = "Pilot", container_extension = "mp4", season = 1 });
            var detailV2 = Detail(
                new { id = 101, episode_num = 1, title = "Pilot (Extended Cut)", container_extension = "mp4", season = 1 },
                new { id = 102, episode_num = 2, title = "Second",               container_extension = "mp4", season = 1 });

            // Register the more specific rule first: the detail URL contains
            // "action=get_series_info&series_id=1", while the list URL does not, so the
            // list falls through to the broader rule. Registering the broad rule first
            // would let a queued list body answer a detail request.
            Handler.RespondWithSequence("action=get_series_info&series_id=1", new[] { detailV1, detailV2 });
            Handler.RespondWithSequence("action=get_series", new[] { listV1, listV2 });

            var svc = MakeService();
            await svc.SyncSeriesAsync(config, None, SaveConfig);
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            var seasonDir = Path.Combine(TempDir.Path, "Shows", "Test Show", "Season 01");
            Assert.Equal(2, Directory.GetFiles(seasonDir, "*.strm").Length);
            Assert.True(File.Exists(EpisodeStrmPath("Test Show", season: 1, episode: 1)));
            Assert.True(File.Exists(EpisodeStrmPath("Test Show", season: 1, episode: 2)));
        }

        // -----------------------------------------------------------------
        // MigrateEpisodeFilenames — one-time rename to the title-free form
        // -----------------------------------------------------------------

        /// <summary>Writes an episode STRM with plugin-owned content unless overridden.</summary>
        private string SeedEpisode(string show, string season, string fileName, string content = null)
        {
            var dir = Path.Combine(TempDir.Path, "Shows", show, season);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName);
            File.WriteAllText(path, content ?? "http://fake-xtream/series/user/pass/101.mp4");
            return path;
        }

        [Fact]
        public void MigrateEpisodeFilenames_RenamesTitledFileInPlace()
        {
            var config = DefaultConfig();
            config.EpisodeFilenameMigrationVersion = 0;
            var titled = SeedEpisode("Test Show", "Season 01", "Test Show - S01E01 - Some Title.strm");

            var changed = MakeService().MigrateEpisodeFilenames(config, () => { });

            Assert.Equal(1, changed);
            Assert.False(File.Exists(titled));
            Assert.True(File.Exists(EpisodeStrmPath("Test Show", season: 1, episode: 1)));
            Assert.Equal(StrmSyncService.CurrentEpisodeFilenameVersion, config.EpisodeFilenameMigrationVersion);
        }

        [Fact]
        public void MigrateEpisodeFilenames_TitledAndUntitledPair_KeepsUntitled()
        {
            var config = DefaultConfig();
            config.EpisodeFilenameMigrationVersion = 0;
            var titled = SeedEpisode("Test Show", "Season 01", "Test Show - S01E01 - Some Title.strm");
            var untitled = SeedEpisode("Test Show", "Season 01", "Test Show - S01E01.strm");

            var changed = MakeService().MigrateEpisodeFilenames(config, () => { });

            Assert.Equal(1, changed);
            Assert.False(File.Exists(titled));
            Assert.True(File.Exists(untitled));
        }

        [Fact]
        public void MigrateEpisodeFilenames_TitleContainingEpisodeCode_SplitsAtFirstCode()
        {
            // A title like "Recap of S01E01" must not be mistaken for the episode code —
            // the name has to keep S01E02, the code the file is actually for.
            var config = DefaultConfig();
            config.EpisodeFilenameMigrationVersion = 0;
            SeedEpisode("Test Show", "Season 01", "Test Show - S01E02 - Recap of S01E01.strm");

            MakeService().MigrateEpisodeFilenames(config, () => { });

            Assert.True(File.Exists(EpisodeStrmPath("Test Show", season: 1, episode: 2)));
            Assert.False(File.Exists(EpisodeStrmPath("Test Show", season: 1, episode: 1)));
        }

        [Fact]
        public void MigrateEpisodeFilenames_LeavesFilesThePluginDidNotWrite()
        {
            var config = DefaultConfig();
            config.EpisodeFilenameMigrationVersion = 0;
            var foreign = SeedEpisode("Test Show", "Season 01", "Test Show - S01E01 - Some Title.strm",
                content: "http://someone-elses-server/video.mkv");

            var changed = MakeService().MigrateEpisodeFilenames(config, () => { });

            Assert.Equal(0, changed);
            Assert.True(File.Exists(foreign), "A file the plugin did not write must be left alone");
        }

        // -----------------------------------------------------------------
        // Silent-gap diagnostic: a series that leaves no episode hash behind
        // -----------------------------------------------------------------

        /// <summary>
        /// Sets up a series that is delta-unchanged with files on disk but has no stored
        /// episode hash — the state that hid a real missing-episode bug for hours, because
        /// the sync reported complete success while never verifying the series at all.
        /// </summary>
        private void SeedSkippedSeriesWithoutHash(PluginConfiguration config, string storedHashesJson)
        {
            config.SmartSkipExisting = true;
            config.LastSeriesSyncTimestamp = 5000;      // ahead of the series' own timestamp
            config.SeriesEpisodeHashesJson = storedHashesJson;

            SeedEpisode("Test Show", "Season 01", "Test Show - S01E01.strm");
            Handler.RespondWith("action=get_series",
                SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "2000")));
        }

        [Fact]
        public async Task SeriesSkippedWithNoStoredHash_IsWarnedAbout()
        {
            var config = DefaultConfig();
            SeedSkippedSeriesWithoutHash(config, string.Empty);

            var logger = new RecordingLogger();
            await new StrmSyncService(logger, HttpClient).SyncSeriesAsync(config, None, SaveConfig);

            Assert.Contains(logger.Warnings,
                w => w.Contains("no episode hash recorded") && w.Contains("id=1"));
        }

        [Fact]
        public async Task SeriesSkippedWithStoredHash_IsNotWarnedAbout()
        {
            // The same skip, but the hash is carried forward — the ordinary case, which
            // must stay quiet or the warning is noise on every sync.
            var config = DefaultConfig();
            SeedSkippedSeriesWithoutHash(config, "{\"1\":\"deadbeef\"}");

            var logger = new RecordingLogger();
            await new StrmSyncService(logger, HttpClient).SyncSeriesAsync(config, None, SaveConfig);

            Assert.DoesNotContain(logger.Warnings, w => w.Contains("no episode hash recorded"));
        }

        [Fact]
        public void MigrateEpisodeFilenames_AlreadyMigrated_DoesNotRescan()
        {
            // DefaultConfig is pinned at the current version, so this models an install
            // that has already migrated: the tree must not be walked on every later sync.
            var config = DefaultConfig();
            var titled = SeedEpisode("Test Show", "Season 01", "Test Show - S01E01 - Some Title.strm");

            var changed = MakeService().MigrateEpisodeFilenames(config, () => { });

            Assert.Equal(0, changed);
            Assert.True(File.Exists(titled));
        }

        // -----------------------------------------------------------------
        // Test 9: EpisodeHashSkip_UnchangedEpisodes_NoFileIO
        // -----------------------------------------------------------------

        [Fact]
        public async Task EpisodeHashSkip_UnchangedEpisodes_NoFileIO()
        {
            // First run: write the episode and populate the hash
            var config = DefaultConfig();
            config.SmartSkipExisting = true;

            var list = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "2000"));
            var detail = SeriesDetailJson(seriesId: 1, seasonNum: 1, episodeNum: 1,
                title: "Episode Title", ext: "mp4");
            RegisterSeriesResponses(list, detail, seriesId: 1);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var strmPath = EpisodeStrmPath("Test Show", season: 1, episode: 1, title: "Episode Title");
            Assert.True(File.Exists(strmPath));

            // config now has SeriesEpisodeHashesJson populated from the first run
            Assert.False(string.IsNullOrEmpty(config.SeriesEpisodeHashesJson),
                "Episode hashes should be persisted after first sync");

            // Overwrite with sentinel to prove file I/O is skipped on second run
            File.WriteAllText(strmPath, "SENTINEL");

            // Second run: provider bumps lastModified globally → isChangedSeries = true
            // but episode hash matches → skip file I/O
            config.LastSeriesSyncTimestamp = 2000;
            var list2 = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "9999"));
            RegisterSeriesResponses(list2, detail, seriesId: 1);

            Handler.ReceivedUrls.Clear();
            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            // File must NOT be overwritten (sentinel survives)
            Assert.Equal("SENTINEL", File.ReadAllText(strmPath));

            // get_series_info IS still called (we need the data to compute the hash)
            Assert.Contains(Handler.ReceivedUrls, u => u.Contains("get_series_info"));
        }

        // -----------------------------------------------------------------
        // Test 10: EpisodeHashMiss_NewEpisode_FileWritten
        // -----------------------------------------------------------------

        [Fact]
        public async Task EpisodeHashMiss_NewEpisode_FileWritten()
        {
            // First run: write one episode
            var config = DefaultConfig();
            config.SmartSkipExisting = true;

            var list = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "2000"));
            var detail1ep = SeriesDetailJson(seriesId: 1, seasonNum: 1, episodeNum: 1,
                title: "Episode Title", ext: "mp4");
            RegisterSeriesResponses(list, detail1ep, seriesId: 1);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.False(string.IsNullOrEmpty(config.SeriesEpisodeHashesJson));

            // Second run: provider adds a second episode (different hash)
            config.LastSeriesSyncTimestamp = 2000;
            var list2 = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "5000"));
            var detail2ep = System.Text.Json.JsonSerializer.Serialize(new
            {
                info = new { series_id = 1, name = "Test Show", tmdb = "" },
                seasons = new object[0],
                episodes = new System.Collections.Generic.Dictionary<string, object[]>
                {
                    ["1"] = new object[]
                    {
                        new { id = 101, episode_num = 1, title = "Episode Title",
                              container_extension = "mp4", season = 1 },
                        new { id = 102, episode_num = 2, title = "New Episode",
                              container_extension = "mp4", season = 1 }
                    }
                }
            });
            Handler.RespondWith("action=get_series", list2);
            Handler.RespondWith("action=get_series_info&series_id=1", detail2ep);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            // New episode must be written (hash mismatch → file I/O happens)
            var newEpPath = EpisodeStrmPath("Test Show", season: 1, episode: 2, title: "New Episode");
            Assert.True(File.Exists(newEpPath), $"Expected new episode at: {newEpPath}");
            Assert.Equal("http://fake-xtream/series/user/pass/102.mp4", File.ReadAllText(newEpPath));
        }

        // -----------------------------------------------------------------
        // Test 11: NamingVersionUpgrade_ClearsEpisodeHashes
        // -----------------------------------------------------------------

        [Fact]
        public async Task NamingVersionUpgrade_ClearsEpisodeHashes()
        {
            // Pre-populate hashes to simulate a previous sync
            var config = DefaultConfig();
            config.SmartSkipExisting = true;
            config.SeriesEpisodeHashesJson = "{\"1\":\"abc123\"}";
            config.StrmNamingVersion = 0; // stale → triggers upgrade

            var list = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "2000"));
            var detail = SeriesDetailJson(seriesId: 1, seasonNum: 1, episodeNum: 1,
                title: "Episode Title", ext: "mp4");
            RegisterSeriesResponses(list, detail, seriesId: 1);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            // After naming version upgrade, old hashes must have been cleared
            // The sync then writes fresh hashes for the series it processed
            var hashes = StrmSyncService.DeserializeEpisodeHashes(config.SeriesEpisodeHashesJson);
            // Old dummy hash "abc123" must be gone; replaced by real computed hash
            Assert.DoesNotContain("abc123", config.SeriesEpisodeHashesJson);
            Assert.True(hashes.ContainsKey("1"), "Fresh hash should exist for series_id=1");
        }

        // -----------------------------------------------------------------
        // Test 12: MultiSeason_WritesFilesInCorrectSubdirs
        // -----------------------------------------------------------------

        [Fact]
        public async Task MultiSeason_WritesFilesInCorrectSubdirs()
        {
            var config = DefaultConfig();
            var list = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "2000"));

            // Build a detail with episodes in two seasons
            var detail = System.Text.Json.JsonSerializer.Serialize(new
            {
                info = new { series_id = 1, name = "Test Show", tmdb = "" },
                seasons = new object[0],
                episodes = new System.Collections.Generic.Dictionary<string, object[]>
                {
                    ["1"] = new object[]
                    {
                        new { id = 101, episode_num = 1, title = "Pilot",    container_extension = "mp4", season = 1 }
                    },
                    ["2"] = new object[]
                    {
                        new { id = 201, episode_num = 1, title = "Premiere", container_extension = "mp4", season = 2 }
                    }
                }
            });

            Handler.RespondWith("action=get_series", list);
            Handler.RespondWith("action=get_series_info&series_id=1", detail);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var s1e1 = EpisodeStrmPath("Test Show", season: 1, episode: 1, title: "Pilot");
            var s2e1 = EpisodeStrmPath("Test Show", season: 2, episode: 1, title: "Premiere");

            Assert.True(File.Exists(s1e1), $"Expected Season 01 episode at: {s1e1}");
            Assert.True(File.Exists(s2e1), $"Expected Season 02 episode at: {s2e1}");

            Assert.Equal("http://fake-xtream/series/user/pass/101.mp4", File.ReadAllText(s1e1));
            Assert.Equal("http://fake-xtream/series/user/pass/201.mp4", File.ReadAllText(s2e1));
        }

        // -----------------------------------------------------------------
        // Specials (season 0 / episode 0) must not collide with Season 01 / E01
        // -----------------------------------------------------------------

        [Fact]
        public async Task SeasonZeroAndEpisodeZero_WriteToSpecials_NotSeasonOne()
        {
            var config = DefaultConfig();
            var list = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "2000"));

            var detail = System.Text.Json.JsonSerializer.Serialize(new
            {
                info = new { series_id = 1, name = "Test Show", tmdb = "" },
                seasons = new object[0],
                episodes = new System.Collections.Generic.Dictionary<string, object[]>
                {
                    ["0"] = new object[]
                    {
                        new { id = 1, episode_num = 2, title = "Special Two",   container_extension = "mp4", season = 0 },
                        new { id = 2, episode_num = 0, title = "Pilot Special", container_extension = "mp4", season = 0 }
                    },
                    ["1"] = new object[]
                    {
                        new { id = 3, episode_num = 1, title = "Pilot",  container_extension = "mp4", season = 1 },
                        new { id = 4, episode_num = 2, title = "Second", container_extension = "mp4", season = 1 }
                    }
                }
            });

            Handler.RespondWith("action=get_series", list);
            Handler.RespondWith("action=get_series_info&series_id=1", detail);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var special2 = EpisodeStrmPath("Test Show", season: 0, episode: 2, title: "Special Two");
            var special0 = EpisodeStrmPath("Test Show", season: 0, episode: 0, title: "Pilot Special");
            var s1e1 = EpisodeStrmPath("Test Show", season: 1, episode: 1, title: "Pilot");
            var s1e2 = EpisodeStrmPath("Test Show", season: 1, episode: 2, title: "Second");

            Assert.True(File.Exists(special2), $"Expected Season 00 special at: {special2}");
            Assert.True(File.Exists(special0), $"Expected Season 00 E00 special at: {special0}");
            Assert.True(File.Exists(s1e1), $"Expected Season 01 episode at: {s1e1}");
            Assert.True(File.Exists(s1e2), $"Expected Season 01 episode at: {s1e2}");

            // Season 01 holds exactly the two real episodes — no specials dumped alongside them.
            var seasonOneDir = Path.Combine(TempDir.Path, "Shows", "Test Show", "Season 01");
            Assert.Equal(2, Directory.GetFiles(seasonOneDir, "*.strm").Length);
        }

        [Fact]
        public async Task MissingEpisodeSeasonField_FallsBackToEpisodesMapKey()
        {
            var config = DefaultConfig();
            var list = SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "2000"));

            // Provider omits the per-episode "season" field; only the map key carries the season.
            var detail = System.Text.Json.JsonSerializer.Serialize(new
            {
                info = new { series_id = 1, name = "Test Show", tmdb = "" },
                seasons = new object[0],
                episodes = new System.Collections.Generic.Dictionary<string, object[]>
                {
                    ["2"] = new object[]
                    {
                        new { id = 201, episode_num = 5, title = "Late One", container_extension = "mp4" }
                    }
                }
            });

            Handler.RespondWith("action=get_series", list);
            Handler.RespondWith("action=get_series_info&series_id=1", detail);

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            var s2e5 = EpisodeStrmPath("Test Show", season: 2, episode: 5, title: "Late One");
            Assert.True(File.Exists(s2e5), $"Expected Season 02 episode at: {s2e5}");
        }

        [Fact]
        public async Task CustomMode_EmptyMappings_AbortsWithoutHttp()
        {
            var config = DefaultConfig();
            config.SeriesFolderMode = "custom";
            config.SeriesFolderMappings = string.Empty;

            var svc = MakeService();
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.Empty(Handler.ReceivedUrls);
            Assert.False(string.IsNullOrEmpty(svc.SeriesProgress.AbortReason));
            Assert.Equal(0, svc.SeriesProgress.Total);
            Assert.Equal(0, SaveConfigCallCount);
        }

        // -----------------------------------------------------------------
        // Per-item exclusion (issue #57)
        // -----------------------------------------------------------------

        [Fact]
        public async Task ExcludedSeries_NotWritten_OthersUnaffected()
        {
            var config = DefaultConfig();
            config.ExcludedSeriesIds = new[] { 2 };

            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 1, name: "Keep Show", lastModified: "1000"),
                Series(seriesId: 2, name: "Drop Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));
            Handler.RespondWith("action=get_series_info&series_id=2", SeriesDetailJson(seriesId: 2));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Keep Show")));
            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Drop Show")));
        }

        [Fact]
        public async Task ExcludedSeries_ExistingFolderDeleted_WithoutOrphanCleanup()
        {
            var config = DefaultConfig();
            config.CleanupOrphans = false;
            config.ExcludedSeriesIds = new[] { 2 };

            var staleSeasonDir = Path.Combine(TempDir.Path, "Shows", "Drop Show", "Season 01");
            Directory.CreateDirectory(staleSeasonDir);
            File.WriteAllText(Path.Combine(staleSeasonDir, "Drop Show - S01E01.strm"), "http://fake-xtream/series/user/pass/7.mp4");

            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 2, name: "Drop Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=2", SeriesDetailJson(seriesId: 2));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Drop Show")));
        }

        /// <summary>
        /// Series counterpart to the movie re-inclusion test. This path has two skip guards to
        /// clear — the pre-fetch directory index and the episode-hash skip — and both must fall
        /// through on a folder that is no longer on disk.
        /// </summary>
        [Fact]
        public async Task ReIncludedSeries_Recreated_WithSmartSkipAndStaleWatermark()
        {
            var config = DefaultConfig();
            config.SmartSkipExisting = true;

            // Three syncs below and RespondWith is single-shot, so queue one body per call.
            // The detail rule must be registered FIRST: rules match on substring in registration
            // order, and "action=get_series" is a prefix of "action=get_series_info".
            var listJson = SeriesListJson(Series(seriesId: 2, name: "Drop Show", lastModified: "1000"));
            var detailJson = SeriesDetailJson(seriesId: 2);
            Handler.RespondWithSequence("action=get_series_info&series_id=2", new[] { detailJson, detailJson });
            Handler.RespondWithSequence("action=get_series", new[] { listJson, listJson, listJson });

            var showDir = Path.Combine(TempDir.Path, "Shows", "Drop Show");

            // Phase 1: normal sync writes the show and stores its episode hash.
            await MakeService().SyncSeriesAsync(config, None, SaveConfig);
            Assert.True(Directory.Exists(showDir));
            Assert.False(string.IsNullOrEmpty(config.SeriesEpisodeHashesJson));

            // Phase 2: exclude it — folder goes away.
            config.ExcludedSeriesIds = new[] { 2 };
            await MakeService().SyncSeriesAsync(config, None, SaveConfig);
            Assert.False(Directory.Exists(showDir));

            // Phase 3: re-include with the watermark already past it.
            config.ExcludedSeriesIds = new int[0];
            await MakeService().SyncSeriesAsync(config, None, SaveConfig);
            Assert.True(Directory.Exists(showDir));
        }

        [Fact]
        public async Task ExcludedSeries_DoesNotStallDeltaWatermark()
        {
            var config = DefaultConfig();
            config.ExcludedSeriesIds = new[] { 2 };

            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 1, name: "Keep Show", lastModified: "1000"),
                Series(seriesId: 2, name: "Drop Show", lastModified: "5000")));
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));
            Handler.RespondWith("action=get_series_info&series_id=2", SeriesDetailJson(seriesId: 2));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.Equal(5000, config.LastSeriesSyncTimestamp);
        }

        // -----------------------------------------------------------------
        // Review gate — RequireReviewBeforeSync, series side (ADR-017)
        // -----------------------------------------------------------------

        [Fact]
        public async Task ReviewGate_Series_Off_ByDefault_EverythingSyncs()
        {
            var config = DefaultConfig();
            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 1, name: "New Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "New Show")));
        }

        [Fact]
        public async Task ReviewGate_Series_UnreviewedAndNotOnDisk_HeldButNotExcluded()
        {
            var config = DefaultConfig();
            config.RequireReviewBeforeSync = true;
            config.ReviewedSeriesIdsJson = "[1]";

            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 1, name: "Reviewed Show", lastModified: "1000"),
                Series(seriesId: 2, name: "New Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));

            var svc = MakeService();
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Reviewed Show")));
            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "New Show")));
            Assert.Empty(config.ExcludedSeriesIds);
            Assert.Equal(0, svc.SeriesProgress.Failed);
            // Not registering id=2's detail is the assertion that it was never fetched: the gate
            // sits before the detail call, which is the expensive one and the one that trips
            // Dispatcharr's episode refresh.
            Assert.DoesNotContain(Handler.ReceivedUrls, u => u.Contains("get_series_info&series_id=2"));
        }

        [Fact]
        public async Task ReviewGate_Series_UnreviewedButFolderOnDisk_SyncedAndMarkedReviewed()
        {
            var config = DefaultConfig();
            config.RequireReviewBeforeSync = true;
            config.ReviewedSeriesIdsJson = "[]";

            Directory.CreateDirectory(Path.Combine(TempDir.Path, "Shows", "Established Show"));

            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 7, name: "Established Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=7", SeriesDetailJson(seriesId: 7));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(EpisodeStrmPath("Established Show", season: 1, episode: 1)));
            Assert.Contains("7", config.ReviewedSeriesIdsJson);
        }

        /// <summary>
        /// The series-specific marker. Series carry no TMDB ID on the list payload, so a stored
        /// episode hash — keyed on SeriesId — is the second piece of evidence that a show was
        /// synced before. It survives the provider renaming the show, which folder-name matching
        /// cannot.
        /// </summary>
        [Fact]
        public async Task ReviewGate_Series_UnreviewedButHasStoredEpisodeHash_SyncedAndMarkedReviewed()
        {
            var config = DefaultConfig();
            config.RequireReviewBeforeSync = true;
            config.ReviewedSeriesIdsJson = "[]";
            config.SeriesEpisodeHashesJson = "{\"9\":\"deadbeef\"}";

            // Renamed by the provider, so nothing on disk matches the new name.
            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 9, name: "Renamed Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=9", SeriesDetailJson(seriesId: 9));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(EpisodeStrmPath("Renamed Show", season: 1, episode: 1)));
            Assert.Contains("9", config.ReviewedSeriesIdsJson);
        }

        /// <summary>
        /// A held show must still advance the delta high-water mark. The series watermark is
        /// accumulated inside the per-series loop (unlike movies, where it is computed over the
        /// unfiltered catalogue afterwards), so gating before that update would freeze the
        /// watermark behind whatever is waiting for review.
        /// </summary>
        [Fact]
        public async Task ReviewGate_Series_HeldShow_StillAdvancesTheDeltaWatermark()
        {
            var config = DefaultConfig();
            config.RequireReviewBeforeSync = true;
            config.ReviewedSeriesIdsJson = "[1]";

            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 1, name: "Reviewed Show", lastModified: "1000"),
                Series(seriesId: 2, name: "Held Show", lastModified: "5000")));
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Held Show")));
            Assert.Equal(5000, config.LastSeriesSyncTimestamp);
        }

        [Fact]
        public async Task ReviewGate_Series_UnparseableReviewedStore_StandsDownRatherThanHoldingEverything()
        {
            var config = DefaultConfig();
            config.RequireReviewBeforeSync = true;
            config.ReviewedSeriesIdsJson = "[1,2";

            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 1, name: "Show One", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Show One")));
        }

        // -----------------------------------------------------------------
        // Collapse-group exclusion propagation — the Path-A fix (ADR-016)
        // -----------------------------------------------------------------

        [Fact]
        public async Task ExcludedSeries_CrossListedCopyUnderNewId_AlsoExcluded()
        {
            // The Path-A quirk. Exclusions are stored per SeriesId, so a copy of an excluded
            // show arriving under a fresh SeriesId — a category enabled after the exclusion was
            // made — used to sync until the user opened the de-dup view and saved. Exclusion now
            // propagates across the collapse group, so id=9 goes too even though only id=2 is on
            // the blocklist. Its lastModified is deliberately the highest in the list: a
            // group-excluded copy must still fold into the delta watermark, or the watermark
            // stalls behind it.
            var config = DefaultConfig();
            config.ExcludedSeriesIds = new[] { 2 };

            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 1, name: "Keep Show", lastModified: "1000"),
                Series(seriesId: 2, name: "Drop Show", lastModified: "1000"),
                Series(seriesId: 9, name: "Drop Show", lastModified: "5000")));
            // Only the kept title's detail is registered: if id=9 is processed its fetch throws
            // (unregistered URL), is caught, and counts as Failed — asserted 0 below.
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));

            var svc = MakeService();
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Keep Show")));
            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Drop Show")));
            Assert.Equal(0, svc.SeriesProgress.Failed);
            Assert.DoesNotContain(Handler.ReceivedUrls, u => u.Contains("get_series_info&series_id=9"));
            Assert.Equal(5000, config.LastSeriesSyncTimestamp);
        }

        [Fact]
        public async Task ExcludedSeries_DifferentName_NotPropagated()
        {
            // Guard against over-reach, and the flip side of the safety argument: propagation
            // covers exactly the collapse group, so a title the collapse would NOT merge is
            // untouched. This is also the known near-duplicate limitation in test form — a
            // provider-prefixed or differently-spelled copy still needs excluding by hand.
            var config = DefaultConfig();
            config.ExcludedSeriesIds = new[] { 2 };

            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 2, name: "Drop Show", lastModified: "1000"),
                Series(seriesId: 9, name: "Drop Show 4K", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=9", SeriesDetailJson(seriesId: 9));

            var svc = MakeService();
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Drop Show")));
            Assert.True(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Drop Show 4K")));
            Assert.Equal(0, svc.SeriesProgress.Failed);
        }

        [Fact]
        public async Task ExcludedSeries_GroupPropagation_ReIncludeTakesEffect()
        {
            // Propagation is derived from the blocklist on every run and persists nothing of its
            // own, so emptying the blocklist re-includes the whole group on the next sync — no
            // second field to clear, no migration, no stale state to strand a re-inclusion.
            var config = DefaultConfig();
            config.ExcludedSeriesIds = new[] { 2 };

            // Two syncs, so the detail rule must be registered before the multi-shot list
            // sequence — "action=get_series" is a substring of "action=get_series_info".
            var listJson = SeriesListJson(
                Series(seriesId: 2, name: "Drop Show", lastModified: "1000"),
                Series(seriesId: 9, name: "Drop Show", lastModified: "1000"));
            Handler.RespondWith("action=get_series_info&series_id=2", SeriesDetailJson(seriesId: 2));
            Handler.RespondWithSequence("action=get_series", new[] { listJson, listJson });

            var dropDir = Path.Combine(TempDir.Path, "Shows", "Drop Show");

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);
            Assert.False(Directory.Exists(dropDir));

            // Re-included: the group collapses to one representative (id=2, the lowest with no
            // stored hash) and writes once.
            config.ExcludedSeriesIds = new int[0];
            var svc = MakeService();
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(Directory.Exists(dropDir));
            Assert.Equal(0, svc.SeriesProgress.Failed);
        }

        // -----------------------------------------------------------------
        // Test 18: Collapse_CrossListedSameName_KeepsOneRepresentative
        // -----------------------------------------------------------------

        [Fact]
        public async Task Collapse_CrossListedSameName_KeepsOneRepresentative()
        {
            // A provider cross-lists the "same" series under two SeriesIds (e.g. one per
            // category). In single-folder mode both land in Shows/{name}, so the sync must
            // collapse them by (folder + cleaned name) and process only the first
            // representative — no duplicate per-episode files, no redundant get_series_info.
            var config = DefaultConfig();
            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 1, name: "Dup Show", lastModified: "1000"),
                Series(seriesId: 2, name: "Dup Show", lastModified: "1000")));
            // Only the kept representative (id=1) is fetched. id=2's detail is deliberately
            // NOT registered: if collapse regresses and id=2 is processed, the loop's fetch
            // throws (unregistered URL), is caught, and counts as Failed — asserted 0 below.
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));

            var svc = MakeService();
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            var strmPath = EpisodeStrmPath("Dup Show", season: 1, episode: 1, title: "Episode Title");
            Assert.True(File.Exists(strmPath), $"Expected STRM at: {strmPath}");
            Assert.Equal(0, svc.SeriesProgress.Failed);
            Assert.Contains(Handler.ReceivedUrls, u => u.Contains("get_series_info&series_id=1"));
            Assert.DoesNotContain(Handler.ReceivedUrls, u => u.Contains("get_series_info&series_id=2"));
        }

        // -----------------------------------------------------------------
        // Test 19: Collapse_DifferentNames_BothProcessed
        // -----------------------------------------------------------------

        [Fact]
        public async Task Collapse_DifferentNames_BothProcessed()
        {
            // Guard against over-collapsing: two genuinely different titles must both be
            // processed (the collapse key includes the cleaned name, not just the folder).
            var config = DefaultConfig();
            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 1, name: "Show One", lastModified: "1000"),
                Series(seriesId: 2, name: "Show Two", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));
            Handler.RespondWith("action=get_series_info&series_id=2", SeriesDetailJson(seriesId: 2));

            var svc = MakeService();
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(EpisodeStrmPath("Show One", season: 1, episode: 1, title: "Episode Title")));
            Assert.True(File.Exists(EpisodeStrmPath("Show Two", season: 1, episode: 1, title: "Episode Title")));
            Assert.Equal(0, svc.SeriesProgress.Failed);
        }

        // -----------------------------------------------------------------
        // Deterministic collapse representative
        // -----------------------------------------------------------------

        [Fact]
        public async Task Collapse_PicksLowestSeriesId_WhateverTheListOrder()
        {
            // The representative used to be whichever copy the provider happened to list
            // first, and the episode hash is keyed on SeriesId — so a flip left the new id
            // with no stored hash, delta-unchanged, pre-fetch-skipping, carrying nothing, and
            // stranded in the no-hash state. The list is deliberately in descending id order:
            // the lowest id must win regardless.
            var config = DefaultConfig();
            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 9, name: "Dup Show", lastModified: "1000"),
                Series(seriesId: 4, name: "Dup Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=4", SeriesDetailJson(seriesId: 4));

            var svc = MakeService();
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(EpisodeStrmPath("Dup Show", season: 1, episode: 1, title: "Episode Title")));
            Assert.Equal(0, svc.SeriesProgress.Failed);
            Assert.Contains(Handler.ReceivedUrls, u => u.Contains("get_series_info&series_id=4"));
            Assert.DoesNotContain(Handler.ReceivedUrls, u => u.Contains("get_series_info&series_id=9"));
        }

        [Fact]
        public async Task Collapse_PrefersRepresentativeThatAlreadyHasAnEpisodeHash()
        {
            // Lowest-id alone would still flip an established representative the first time a
            // lower id shows up (a newly enabled category, another provider). A stored episode
            // hash outranks the id tie-break, so id=9 keeps its place over id=4.
            var config = DefaultConfig();
            config.SeriesEpisodeHashesJson = "{\"9\":\"deadbeef\"}";
            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 4, name: "Dup Show", lastModified: "1000"),
                Series(seriesId: 9, name: "Dup Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=9", SeriesDetailJson(seriesId: 9));

            var svc = MakeService();
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.True(File.Exists(EpisodeStrmPath("Dup Show", season: 1, episode: 1, title: "Episode Title")));
            Assert.Equal(0, svc.SeriesProgress.Failed);
            Assert.Contains(Handler.ReceivedUrls, u => u.Contains("get_series_info&series_id=9"));
            Assert.DoesNotContain(Handler.ReceivedUrls, u => u.Contains("get_series_info&series_id=4"));
        }

        // -----------------------------------------------------------------
        // Test 20: EmptyDetailUnderLoad_RetryRecovers
        // -----------------------------------------------------------------

        [Fact]
        public async Task EmptyDetailUnderLoad_RetryRecovers()
        {
            // get_series_info can answer HTTP 200 with an empty episode list under concurrent
            // load. FetchSeriesDetailAsync retries; an empty-then-valid sequence must recover
            // the episode within a single sync (no ratchet where re-included titles trickle in).
            var config = DefaultConfig();
            var emptyDetail = System.Text.Json.JsonSerializer.Serialize(new
            {
                info = new { series_id = 1, name = "Test Show", tmdb = "" },
                seasons = new object[0],
                episodes = new System.Collections.Generic.Dictionary<string, object[]>()
            });
            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 1, name: "Test Show", lastModified: "1000")));
            Handler.RespondWithSequence("action=get_series_info&series_id=1",
                new[] { emptyDetail, SeriesDetailJson(seriesId: 1) });

            var svc = MakeService();
            svc.SeriesDetailRetryBaseDelayMs = 0; // no real delay in tests
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            var strmPath = EpisodeStrmPath("Test Show", season: 1, episode: 1, title: "Episode Title");
            Assert.True(File.Exists(strmPath), $"Expected STRM at: {strmPath}");
            Assert.Equal(0, svc.SeriesProgress.Failed);
            var detailCalls = Handler.ReceivedUrls.FindAll(u => u.Contains("get_series_info&series_id=1")).Count;
            Assert.Equal(2, detailCalls);
        }

        // -----------------------------------------------------------------
        // Test 21: RefreshDispatcharrEpisodes_PokesCollapsedSiblings
        // -----------------------------------------------------------------

        [Fact]
        public async Task RefreshDispatcharrEpisodes_PokesCollapsedSiblings()
        {
            // With the toggle on, the collapsed-away sibling (id=2) — a distinct Dispatcharr
            // relation for the same show — must still get a get_series_info call so Dispatcharr
            // refreshes its episode streams, even though only id=1 is written to disk.
            var config = DefaultConfig();
            config.RefreshDispatcharrEpisodes = true;
            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 1, name: "Dup Show", lastModified: "1000"),
                Series(seriesId: 2, name: "Dup Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));
            Handler.RespondWith("action=get_series_info&series_id=2", SeriesDetailJson(seriesId: 2));

            var svc = MakeService();
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            // One STRM written (collapse unchanged), and both relations poked.
            Assert.True(File.Exists(EpisodeStrmPath("Dup Show", season: 1, episode: 1, title: "Episode Title")));
            Assert.Equal(0, svc.SeriesProgress.Failed);
            Assert.Contains(Handler.ReceivedUrls, u => u.Contains("get_series_info&series_id=1"));
            Assert.Contains(Handler.ReceivedUrls, u => u.Contains("get_series_info&series_id=2"));
        }

        // -----------------------------------------------------------------
        // Test 22: RefreshDispatcharrEpisodes_Off_DoesNotPokeSiblings
        // -----------------------------------------------------------------

        [Fact]
        public async Task RefreshDispatcharrEpisodes_Off_DoesNotPokeSiblings()
        {
            // Toggle off (the default) → the sibling is never poked; behaviour is exactly the
            // pre-feature collapse. id=2's detail is deliberately not registered.
            var config = DefaultConfig();
            config.RefreshDispatcharrEpisodes = false;
            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 1, name: "Dup Show", lastModified: "1000"),
                Series(seriesId: 2, name: "Dup Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));

            var svc = MakeService();
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.Equal(0, svc.SeriesProgress.Failed);
            Assert.DoesNotContain(Handler.ReceivedUrls, u => u.Contains("get_series_info&series_id=2"));
        }

        // -----------------------------------------------------------------
        // Test 23: RefreshDispatcharrEpisodes_ThrottledByLog_SkipsRecent
        // -----------------------------------------------------------------

        [Fact]
        public async Task RefreshDispatcharrEpisodes_ThrottledByLog_SkipsRecent()
        {
            // A sibling poked within the throttle window must be skipped — no wasted call that
            // Dispatcharr would only answer from cache.
            var config = DefaultConfig();
            config.RefreshDispatcharrEpisodes = true;
            config.DispatcharrEpisodeRefreshLogJson =
                System.Text.Json.JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, long>
                {
                    ["2"] = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 1, name: "Dup Show", lastModified: "1000"),
                Series(seriesId: 2, name: "Dup Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));

            var svc = MakeService();
            svc.DispatcharrRefreshThrottleSeconds = long.MaxValue; // never re-poke within the test
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.Equal(0, svc.SeriesProgress.Failed);
            Assert.DoesNotContain(Handler.ReceivedUrls, u => u.Contains("get_series_info&series_id=2"));
        }

        // -----------------------------------------------------------------
        // Test 24: RefreshDispatcharrEpisodes_ThrottleLog_KeepsFreshEntriesOutOfScope
        // -----------------------------------------------------------------

        [Fact]
        public async Task RefreshDispatcharrEpisodes_ThrottleLog_KeepsFreshEntriesOutOfScope()
        {
            // The throttle log must survive a sync that doesn't see a given relation: a fresh
            // entry for id=999 (not a sibling this sync) must remain after the run, so throttle
            // memory isn't wiped by syncs whose scope is shaped by delta/exclusions.
            var config = DefaultConfig();
            config.RefreshDispatcharrEpisodes = true;
            config.DispatcharrEpisodeRefreshLogJson =
                System.Text.Json.JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, long>
                {
                    ["999"] = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
            Handler.RespondWith("action=get_series", SeriesListJson(
                Series(seriesId: 1, name: "Dup Show", lastModified: "1000"),
                Series(seriesId: 2, name: "Dup Show", lastModified: "1000")));
            Handler.RespondWith("action=get_series_info&series_id=1", SeriesDetailJson(seriesId: 1));
            Handler.RespondWith("action=get_series_info&series_id=2", SeriesDetailJson(seriesId: 2));

            var svc = MakeService();
            await svc.SyncSeriesAsync(config, None, SaveConfig);

            Assert.Equal(0, svc.SeriesProgress.Failed);
            // Unrelated fresh entry preserved, and the just-poked sibling recorded.
            Assert.Contains("\"999\"", config.DispatcharrEpisodeRefreshLogJson);
            Assert.Contains("\"2\"", config.DispatcharrEpisodeRefreshLogJson);
        }
    }
}
