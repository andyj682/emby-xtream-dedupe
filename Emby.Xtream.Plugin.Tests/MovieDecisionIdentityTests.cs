using System.Collections.Generic;
using Emby.Xtream.Plugin.Client.Models;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Unit tests for ADR-F004 stage 3 — carrying stored movie decisions across a provider
    /// re-issuing its stream ids (<see cref="StrmSyncService.ReconcileMovieDecisionIdentity"/>
    /// and the two serializers beside it).
    /// </summary>
    public class MovieDecisionIdentityTests
    {
        private const int Sample = 15;

        private static VodStreamInfo Vod(int streamId, int tmdb, string name = "A Film") =>
            new VodStreamInfo
            {
                StreamId = streamId,
                Name = name,
                TmdbId = tmdb > 0 ? tmdb.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty,
            };

        private static StrmSyncService.MovieIdentityReconciliation Run(
            IList<VodStreamInfo> live,
            HashSet<int> excluded,
            HashSet<int> reviewed,
            Dictionary<int, int> map,
            bool allowRepoint = true) =>
            StrmSyncService.ReconcileMovieDecisionIdentity(live, excluded, reviewed, map, allowRepoint, Sample);

        // ---- Backfill: the migration, run incrementally against the live catalogue ----

        [Fact]
        public void Backfill_RecordsTmdbForEveryStoredDecisionStillInTheCatalogue()
        {
            var excluded = new HashSet<int> { 100 };
            var reviewed = new HashSet<int> { 200 };
            var map = new Dictionary<int, int>();

            var outcome = Run(
                new[] { Vod(100, 603), Vod(200, 604), Vod(300, 605) },
                excluded, reviewed, map);

            Assert.Equal(2, outcome.Backfilled);
            Assert.Equal(603, map[100]);
            Assert.Equal(604, map[200]);

            // 300 carries no decision, so it gets no identity record. The map tracks decisions,
            // not the catalogue — that is what keeps it ~66k entries rather than ~37k titles
            // of pure overhead.
            Assert.False(map.ContainsKey(300));
        }

        [Fact]
        public void Backfill_SkipsDecisionsWhoseTitleCarriesNoTmdb()
        {
            var excluded = new HashSet<int> { 100 };
            var map = new Dictionary<int, int>();

            var outcome = Run(new[] { Vod(100, 0) }, excluded, new HashSet<int>(), map);

            // The honest residue: a title the provider ships with no TMDB id cannot be given a
            // durable identity, and no amount of retrying changes that.
            Assert.Equal(0, outcome.Backfilled);
            Assert.Empty(map);
        }

        [Fact]
        public void Backfill_IsIdempotent()
        {
            var excluded = new HashSet<int> { 100 };
            var map = new Dictionary<int, int>();
            var live = new[] { Vod(100, 603) };

            Assert.Equal(1, Run(live, excluded, new HashSet<int>(), map).Backfilled);
            Assert.Equal(0, Run(live, excluded, new HashSet<int>(), map).Backfilled);
        }

        // ---- Re-pointing: the failure this whole stage exists for ----

        [Fact]
        public void Repoint_CarriesAnExclusionOntoTheReIssuedStreamId()
        {
            var excluded = new HashSet<int> { 100 };
            var map = new Dictionary<int, int> { { 100, 603 } };

            // 100 is gone from the catalogue; the same film is back as 900.
            var outcome = Run(new[] { Vod(900, 603) }, excluded, new HashSet<int>(), map);

            Assert.Equal(1, outcome.RepointedExclusions);
            Assert.Contains(900, excluded);
            Assert.Equal(603, map[900]);
            Assert.Single(outcome.Samples);
            Assert.Contains("100 → 900", outcome.Samples[0]);
        }

        [Fact]
        public void Repoint_CarriesAReviewedMarkOntoTheReIssuedStreamId()
        {
            // The user's stated invariant: a film they reviewed and kept must not reappear in
            // the unreviewed queue just because the provider renumbered it.
            var reviewed = new HashSet<int> { 100 };
            var map = new Dictionary<int, int> { { 100, 603 } };

            var outcome = Run(new[] { Vod(900, 603) }, new HashSet<int>(), reviewed, map);

            Assert.Equal(1, outcome.RepointedReviews);
            Assert.Contains(900, reviewed);
        }

        [Fact]
        public void Repoint_ReviewedThenExcluded_SurvivesRotationInBothStores()
        {
            // Reviewing a title and later excluding it is an ordinary sequence, so an id sits in
            // both stores. A rotation must reproduce that state exactly, not collapse it — which
            // is why the exclusion guard reads the reviewed set as it stood BEFORE the pass.
            var excluded = new HashSet<int> { 100 };
            var reviewed = new HashSet<int> { 100 };
            var map = new Dictionary<int, int> { { 100, 603 } };

            Run(new[] { Vod(900, 603) }, excluded, reviewed, map);

            Assert.Contains(900, excluded);
            Assert.Contains(900, reviewed);
        }

        [Fact]
        public void Repoint_DeclinesAnExclusionOntoAnIdTheUserHasSinceReviewedAndKept()
        {
            // Old exclusion, then a rotation, then the user decided to keep the title under its
            // new id. The newer decision wins: re-applying the old one here would have
            // RemoveExcludedContent delete a folder they had just chosen to keep, with no ratio
            // guard and no warning.
            var excluded = new HashSet<int> { 100 };
            var reviewed = new HashSet<int> { 900 };
            var map = new Dictionary<int, int> { { 100, 603 } };

            var outcome = Run(new[] { Vod(900, 603) }, excluded, reviewed, map);

            Assert.Equal(1, outcome.DeclinedExclusions);
            Assert.Equal(0, outcome.RepointedExclusions);
            Assert.DoesNotContain(900, excluded);

            // A declined move carried nothing, so it must not be reported or sampled as though
            // it had — otherwise the log claims a title moved on every run, forever.
            Assert.Equal(0, outcome.MovedTitles);
            Assert.Empty(outcome.Samples);
        }

        [Fact]
        public void Repoint_LeavesALiveIdAlone()
        {
            var excluded = new HashSet<int> { 100 };
            var map = new Dictionary<int, int> { { 100, 603 } };

            var outcome = Run(new[] { Vod(100, 603) }, excluded, new HashSet<int>(), map);

            Assert.Equal(0, outcome.RepointedExclusions);
            Assert.Equal(0, outcome.MovedTitles);
        }

        [Fact]
        public void Repoint_KeepsTheRecordForADeadIdNothingCarriesTheTmdbFor()
        {
            var excluded = new HashSet<int> { 100 };
            var map = new Dictionary<int, int> { { 100, 603 } };

            var outcome = Run(new[] { Vod(900, 999) }, excluded, new HashSet<int>(), map);

            Assert.Equal(0, outcome.MovedTitles);

            // Keeping it is the point: if the title returns under a third id later, this record
            // is the only thing that can recognize it.
            Assert.Equal(603, map[100]);
        }

        [Fact]
        public void Repoint_PicksTheLowestIdWhenTwoLiveRowsCarryTheSameTmdb()
        {
            // Unmerged duplicate rows. Picking deterministically stops the target flipping
            // between runs, which is the failure the series collapse representative once had.
            var excluded = new HashSet<int> { 100 };
            var map = new Dictionary<int, int> { { 100, 603 } };

            Run(new[] { Vod(950, 603), Vod(900, 603) }, excluded, new HashSet<int>(), map);

            Assert.Contains(900, excluded);
            Assert.DoesNotContain(950, excluded);
        }

        [Fact]
        public void Repoint_IsIdempotentAcrossRuns()
        {
            var excluded = new HashSet<int> { 100 };
            var map = new Dictionary<int, int> { { 100, 603 } };
            var live = new[] { Vod(900, 603) };

            Assert.Equal(1, Run(live, excluded, new HashSet<int>(), map).RepointedExclusions);
            Assert.Equal(0, Run(live, excluded, new HashSet<int>(), map).RepointedExclusions);
        }

        // ---- Pruning: telling a withdrawn decision from a rotated id ----

        [Fact]
        public void Prune_DropsTheIdentityRecordWhenTheDecisionIsWithdrawn()
        {
            // The user un-excluded 100 — here, or in upstream's category tree, which knows
            // nothing about TMDB and cannot clean up after itself.
            var map = new Dictionary<int, int> { { 100, 603 } };

            var outcome = Run(new[] { Vod(100, 603) }, new HashSet<int>(), new HashSet<int>(), map);

            Assert.Equal(1, outcome.Pruned);
            Assert.Empty(map);
        }

        [Fact]
        public void Prune_StopsAWithdrawnExclusionBeingSilentlyReapplied()
        {
            // The destructive case, and the reason the store holds PAIRS rather than a set of
            // excluded TMDB ids: with only a set, "603 is excluded" survives the un-exclusion,
            // the next sync reads it as a rotation, re-excludes the title and deletes the folder.
            var excluded = new HashSet<int>();
            var map = new Dictionary<int, int> { { 100, 603 } };

            // The user un-excluded it, and the provider renumbered it in the same window.
            var outcome = Run(new[] { Vod(900, 603) }, excluded, new HashSet<int>(), map);

            Assert.Equal(1, outcome.Pruned);
            Assert.Empty(excluded);
            Assert.Equal(0, outcome.RepointedExclusions);
        }

        // ---- The safety property the whole design rests on ----

        [Fact]
        public void NeverRemovesADecisionFromEitherStore()
        {
            // This pass runs automatically on every sync with no confirmation step, which is
            // only defensible because it cannot subtract. It adds ids to the two stores and
            // drops entries from the identity map; a dropped entry loses an identity record,
            // never a decision.
            var excluded = new HashSet<int> { 100, 101, 102 };
            var reviewed = new HashSet<int> { 200, 201 };
            var map = new Dictionary<int, int>
            {
                { 100, 603 }, { 101, 604 }, { 200, 605 },
                { 777, 606 }, // record for a decision that no longer exists
            };

            Run(
                new[] { Vod(900, 603), Vod(101, 604), Vod(200, 605), Vod(950, 606) },
                excluded, reviewed, map);

            foreach (var id in new[] { 100, 101, 102 })
            {
                Assert.Contains(id, excluded);
            }

            foreach (var id in new[] { 200, 201 })
            {
                Assert.Contains(id, reviewed);
            }

            // 606's record was pruned (no decision holds 777), so nothing was re-pointed onto
            // 950 even though a live row carries that TMDB.
            Assert.DoesNotContain(950, excluded);
            Assert.DoesNotContain(950, reviewed);
        }

        [Fact]
        public void PartialFetch_BackfillsAndPrunesButDoesNotRepoint()
        {
            // Absence is how a dead id is recognized, so a category that failed to answer makes
            // every live id in it look dead. Re-pointing stands down; the other two steps infer
            // nothing from absence and stay safe.
            var excluded = new HashSet<int> { 100, 300 };
            var map = new Dictionary<int, int> { { 100, 603 } };

            var outcome = Run(
                new[] { Vod(900, 603), Vod(300, 607) },
                excluded, new HashSet<int>(), map, allowRepoint: false);

            Assert.Equal(0, outcome.RepointedExclusions);
            Assert.DoesNotContain(900, excluded);
            Assert.Equal(1, outcome.Backfilled);
            Assert.Equal(607, map[300]);
        }

        // ---- Serialization ----

        [Fact]
        public void TmdbMap_RoundTrips()
        {
            var map = new Dictionary<int, int> { { 200, 604 }, { 100, 603 } };

            var json = StrmSyncService.SerializeTmdbMap(map);
            var back = StrmSyncService.DeserializeTmdbMap(json);

            Assert.Equal(2, back.Count);
            Assert.Equal(603, back[100]);
            Assert.Equal(604, back[200]);

            // Key-sorted, so successive saves diff readably instead of reshuffling.
            Assert.True(json.IndexOf("100", System.StringComparison.Ordinal)
                < json.IndexOf("200", System.StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void TmdbMap_AbsentOrEmptyReadsAsEmptyNotAsAFailure(string json)
        {
            var map = StrmSyncService.DeserializeTmdbMap(json);

            Assert.NotNull(map);
            Assert.Empty(map);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("[1,2,3]")]
        [InlineData("{\"100\":\"lots\"}")]
        public void TmdbMap_UnreadableReturnsNullSoTheCallerCanStandDown(string json)
        {
            // Empty and unreadable must stay distinguishable. Reading a broken map as "no
            // identities are known" looks exactly like every decision having just been
            // withdrawn — and the pruning pass would agree and make it permanent.
            Assert.Null(StrmSyncService.DeserializeTmdbMap(json));
        }

        [Fact]
        public void TmdbMap_EmptySerializesToEmptyStringNotAnEmptyObject()
        {
            Assert.Equal(string.Empty, StrmSyncService.SerializeTmdbMap(new Dictionary<int, int>()));
            Assert.Equal(string.Empty, StrmSyncService.SerializeTmdbMap(null));
        }
    }
}
