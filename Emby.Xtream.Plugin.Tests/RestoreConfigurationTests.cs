using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Restoring a saved configuration (ADR-F005 mechanism 9).
    ///
    /// This is the first thing that lets the plugin overwrite its own configuration from data the
    /// user supplied, so the tests here are weighted towards the refusals rather than the happy
    /// path. Applying the configuration is injected, because there is no plugin instance under
    /// test and the rules would otherwise all stop at "not initialized".
    /// </summary>
    public class RestoreConfigurationTests : SyncTestBase
    {
        /// <summary>
        /// Builds a service whose records root and rollback folder both sit under the temp
        /// directory, and returns the two directories a candidate may come from.
        /// </summary>
        private StrmSyncService MakeRestoreService(
            PluginConfiguration config, out string backupDir, out string rollbackDir)
        {
            var service = MakeService();

            // The rollback folder is derived from the configuration file's own location, which is
            // normally read from the plugin. Pointing this at a file inside the temp directory is
            // what the production code does with Plugin.ConfigPath.
            var configFile = Path.Combine(TempDir.Path, "Emby.Xtream.Plugin.xml");
            File.WriteAllText(configFile, "<PluginConfiguration />");
            service.ConfigRollbackSourcePath = configFile;

            config.RecordsPath = Path.Combine(TempDir.Path, "records");
            _liveRecordsPath = config.RecordsPath;

            backupDir = Path.Combine(config.RecordsPath, StrmSyncService.ConfigBackupFolderName);
            rollbackDir = Path.Combine(TempDir.Path, StrmSyncService.RollbackFolderName);
            Directory.CreateDirectory(backupDir);
            Directory.CreateDirectory(rollbackDir);

            return service;
        }

        /// <summary>The records path the live configuration is using, set by MakeRestoreService.</summary>
        private string _liveRecordsPath;

        /// <summary>
        /// Writes a saved configuration. <paramref name="recordsPath"/> defaults to the live one,
        /// which is what a real copy carries — it is a copy of that same configuration. Pass a
        /// different value to exercise a restore that relocates the records root.
        /// </summary>
        private string WriteCopy(
            string directory,
            string stamp,
            int[] excludedVod = null,
            string reviewedVodJson = null,
            string recordsPath = null)
        {
            var config = new PluginConfiguration
            {
                ExcludedVodStreamIds = excludedVod ?? new[] { 1, 2, 3 },
                ExcludedSeriesIds = new[] { 9 },
                ReviewedVodStreamIdsJson = reviewedVodJson ?? "[10,11]",
                ReviewedSeriesIdsJson = "[20]",
                RecordsPath = recordsPath ?? _liveRecordsPath ?? string.Empty,
            };

            var path = Path.Combine(directory, stamp + ".xml");
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
            using (var stream = File.Create(path))
            {
                serializer.Serialize(stream, config);
            }

            return path;
        }

        // ---- Listing ----

        [Fact]
        public void ListsBothBackupsAndRollbacks_LabeledBySource()
        {
            var config = DefaultConfig();
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);

            WriteCopy(backupDir, "20260913-030000-000");
            WriteCopy(rollbackDir, "20260915-155800-000");

            var copies = service.ListConfigurationCopies(config).Copies;

            Assert.Equal(2, copies.Count);
            Assert.Contains(copies, c => c.Source == "backup");
            Assert.Contains(copies, c => c.Source == "rollback");
        }

        /// <summary>
        /// Newest first, because at recovery time the copy wanted is almost always a recent one,
        /// and a rollback taken minutes ago must not be buried under older scheduled backups.
        /// </summary>
        [Fact]
        public void ListsNewestFirst_AcrossBothSources()
        {
            var config = DefaultConfig();
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);

            WriteCopy(backupDir, "20260913-030000-000");
            WriteCopy(rollbackDir, "20260915-155800-000");
            WriteCopy(backupDir, "20260914-030000-000");

            var copies = service.ListConfigurationCopies(config).Copies;

            Assert.Equal("2026-09-15 15:58:00", copies[0].Taken);
            Assert.Equal("2026-09-14 03:00:00", copies[1].Taken);
            Assert.Equal("2026-09-13 03:00:00", copies[2].Taken);
        }

        [Fact]
        public void ReportsTheFourStoreSizes()
        {
            var config = DefaultConfig();
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);

            WriteCopy(backupDir, "20260913-030000-000", excludedVod: new[] { 1, 2, 3, 4 });

            var copy = service.ListConfigurationCopies(config).Copies.Single();

            Assert.Equal("4", copy.ExcludedVodStreamIds);
            Assert.Equal("1", copy.ExcludedSeriesIds);
            Assert.Equal("2", copy.ReviewedVodStreamIdsJson);
            Assert.Equal("1", copy.ReviewedSeriesIdsJson);
            Assert.True(copy.Restorable);
        }

        [Fact]
        public void ListIsEmptyWhenNothingHasBeenSaved()
        {
            var config = DefaultConfig();
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);

            Assert.Empty(service.ListConfigurationCopies(config).Copies);
        }

        /// <summary>
        /// The current configuration travels with the list. Four store counts cannot be judged
        /// without the baseline they are being compared against.
        /// </summary>
        [Fact]
        public void TheListCarriesTheConfigurationInForce()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 1, 2, 3, 4, 5, 6 };
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);

            WriteCopy(backupDir, "20260913-030000-000");

            var current = service.ListConfigurationCopies(config).Current;

            Assert.Equal("6", current.ExcludedVodStreamIds);
            Assert.Equal("current", current.Source);
        }

        // ---- What restoring would change ----

        /// <summary>
        /// On a settled install every copy carries identical store counts, which leaves the
        /// timestamp as the only discriminator — and a timestamp does not say whether restoring
        /// would change anything. This is what answers that.
        /// </summary>
        [Fact]
        public void ACopyMatchingTheCurrentConfigurationReportsNoChange()
        {
            var config = DefaultConfig();
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);

            // Save the live configuration as the copy, so the two genuinely match.
            var path = Path.Combine(backupDir, "20260913-030000-000.xml");
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
            using (var stream = File.Create(path))
            {
                serializer.Serialize(stream, config);
            }

            var copy = service.ListConfigurationCopies(config).Copies.Single();

            Assert.True(copy.IdenticalToCurrent);
            Assert.Equal(0, copy.DifferingSettings);
            Assert.False(copy.DecisionStoresDiffer);
            Assert.Equal("nothing", copy.ChangeSummary);
        }

        /// <summary>
        /// Labeled deltas rather than four bare store sizes: nobody can read "61697 / 18901 / 4343
        /// / 3144" without a key, and on a settled install every copy carries the same four. The
        /// magnitude still has to survive, because after a wipe it is what identifies the good copy.
        /// </summary>
        [Fact]
        public void ChangeSummaryNamesEachStoreAndItsDirection()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new int[0];
            config.ReviewedVodStreamIdsJson = "[1,2,3]";
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);

            var saved = DefaultConfig();
            saved.RecordsPath = config.RecordsPath;
            saved.ExcludedVodStreamIds = Enumerable.Range(1, 8554).ToArray();
            saved.ReviewedVodStreamIdsJson = "[1]";

            var path = Path.Combine(backupDir, "20260913-030000-000.xml");
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
            using (var stream = File.Create(path))
            {
                serializer.Serialize(stream, saved);
            }

            var copy = service.ListConfigurationCopies(config).Copies.Single();

            // Recovering a wiped exclusion list is the case this feature exists for, so the
            // number has to be visible and its direction unambiguous.
            Assert.Contains("+8,554 movie exclusions", copy.ChangeSummary);
            Assert.Contains("-2 movies reviewed", copy.ChangeSummary);
            // Stores that did not move say nothing, rather than reporting a zero.
            Assert.DoesNotContain("series exclusions", copy.ChangeSummary);
        }

        [Fact]
        public void ACopyWithADifferentSettingCountsIt()
        {
            var config = DefaultConfig();
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);

            var path = Path.Combine(backupDir, "20260913-030000-000.xml");
            var saved = DefaultConfig();
            saved.RecordsPath = config.RecordsPath;
            saved.CatalogueSnapshotCount = config.CatalogueSnapshotCount + 3;
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
            using (var stream = File.Create(path))
            {
                serializer.Serialize(stream, saved);
            }

            var copy = service.ListConfigurationCopies(config).Copies.Single();

            Assert.False(copy.IdenticalToCurrent);
            Assert.Equal(1, copy.DifferingSettings);
            Assert.False(copy.DecisionStoresDiffer);
            Assert.Equal("1 setting", copy.ChangeSummary);
        }

        /// <summary>
        /// Decision stores are reported separately rather than counted as settings: a churned
        /// exclusion list is one fact about a copy, not evidence that its settings were edited.
        /// </summary>
        [Fact]
        public void DifferentDecisionStoresAreReportedSeparatelyFromSettings()
        {
            var config = DefaultConfig();
            config.ExcludedVodStreamIds = new[] { 1, 2, 3 };
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);

            var path = Path.Combine(backupDir, "20260913-030000-000.xml");
            var saved = DefaultConfig();
            saved.RecordsPath = config.RecordsPath;
            saved.ExcludedVodStreamIds = new[] { 1, 2, 3, 4, 5 };
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
            using (var stream = File.Create(path))
            {
                serializer.Serialize(stream, saved);
            }

            var copy = service.ListConfigurationCopies(config).Copies.Single();

            Assert.True(copy.DecisionStoresDiffer);
            Assert.Equal(0, copy.DifferingSettings);
            Assert.False(copy.IdenticalToCurrent);
        }

        /// <summary>
        /// The category selections are int[], which object.Equals compares by reference — so
        /// without array-aware comparison every copy would read as differing on them forever.
        /// </summary>
        [Fact]
        public void EqualArrayValuedSettingsDoNotCountAsDifferences()
        {
            var config = DefaultConfig();
            config.SelectedVodCategoryIds = new[] { 4, 5, 6 };
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);

            var path = Path.Combine(backupDir, "20260913-030000-000.xml");
            var saved = DefaultConfig();
            saved.RecordsPath = config.RecordsPath;
            saved.SelectedVodCategoryIds = new[] { 4, 5, 6 };
            var serializer = new System.Xml.Serialization.XmlSerializer(typeof(PluginConfiguration));
            using (var stream = File.Create(path))
            {
                serializer.Serialize(stream, saved);
            }

            var copy = service.ListConfigurationCopies(config).Copies.Single();

            Assert.Equal(0, copy.DifferingSettings);
        }

        // ---- Refusing a damaged copy ----

        /// <summary>
        /// The failure this whole ADR exists to prevent, arriving through the tool built to
        /// recover from it: applying a copy whose store cannot be read would write the unreadable
        /// field back as the live one.
        /// </summary>
        [Fact]
        public void ACopyWithAnUnparseableStoreIsNotRestorable()
        {
            var config = DefaultConfig();
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);

            WriteCopy(backupDir, "20260913-030000-000", reviewedVodJson: "{ not an array }");

            var copy = service.ListConfigurationCopies(config).Copies.Single();

            Assert.False(copy.Restorable);
            Assert.Equal(StrmSyncService.UnparseableStore, copy.ReviewedVodStreamIdsJson);
            Assert.False(string.IsNullOrEmpty(copy.Problem));
        }

        /// <summary>
        /// Absent and unreadable are different answers. An empty store is legitimate and must stay
        /// restorable, or a fresh install's own backup would be refused.
        /// </summary>
        [Fact]
        public void AnEmptyStoreIsZeroAndStillRestorable()
        {
            var config = DefaultConfig();
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);

            WriteCopy(backupDir, "20260913-030000-000", reviewedVodJson: "");

            var copy = service.ListConfigurationCopies(config).Copies.Single();

            Assert.Equal("0", copy.ReviewedVodStreamIdsJson);
            Assert.True(copy.Restorable);
        }

        [Fact]
        public void RestoringAnUnparseableCopyIsRefused()
        {
            var config = DefaultConfig();
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);
            var applied = new List<PluginConfiguration>();
            service.ApplyRestoredConfiguration = applied.Add;

            var path = WriteCopy(backupDir, "20260913-030000-000", reviewedVodJson: "{ nope }");

            var result = service.RestoreConfiguration(config, path);

            Assert.False(result.Success);
            Assert.Empty(applied);
        }

        // ---- Refusing a path that is not ours ----

        [Fact]
        public void RestoringAPathOutsideTheManagedFoldersIsRefused()
        {
            var config = DefaultConfig();
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);
            var applied = new List<PluginConfiguration>();
            service.ApplyRestoredConfiguration = applied.Add;

            var stray = WriteCopy(TempDir.Path, "20260913-030000-000");

            var result = service.RestoreConfiguration(config, stray);

            Assert.False(result.Success);
            Assert.Empty(applied);
        }

        /// <summary>
        /// The containing directory is compared, not a string prefix — otherwise a sibling folder
        /// whose name merely starts with the same characters would be accepted.
        /// </summary>
        [Fact]
        public void ASiblingDirectoryWithASharedPrefixIsNotACandidate()
        {
            var config = DefaultConfig();
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);

            var lookalike = rollbackDir + "-old";
            Directory.CreateDirectory(lookalike);
            var path = WriteCopy(lookalike, "20260913-030000-000");

            Assert.False(service.IsRestoreCandidate(config, path));
        }

        [Fact]
        public void ACandidateInEitherManagedFolderIsAccepted()
        {
            var config = DefaultConfig();
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);

            Assert.True(service.IsRestoreCandidate(config, WriteCopy(backupDir, "20260913-030000-000")));
            Assert.True(service.IsRestoreCandidate(config, WriteCopy(rollbackDir, "20260914-030000-000")));
        }

        [Fact]
        public void RestoringAMissingFileIsRefused()
        {
            var config = DefaultConfig();
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);

            var result = service.RestoreConfiguration(
                config, Path.Combine(backupDir, "20260101-000000-000.xml"));

            Assert.False(result.Success);
        }

        // ---- The happy path ----

        [Fact]
        public void RestoringAppliesTheSavedConfiguration()
        {
            var config = DefaultConfig();
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);
            var applied = new List<PluginConfiguration>();
            service.ApplyRestoredConfiguration = applied.Add;

            var path = WriteCopy(backupDir, "20260913-030000-000", excludedVod: new[] { 7, 8, 9, 10, 11 });

            var result = service.RestoreConfiguration(config, path);

            Assert.True(result.Success);
            Assert.Single(applied);
            Assert.Equal(5, applied[0].ExcludedVodStreamIds.Length);
            Assert.Contains("2026-09-13 03:00:00", result.Message);
        }

        /// <summary>
        /// The durable history must record the step change, or the counts log shows a jump with
        /// nothing explaining it — the damage the shared line format exists to prevent.
        /// </summary>
        [Fact]
        public void RestoringAppendsACountsLine()
        {
            var config = DefaultConfig();
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);
            service.ApplyRestoredConfiguration = _ => { };

            var path = WriteCopy(backupDir, "20260913-030000-000", excludedVod: new[] { 7, 8, 9, 10, 11 });
            service.RestoreConfiguration(config, path);

            var countsLog = Path.Combine(config.RecordsPath, StrmSyncService.CountsLogFileName);
            Assert.True(File.Exists(countsLog));
            Assert.Contains("ExcludedVodStreamIds=5", File.ReadAllText(countsLog));
        }

        /// <summary>
        /// A whole-file restore replaces every setting, so it can move the records root as a side
        /// effect of recovering decisions — and relocating that root does not migrate what is
        /// already there, which splits the counts history in two. Silently doing that would be the
        /// worst kind of surprise, so the result says it happened.
        /// </summary>
        [Fact]
        public void RestoringACopyThatMovesTheRecordsRootSaysSo()
        {
            var config = DefaultConfig();
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);
            service.ApplyRestoredConfiguration = _ => { };

            var elsewhere = Path.Combine(TempDir.Path, "somewhere-else");
            var path = WriteCopy(backupDir, "20260913-030000-000", recordsPath: elsewhere);

            var result = service.RestoreConfiguration(config, path);

            Assert.True(result.Success);
            Assert.Contains("backup and records folder", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(elsewhere, result.Message);
            // And the counts line follows the configuration that is now live, not the old root.
            Assert.True(File.Exists(Path.Combine(elsewhere, StrmSyncService.CountsLogFileName)));
        }

        /// <summary>The ordinary case: same records root, so nothing about relocation is said.</summary>
        [Fact]
        public void RestoringACopyWithTheSameRecordsRootSaysNothingAboutRelocation()
        {
            var config = DefaultConfig();
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);
            service.ApplyRestoredConfiguration = _ => { };

            var path = WriteCopy(backupDir, "20260913-030000-000");

            var result = service.RestoreConfiguration(config, path);

            Assert.True(result.Success);
            Assert.DoesNotContain("backup and records folder", result.Message, StringComparison.OrdinalIgnoreCase);
        }

        // ---- The sync guard ----

        /// <summary>
        /// A sync writes watermarks and reviewed IDs back as it finishes, so a restore landing
        /// mid-run would be silently overwritten by the sync's own results.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task RestoringIsRefusedWhileASyncIsRunning()
        {
            var config = DefaultConfig();
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);
            var applied = new List<PluginConfiguration>();
            service.ApplyRestoredConfiguration = applied.Add;

            var path = WriteCopy(backupDir, "20260913-030000-000");

            Handler.RespondWith("get_vod_streams",
                VodStreamsJson(VodStream(streamId: 1, name: "Test Movie", added: 1000)));

            // Attempt the restore from inside the sync's own progress callback — the only point at
            // which the running flag is reliably set without racing the run's completion.
            RestoreConfigurationResult duringSync = null;
            var probe = new InlineProgress(_ =>
            {
                if (duringSync == null)
                {
                    duringSync = service.RestoreConfiguration(config, path);
                }
            });

            await service.SyncMoviesAsync(config, None, SaveConfig, probe);

            Assert.NotNull(duringSync);
            Assert.False(duringSync.Success);
            Assert.Contains("sync is running", duringSync.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(applied);
        }

        /// <summary>
        /// The series half of the same guard. Worth its own test rather than assuming symmetry:
        /// the two progress objects are separate fields, and a guard that checked the movie one
        /// twice would pass every movie-side test while leaving a series sync unprotected.
        /// </summary>
        [Fact]
        public async System.Threading.Tasks.Task RestoringIsRefusedWhileASeriesSyncIsRunning()
        {
            var config = DefaultConfig();
            string backupDir, rollbackDir;
            var service = MakeRestoreService(config, out backupDir, out rollbackDir);
            var applied = new List<PluginConfiguration>();
            service.ApplyRestoredConfiguration = applied.Add;

            var path = WriteCopy(backupDir, "20260913-030000-000");

            Handler.RespondWith("action=get_series",
                SeriesListJson(Series(seriesId: 1, name: "Test Show", lastModified: "2000")));
            Handler.RespondWith("action=get_series_info",
                SeriesDetailJson(seriesId: 1, seasonNum: 1, episodeNum: 1, title: "Episode Title", ext: "mp4"));

            RestoreConfigurationResult duringSync = null;
            var probe = new InlineProgress(_ =>
            {
                if (duringSync == null)
                {
                    duringSync = service.RestoreConfiguration(config, path);
                }
            });

            await service.SyncSeriesAsync(config, None, SaveConfig, probe);

            Assert.NotNull(duringSync);
            Assert.False(duringSync.Success);
            Assert.Contains("sync is running", duringSync.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(applied);
        }

        /// <summary>
        /// Reports synchronously, unlike <see cref="Progress{T}"/>, which posts to a
        /// synchronization context and would run after the sync had already finished.
        /// </summary>
        private sealed class InlineProgress : IProgress<double>
        {
            private readonly Action<double> _onReport;

            public InlineProgress(Action<double> onReport)
            {
                _onReport = onReport;
            }

            public void Report(double value)
            {
                _onReport(value);
            }
        }
    }
}
