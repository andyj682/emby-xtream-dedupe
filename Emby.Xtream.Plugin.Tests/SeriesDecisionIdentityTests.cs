using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client.Models;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// ADR-F009 — carrying series decisions across a show coming back under a new id
    /// (<see cref="StrmSyncService.ReconcileSeriesDecisionIdentity"/>), the keep-wins rule, and the
    /// listing's TMDB field.
    /// </summary>
    public class SeriesDecisionIdentityTests : SyncTestBase
    {
        private const int Sample = 15;

        private static SeriesInfo Show(int seriesId, int tmdb, string name = "A Show") =>
            new SeriesInfo
            {
                SeriesId = seriesId,
                Name = name,
                TmdbIdField = tmdb > 0 ? tmdb.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty,
            };

        private static StrmSyncService.SeriesIdentityReconciliation Run(
            IList<SeriesInfo> live,
            HashSet<int> excluded,
            HashSet<int> reviewed,
            Dictionary<int, int> map,
            Dictionary<string, string> hashes = null,
            bool allowRepoint = true) =>
            StrmSyncService.ReconcileSeriesDecisionIdentity(
                live, excluded, reviewed, hashes ?? new Dictionary<string, string>(), map, allowRepoint, Sample);

        // ---- The listing field ----

        [Theory]
        [InlineData("{\"series_id\":1,\"tmdb_id\":\"500\"}", "500")]   // Dispatcharr
        [InlineData("{\"series_id\":1,\"tmdb\":\"500\"}", "500")]      // other Xtream panels
        [InlineData("{\"series_id\":1,\"tmdb\":\"\",\"tmdb_id\":\"500\"}", "500")]
        [InlineData("{\"series_id\":1,\"tmdb\":\"400\",\"tmdb_id\":\"500\"}", "400")]
        [InlineData("{\"series_id\":1}", "")]
        public void SeriesInfo_ReadsTheTmdbIdUnderEitherName(string json, string expected)
        {
            var info = JsonSerializer.Deserialize<SeriesInfo>(json);
            Assert.Equal(expected, info.TmdbId);
        }

        [Fact]
        public void SeriesInfo_ReadsANumericTmdbIdThroughTheSyncsOptions()
        {
            // Dispatcharr sends a string, but a panel that sends a number must not abort the parse:
            // the sync's options carry the tolerant string converter (ADR-010), as these do.
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            options.Converters.Add(new TolerantStringConverter());
            var info = JsonSerializer.Deserialize<SeriesInfo>("{\"series_id\":1,\"tmdb_id\":500}", options);
            Assert.Equal("500", info.TmdbId);
        }

        // ---- Backfill and prune ----

        [Fact]
        public void Backfill_RecordsEveryListedIdThatHoldsADecisionOrAnEpisodeList()
        {
            var map = new Dictionary<int, int>();
            var outcome = Run(
                new[] { Show(1, 501), Show(2, 502), Show(3, 503), Show(4, 504) },
                new HashSet<int> { 1 }, new HashSet<int> { 2 }, map,
                new Dictionary<string, string> { { "3", "h" } });

            Assert.Equal(3, outcome.Backfilled);
            Assert.Equal(new[] { 1, 2, 3 }, map.Keys.OrderBy(k => k));
        }

        [Fact]
        public void Update_AListedIdsRecordFollowsItsCurrentTmdbId()
        {
            // Dispatcharr corrected the show's TMDB id, or a merge handed it another show's.
            var map = new Dictionary<int, int> { { 1, 501 } };
            var outcome = Run(new[] { Show(1, 777) }, new HashSet<int> { 1 }, new HashSet<int>(), map);

            Assert.Equal(1, outcome.Updated);
            Assert.Equal(777, map[1]);
            Assert.Equal(0, outcome.MovedShows);
        }

        [Fact]
        public void Update_TheRigsStagedMerge_LiftsAndLeavesTheSurvivorRecordedUnderTheShowsId()
        {
            // As staged on the test rig: the excluded show was recorded under its own TMDB id,
            // then received the kept show's TMDB id and provider entries.
            var excluded = new HashSet<int> { 104489 };
            var reviewed = new HashSet<int> { 16901 };
            var hashes = new Dictionary<string, string> { { "16901", "h" } };
            var map = new Dictionary<int, int> { { 16901, 11245 }, { 104489, 39299 } };

            var outcome = Run(new[] { Show(104489, 11245, "Merged Show") }, excluded, reviewed, map, hashes);

            Assert.DoesNotContain(104489, excluded);
            Assert.Contains(104489, reviewed);
            Assert.Equal("h", hashes["104489"]);
            Assert.Single(outcome.Lifted);
            Assert.Equal(11245, map[104489]);
            Assert.False(map.ContainsKey(16901));
        }

        [Fact]
        public void Prune_DropsARecordWhoseIdHoldsNothing()
        {
            var map = new Dictionary<int, int> { { 1, 501 } };
            var outcome = Run(new[] { Show(1, 501) }, new HashSet<int>(), new HashSet<int>(), map);

            Assert.Equal(1, outcome.Pruned);
            Assert.Empty(map);
        }

        [Fact]
        public void Prune_StandsDownWhenTheEpisodeListsAreUnreadable()
        {
            // An id that holds only an episode list would look empty and lose its record.
            var map = new Dictionary<int, int> { { 1, 501 } };
            var outcome = StrmSyncService.ReconcileSeriesDecisionIdentity(
                new[] { Show(1, 501) }, new HashSet<int>(), new HashSet<int>(), null, map, true, Sample);

            Assert.Equal(0, outcome.Pruned);
            Assert.Equal(501, map[1]);
        }

        // ---- Moves ----

        [Fact]
        public void Move_CarriesAnExclusionToTheNewId()
        {
            var excluded = new HashSet<int> { 100 };
            var map = new Dictionary<int, int> { { 100, 500 } };

            var outcome = Run(new[] { Show(900, 500) }, excluded, new HashSet<int>(), map);

            Assert.Contains(900, excluded);
            Assert.Equal(1, outcome.RepointedExclusions);
            Assert.Equal(1, outcome.MovedShows);
        }

        [Fact]
        public void Move_CarriesTheReviewedMarkAndTheEpisodeList()
        {
            var reviewed = new HashSet<int> { 100 };
            var hashes = new Dictionary<string, string> { { "100", "abc" } };
            var map = new Dictionary<int, int> { { 100, 500 } };

            var outcome = Run(new[] { Show(900, 500) }, new HashSet<int>(), reviewed, map, hashes);

            Assert.Contains(900, reviewed);
            Assert.Equal("abc", hashes["900"]);
            Assert.Equal(1, outcome.RepointedReviews);
            Assert.Equal(1, outcome.RepointedHashes);
        }

        [Fact]
        public void Move_RetiresTheOldRecordAndRecordsTheNewId_SoASecondRunDoesNothing()
        {
            var excluded = new HashSet<int> { 100 };
            var map = new Dictionary<int, int> { { 100, 500 } };
            var live = new[] { Show(900, 500) };

            var first = Run(live, excluded, new HashSet<int>(), map);
            Assert.Equal(1, first.Retired);
            Assert.False(map.ContainsKey(100));
            Assert.Equal(500, map[900]);

            var second = Run(live, excluded, new HashSet<int>(), map);
            Assert.False(second.ChangedAnything);
        }

        [Fact]
        public void Move_ADeadIdWhoseShowIsListedNowhereKeepsItsRecord()
        {
            // The only thing that can recognize the show if it comes back later.
            var map = new Dictionary<int, int> { { 100, 500 } };
            var outcome = Run(new[] { Show(900, 999) }, new HashSet<int> { 100 }, new HashSet<int>(), map);

            Assert.Equal(0, outcome.MovedShows);
            Assert.Equal(500, map[100]);
        }

        [Fact]
        public void Move_ThePickIsTheLowestListedIdCarryingTheTmdbId()
        {
            // Every category's copy on 0.32, or an unmerged pair on 0.33.
            var excluded = new HashSet<int> { 100 };
            Run(new[] { Show(950, 500), Show(900, 500) }, excluded, new HashSet<int>(),
                new Dictionary<int, int> { { 100, 500 } });

            Assert.Contains(900, excluded);
            Assert.DoesNotContain(950, excluded);
        }

        [Fact]
        public void PartialListing_BackfillsButMovesNothing()
        {
            var excluded = new HashSet<int> { 100, 1 };
            var map = new Dictionary<int, int> { { 100, 500 } };

            var outcome = Run(new[] { Show(900, 500), Show(1, 501) }, excluded, new HashSet<int>(), map,
                allowRepoint: false);

            Assert.DoesNotContain(900, excluded);
            Assert.Equal(1, outcome.Backfilled);
            Assert.Equal(500, map[100]);
        }

        // ---- Keep wins, where kept means reviewed and not excluded ----

        [Fact]
        public void KeepWins_AnExclusionIsNotCarriedOntoAShowKeptUnderAListedId()
        {
            // An excluded copy moved onto the id of a copy the user keeps.
            var excluded = new HashSet<int> { 100 };
            var reviewed = new HashSet<int> { 900 };

            var outcome = Run(new[] { Show(900, 500) }, excluded, reviewed,
                new Dictionary<int, int> { { 100, 500 } });

            Assert.DoesNotContain(900, excluded);
            Assert.Equal(1, outcome.DeclinedExclusions);
        }

        [Fact]
        public void KeepWins_AKeptAndAnExcludedCopyMergedIntoANewId_StayKept()
        {
            // Both copies left the listing; the merged show is listed under a third id.
            var excluded = new HashSet<int> { 100 };
            var reviewed = new HashSet<int> { 200 };
            var map = new Dictionary<int, int> { { 100, 500 }, { 200, 500 } };

            var outcome = Run(new[] { Show(900, 500) }, excluded, reviewed, map);

            Assert.DoesNotContain(900, excluded);
            Assert.Contains(900, reviewed);
            Assert.Equal(1, outcome.DeclinedExclusions);
        }

        [Fact]
        public void KeepWins_LiftsTheExclusionWhenAKeptCopyMergesIntoTheExcludedId()
        {
            // The case ADR-F009 exists for: the merge kept the EXCLUDED copy's id.
            var excluded = new HashSet<int> { 900 };
            var reviewed = new HashSet<int> { 200 };
            var map = new Dictionary<int, int> { { 200, 500 }, { 900, 500 } };

            var outcome = Run(new[] { Show(900, 500, "We Crashed") }, excluded, reviewed, map);

            Assert.DoesNotContain(900, excluded);
            Assert.Contains(900, reviewed);
            Assert.Single(outcome.Lifted);
            Assert.Contains("We Crashed", outcome.Lifted[0]);
        }

        [Fact]
        public void KeepWins_ALiftHappensOnce_SoAReExclusionSticks()
        {
            var excluded = new HashSet<int> { 900 };
            var reviewed = new HashSet<int> { 200 };
            var map = new Dictionary<int, int> { { 200, 500 }, { 900, 500 } };
            var live = new[] { Show(900, 500) };

            Run(live, excluded, reviewed, map);
            Assert.DoesNotContain(900, excluded);

            // The user meant it, and excludes the show again.
            excluded.Add(900);
            var second = Run(live, excluded, reviewed, map);

            Assert.Contains(900, excluded);
            Assert.Empty(second.Lifted);
        }

        [Fact]
        public void KeepWins_NoLiftWhenAListedCopyIsAlreadyKept()
        {
            // 0.32: one listed copy kept, one listed copy excluded as a duplicate, and a third
            // kept copy's category went away. The show is still kept; the duplicate stays excluded.
            var excluded = new HashSet<int> { 900 };
            var reviewed = new HashSet<int> { 200, 950 };
            var map = new Dictionary<int, int> { { 200, 500 } };

            var outcome = Run(new[] { Show(900, 500), Show(950, 500) }, excluded, reviewed, map);

            Assert.Contains(900, excluded);
            Assert.Empty(outcome.Lifted);
        }

        [Fact]
        public void Unreviewed_IsNotKept_SoTheExclusionCarries()
        {
            // Gate off: the other copy is on disk only because nothing excluded it. The exclusion
            // is the only decision there is.
            var excluded = new HashSet<int> { 100 };
            var hashes = new Dictionary<string, string> { { "900", "h" } };

            var outcome = Run(new[] { Show(900, 500) }, excluded, new HashSet<int>(),
                new Dictionary<int, int> { { 100, 500 } }, hashes);

            Assert.Contains(900, excluded);
            Assert.Equal(0, outcome.DeclinedExclusions);
        }

        [Fact]
        public void ReviewedThenExcluded_IsNotKept_SoBothCarry()
        {
            var excluded = new HashSet<int> { 100 };
            var reviewed = new HashSet<int> { 100 };

            Run(new[] { Show(900, 500) }, excluded, reviewed, new Dictionary<int, int> { { 100, 500 } });

            Assert.Contains(900, excluded);
            Assert.Contains(900, reviewed);
        }

        [Fact]
        public void OnlyALiftRemovesADecision()
        {
            // Apart from a lift, the pass only adds to the stores.
            var excluded = new HashSet<int> { 100, 101, 102 };
            var reviewed = new HashSet<int> { 200, 201 };
            var map = new Dictionary<int, int> { { 100, 500 }, { 101, 501 }, { 200, 502 } };

            Run(new[] { Show(900, 500), Show(101, 501), Show(950, 502) }, excluded, reviewed, map);

            Assert.Superset(new HashSet<int> { 100, 101, 102 }, excluded);
            Assert.Superset(new HashSet<int> { 200, 201 }, reviewed);
        }

        // ---- Through the sync ----

        private static string DispatcharrSeriesListJson(int seriesId, string name, int tmdb) =>
            JsonSerializer.Serialize(new[]
            {
                new { series_id = seriesId, name, last_modified = "2000", tmdb_id = tmdb.ToString(), category_id = (int?)null },
            });

        [Fact]
        public async Task Sync_CarriesAnExclusionToTheShowsNewId_AndWritesNothing()
        {
            var config = DefaultConfig();
            config.ExcludedSeriesIds = new[] { 100 };
            config.SeriesDecisionTmdbIdsJson = "{\"100\":500}";

            Handler.RespondWith("action=get_series", DispatcharrSeriesListJson(900, "Test Show", 500));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.Contains(900, config.ExcludedSeriesIds);
            Assert.False(Directory.Exists(Path.Combine(TempDir.Path, "Shows", "Test Show")));
        }

        [Fact]
        public async Task Sync_LiftsTheExclusionWhenAKeptCopyMergedIntoIt_AndWritesTheShow()
        {
            var config = DefaultConfig();
            config.ExcludedSeriesIds = new[] { 900 };
            config.ReviewedSeriesIdsJson = "[200]";
            config.SeriesDecisionTmdbIdsJson = "{\"200\":500,\"900\":500}";

            Handler.RespondWith("action=get_series_info&series_id=900", SeriesDetailJson(seriesId: 900));
            Handler.RespondWith("action=get_series", DispatcharrSeriesListJson(900, "Test Show", 500));

            await MakeService().SyncSeriesAsync(config, None, SaveConfig);

            Assert.DoesNotContain(900, config.ExcludedSeriesIds);
            Assert.Contains("900", config.ReviewedSeriesIdsJson);
            Assert.True(File.Exists(Path.Combine(
                TempDir.Path, "Shows", "Test Show", "Season 01", "Test Show - S01E01.strm")));
        }
    }
}
