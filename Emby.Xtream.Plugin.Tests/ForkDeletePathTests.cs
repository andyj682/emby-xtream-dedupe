using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Service;
using Emby.Xtream.Plugin.Tests.Fakes;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// The fork's own delete paths, pinned tightly enough for the mutation gate.
    ///
    /// Since the upstream merge through 91f27d3 these methods live in StrmSyncService.Cleanup.cs,
    /// which CI mutation-tests with a 75% break threshold. The first Stryker run scored 68.98%,
    /// and the shortfall was this fork's code: tests reached these methods but never in a state
    /// that could tell a correct prune from a broken one.
    ///
    /// Two shapes recur below, and both are worth having for their own sake, not just the score:
    ///   * A foreign file beside the plugin's own. Each prune matches on a name pattern, and on
    ///     Linux an empty pattern matches everything — so a fixture containing only the plugin's
    ///     own files cannot tell "matched the pattern" from "matched every file". These assert
    ///     the prune leaves alone what it did not write. That is exactly how a PRE-...-KEEP
    ///     snapshot survived the rolling delete during a real recovery.
    ///   * Log assertions on the fork's summary lines. The logging call itself is excluded from
    ///     mutation; the condition guarding it is not, and only the log can observe it.
    /// </summary>
    public class ForkDeletePathTests : SyncTestBase
    {
        private string RecordsRoot() => Path.Combine(TempDir.Path, "records");

        private string SnapshotDir() => Path.Combine(RecordsRoot(), StrmSyncService.SnapshotsFolderName);

        private string SeedSnapshot(string fileName)
        {
            Directory.CreateDirectory(SnapshotDir());
            var path = Path.Combine(SnapshotDir(), fileName);
            File.WriteAllText(path, StrmSyncService.SnapshotHeader + "\n");
            return path;
        }

        private string MovieStrmPath(string movieName)
            => Path.Combine(TempDir.Path, "Movies", movieName, movieName + ".strm");

        private string SeedMovieStrm(string movieName)
        {
            var path = MovieStrmPath(movieName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "http://fake-xtream/movie/user/pass/9999.mkv");
            return path;
        }

        private string SeedEpisode(string show, string season, string fileName)
        {
            var dir = Path.Combine(TempDir.Path, "Shows", show, season);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, fileName);
            File.WriteAllText(path, "http://fake-xtream/series/user/pass/101.mp4");
            return path;
        }

        private string RecordDir()
        {
            var dir = Path.Combine(TempDir.Path, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string[] DeletionRecords(string dir) => Directory.GetFiles(dir, "xtream-deleted-*.txt");

        private void RegisterVodStreams(string json) => Handler.RespondWith("get_vod_streams", json);

        // -----------------------------------------------------------------
        // Catalogue snapshot pruning (ADR-F005 mechanism 6)
        // -----------------------------------------------------------------

        [Fact]
        public async Task SnapshotPrune_KeepsTheNewestN_AndNeverTouchesAFileItDidNotName()
        {
            var config = DefaultConfig();
            config.RecordsPath = RecordsRoot();
            config.CatalogueSnapshotCount = 3;

            var oldest = SeedSnapshot("catalogue-ids-2020-01-01.tsv");
            var older = SeedSnapshot("catalogue-ids-2020-01-02.tsv");
            var keptA = SeedSnapshot("catalogue-ids-2020-01-03.tsv");
            var keptB = SeedSnapshot("catalogue-ids-2020-01-04.tsv");
            // A pre-event snapshot renamed so the rolling prune cannot match it — the escape the
            // recovery procedure depends on. It sorts before every dated name, so a prune that
            // matched everything would take it first.
            var preserved = SeedSnapshot("PRE-EVENT-KEEP-catalogue-ids-2019-12-31.tsv");

            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "A Movie", added: 1000)));
            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            // Today's snapshot plus the two newest seeded ones; the two oldest are gone.
            var dated = Directory.GetFiles(SnapshotDir(), "catalogue-ids-*.tsv");
            Assert.Equal(3, dated.Length);
            Assert.False(File.Exists(oldest));
            Assert.False(File.Exists(older));
            Assert.True(File.Exists(keptA));
            Assert.True(File.Exists(keptB));
            Assert.Single(dated, f => !Path.GetFileName(f).StartsWith("catalogue-ids-2020-", StringComparison.Ordinal));

            Assert.True(File.Exists(preserved),
                "A snapshot renamed out of the dated pattern must never be pruned");
        }

        [Fact]
        public async Task SnapshotPrune_AtExactlyTheLimit_DeletesNothing()
        {
            var config = DefaultConfig();
            config.RecordsPath = RecordsRoot();
            config.CatalogueSnapshotCount = 3;

            var a = SeedSnapshot("catalogue-ids-2020-01-01.tsv");
            var b = SeedSnapshot("catalogue-ids-2020-01-02.tsv");

            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "A Movie", added: 1000)));
            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(3, Directory.GetFiles(SnapshotDir(), "catalogue-ids-*.tsv").Length);
            Assert.True(File.Exists(a));
            Assert.True(File.Exists(b));
        }

        // -----------------------------------------------------------------
        // Configuration copy pruning (ADR-F005 mechanisms 3 and 5)
        // -----------------------------------------------------------------

        [Fact]
        public void BackupPrune_LeavesAFileThatIsNotACopy()
        {
            var config = DefaultConfig();
            config.RecordsPath = RecordsRoot();
            config.ConfigBackupCount = 2;

            var cfgPath = Path.Combine(TempDir.Path, "cfg", "Emby.Xtream.Plugin.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(cfgPath));
            var backupDir = Path.Combine(RecordsRoot(), StrmSyncService.ConfigBackupFolderName);
            Directory.CreateDirectory(backupDir);
            // Sorts before any copy's name, so a prune that matched every file would delete it first.
            var foreign = Path.Combine(backupDir, "000-readme.txt");
            File.WriteAllText(foreign, "not a backup");

            var svc = new StrmSyncService(new RecordingLogger(), HttpClient) { ConfigRollbackSourcePath = cfgPath };
            for (var i = 0; i < 3; i++)
            {
                // Content must differ each time or the unchanged-skip suppresses the copy.
                File.WriteAllText(cfgPath, "<PluginConfiguration><A>" + i + "</A></PluginConfiguration>");
                svc.BackupConfiguration(config);
            }

            Assert.Equal(2, Directory.GetFiles(backupDir, "*.xml").Length);
            Assert.True(File.Exists(foreign), "Pruning backups must only ever delete backups");
        }

        // -----------------------------------------------------------------
        // Deleted-path records (ADR-F005 mechanism 2)
        // -----------------------------------------------------------------

        [Fact]
        public async Task DeletionRecordPrune_LeavesAFileThatIsNotARecord()
        {
            var dir = RecordDir();
            for (var i = 1; i <= 12; i++)
            {
                File.WriteAllText(
                    Path.Combine(dir, string.Format("xtream-deleted-202501{0:D2}-000000-Movies.txt", i)),
                    "seed");
            }
            // In Emby's log directory in production, beside files that are nothing to do with us.
            var foreign = Path.Combine(dir, "000-embyserver.txt");
            File.WriteAllText(foreign, "not a record");

            var config = DefaultConfig();
            config.CleanupOrphans = true;
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));
            for (var i = 0; i < 20; i++)
            {
                SeedMovieStrm(string.Format("Gone Movie {0:D2}", i));
            }

            var svc = new StrmSyncService(new RecordingLogger(), HttpClient) { DeletionRecordDirectory = dir };
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(10, DeletionRecords(dir).Length);
            Assert.True(File.Exists(foreign), "Pruning records must only ever delete records");
        }

        [Fact]
        public async Task DeletionRecord_NotWrittenWhenTheSampleAlreadyHoldsEveryPath()
        {
            // The full record exists for the deletions the logged sample cannot show. At exactly
            // the sample size the log line already names every file, so a record would be noise.
            var dir = RecordDir();
            var config = DefaultConfig();
            config.CleanupOrphans = true;
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Kept Movie", added: 1000)));
            for (var i = 0; i < 15; i++)
            {
                SeedMovieStrm(string.Format("Gone Movie {0:D2}", i));
            }

            var svc = new StrmSyncService(new RecordingLogger(), HttpClient) { DeletionRecordDirectory = dir };
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(15, svc.MovieProgress.Deleted);
            Assert.Empty(DeletionRecords(dir));
        }

        // -----------------------------------------------------------------
        // Kept-folder summary (ADR-F007)
        // -----------------------------------------------------------------

        [Fact]
        public async Task KeptFolderSummary_LoggedWhenAnExclusionMatchedAFolderJustWritten()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Twin Title", added: 1000),
                VodStream(streamId: 2, name: "twin title", added: 1000)));

            var logger = new RecordingLogger();
            await new StrmSyncService(logger, HttpClient).SyncMoviesAsync(config, None, SaveConfig);

            Assert.Contains(logger.Infos, i => i.StartsWith("Kept 1 folder(s) under Movies", StringComparison.Ordinal));
        }

        [Fact]
        public async Task KeptFolderSummary_NotLoggedWhenNothingWasKept()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 2 };
            var dropped = SeedMovieStrm("Drop Me");
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Keep Me", added: 1000),
                VodStream(streamId: 2, name: "Drop Me", added: 1000)));

            var logger = new RecordingLogger();
            await new StrmSyncService(logger, HttpClient).SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(File.Exists(dropped), "the exclusion must actually have run, or this asserts nothing");
            Assert.DoesNotContain(logger.Infos, i => i.Contains("folder(s) under Movies that an excluded item matched"));
        }

        // -----------------------------------------------------------------
        // Episode filename migration summary
        // -----------------------------------------------------------------

        [Fact]
        public void MigrationSummary_ReportsARename()
        {
            var config = DefaultConfig();
            config.EpisodeFilenameMigrationVersion = 0;
            SeedEpisode("Test Show", "Season 01", "Test Show - S01E01 - Some Title.strm");

            var logger = new RecordingLogger();
            new StrmSyncService(logger, HttpClient).MigrateEpisodeFilenames(config, () => { });

            Assert.Contains(logger.Infos, i => i.Contains("renamed 1 file(s) to the title-free form, removed 0 duplicate(s)"));
        }

        [Fact]
        public void MigrationSummary_ReportsACollapsedDuplicate()
        {
            var config = DefaultConfig();
            config.EpisodeFilenameMigrationVersion = 0;
            SeedEpisode("Test Show", "Season 01", "Test Show - S01E01 - Some Title.strm");
            SeedEpisode("Test Show", "Season 01", "Test Show - S01E01.strm");

            var logger = new RecordingLogger();
            new StrmSyncService(logger, HttpClient).MigrateEpisodeFilenames(config, () => { });

            Assert.Contains(logger.Infos, i => i.Contains("renamed 0 file(s) to the title-free form, removed 1 duplicate(s)"));
        }

        [Fact]
        public void MigrationSummary_SilentWhenThereWasNothingToMigrate()
        {
            var config = DefaultConfig();
            config.EpisodeFilenameMigrationVersion = 0;
            SeedEpisode("Test Show", "Season 01", "Test Show - S01E01.strm");

            var logger = new RecordingLogger();
            new StrmSyncService(logger, HttpClient).MigrateEpisodeFilenames(config, () => { });

            // The migration still ran — it is the version stamp that proves it reached the summary.
            Assert.Equal(StrmSyncService.CurrentEpisodeFilenameVersion, config.EpisodeFilenameMigrationVersion);
            Assert.DoesNotContain(logger.Infos, i => i.StartsWith("Episode filename migration:", StringComparison.Ordinal));
        }
    }
}
