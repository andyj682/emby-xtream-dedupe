using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client.Models;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Tests for the wanted set (ADR-F008): the movies a sync keeps on disk, published as a
    /// JSON file so another component can scope work to the titles someone actually wants.
    ///
    /// Two properties carry the feature and neither is obvious from the code:
    ///   * A smart-skipped title is still wanted. In steady state almost every title skips, so
    ///     a collection point below the skip would publish a nearly empty file every night.
    ///   * A partial catalogue fetch publishes nothing. A file listing only the categories
    ///     that answered reads as authoritative and silently instructs the consumer to do
    ///     less work.
    /// </summary>
    public class WantedSetTests : SyncTestBase
    {
        // A directory that does not exist yet, so every test also exercises creation. Inside
        // the library root only for cleanup: orphan cleanup scans "<root>/Movies", never a
        // sibling of it.
        private string ExchangeDir => Path.Combine(TempDir.Path, "exchange");

        private string PublishedFile => Path.Combine(ExchangeDir, StrmSyncService.WantedSetFileName);

        private void RegisterVodStreams(string json)
            => Handler.RespondWith("get_vod_streams", json);

        private PluginConfiguration PublishingConfig()
        {
            var config = DefaultConfig();
            config.WantedSetPath = ExchangeDir;
            return config;
        }

        private JsonElement ReadWantedSet()
        {
            Assert.True(File.Exists(PublishedFile), "Expected a wanted set at: " + PublishedFile);
            return JsonDocument.Parse(File.ReadAllText(PublishedFile)).RootElement;
        }

        private static int[] TmdbIds(JsonElement root)
            => root.GetProperty("tmdb_ids").EnumerateArray().Select(e => e.GetInt32()).ToArray();

        private static int[] UnidentifiedStreamIds(JsonElement root)
            => root.GetProperty("unidentified").EnumerateArray()
                .Select(e => e.GetProperty("stream_id").GetInt32())
                .ToArray();

        // -----------------------------------------------------------------
        // Off by default
        // -----------------------------------------------------------------

        [Fact]
        public async Task BlankPath_PublishesNothing()
        {
            // Unlike the records root, this one cannot have a working default: the file is only
            // useful where another container can read it, which takes a mount.
            var config = DefaultConfig();
            Assert.Equal(string.Empty, config.WantedSetPath);
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Test Movie", added: 1000)));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.False(Directory.Exists(ExchangeDir), "A blank path must not create anything");
        }

        // -----------------------------------------------------------------
        // Shape of the file
        // -----------------------------------------------------------------

        [Fact]
        public async Task ConfiguredPath_CreatesTheDirectoryAndPublishesTheContract()
        {
            var config = PublishingConfig();
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Test Movie", added: 1000, tmdbId: "603")));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var root = ReadWantedSet();
            Assert.Equal(1, root.GetProperty("schema").GetInt32());
            Assert.Equal("emby-strm", root.GetProperty("generator").GetString());
            Assert.Equal(new[] { 603 }, TmdbIds(root));
            Assert.Empty(UnidentifiedStreamIds(root));
        }

        [Fact]
        public async Task GeneratedAt_IsUtcWithAnExplicitZone()
        {
            // The consumer parses this to decide whether the set is too stale to act on. An
            // unqualified local timestamp would make that answer depend on two containers
            // agreeing about the zone.
            var config = PublishingConfig();
            RegisterVodStreams(VodStreamsJson(VodStream(streamId: 1, name: "Test Movie", added: 1000)));

            var before = DateTime.UtcNow.AddSeconds(-5);
            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var text = ReadWantedSet().GetProperty("generated_at").GetString();
            Assert.EndsWith("Z", text, StringComparison.Ordinal);

            var parsed = DateTime.Parse(
                text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            Assert.InRange(parsed, before, DateTime.UtcNow.AddSeconds(5));
        }

        [Fact]
        public async Task Count_CoversTmdbIdsOnly()
        {
            // Stated in the ADR, the README and the code. A count that silently meant
            // "tmdb_ids plus unidentified" would make the consumer's partial-write guard
            // reject every healthy file.
            var config = PublishingConfig();
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Identified", added: 1000, tmdbId: "603"),
                VodStream(streamId: 2, name: "Nameless", added: 1000, tmdbId: "")));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var root = ReadWantedSet();
            Assert.Equal(1, root.GetProperty("count").GetInt32());
            Assert.Single(TmdbIds(root));
            Assert.Single(UnidentifiedStreamIds(root));
        }

        [Fact]
        public async Task TitleWithoutATmdbId_IsPublishedByStreamId()
        {
            // Not a rounding error to be dropped as a simplification: a provider that ships no
            // id also ships no stream metadata, so these are disproportionately the titles the
            // consumer's pass exists for.
            var config = PublishingConfig();
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 419883, name: "No Id Movie", added: 1000, tmdbId: "")));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var root = ReadWantedSet();
            Assert.Empty(TmdbIds(root));
            Assert.Equal(new[] { 419883 }, UnidentifiedStreamIds(root));
        }

        [Fact]
        public async Task TmdbIds_AreDeduplicatedAndSorted()
        {
            // Sorted so two runs over an unchanged set produce an identical file, which is what
            // makes a diff of two days' output mean something.
            var config = PublishingConfig();
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Movie C", added: 1000, tmdbId: "27205"),
                VodStream(streamId: 2, name: "Movie A", added: 1000, tmdbId: "603"),
                VodStream(streamId: 3, name: "Movie B", added: 1000, tmdbId: "603")));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(new[] { 603, 27205 }, TmdbIds(ReadWantedSet()));
        }

        // -----------------------------------------------------------------
        // What counts as wanted
        // -----------------------------------------------------------------

        [Fact]
        public async Task ASmartSkippedTitleIsStillWanted()
        {
            // 🔑 THE TEST THAT PROTECTS THE COLLECTION POINT. Moving the capture three lines
            // later — below the smart-skip return — would break the feature in steady state and
            // every other test here would still pass, because they all write files.
            //
            // The sentinel proves the skip genuinely happened rather than the title being
            // rewritten: if the sync had gone past the skip, the file would hold a real URL.
            var config = PublishingConfig();
            config.SmartSkipExisting = true;
            config.LastMovieSyncTimestamp = 9999;

            var strmPath = Path.Combine(TempDir.Path, "Movies", "Kept Movie", "Kept Movie.strm");
            Directory.CreateDirectory(Path.GetDirectoryName(strmPath));
            File.WriteAllText(strmPath, "SENTINEL");

            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Kept Movie", added: 5000, tmdbId: "603")));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal("SENTINEL", File.ReadAllText(strmPath));
            Assert.Equal(new[] { 603 }, TmdbIds(ReadWantedSet()));
        }

        [Fact]
        public async Task AnExcludedTitleIsNotWanted()
        {
            var config = PublishingConfig();
            config.ExcludedVodStreamIds = new[] { 2 };
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Kept Movie", added: 1000, tmdbId: "603"),
                VodStream(streamId: 2, name: "Blocked Movie", added: 1000, tmdbId: "27205")));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var root = ReadWantedSet();
            Assert.Equal(new[] { 603 }, TmdbIds(root));
            Assert.Empty(UnidentifiedStreamIds(root));
        }

        [Fact]
        public async Task ATitleHeldForReviewIsNotWanted()
        {
            // The whole point of the file is that it carries a decision the user made. A title
            // sitting in the review queue is one they have not made yet, and publishing it
            // would spend the consumer's effort on the provider's overnight additions.
            var config = PublishingConfig();
            config.RequireReviewBeforeSync = true;
            config.ReviewedVodStreamIdsJson = "[1]";
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Reviewed Movie", added: 1000, tmdbId: "603"),
                VodStream(streamId: 2, name: "Unreviewed Movie", added: 1000, tmdbId: "27205")));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(new[] { 603 }, TmdbIds(ReadWantedSet()));
        }

        [Fact]
        public async Task WithTheReviewGateOffEverythingIncludedIsWanted()
        {
            // Not a bug — with the gate off, "the titles this sync keeps" genuinely is the
            // whole included catalogue. Pinned because it is the case that makes the published
            // set large, and the sync warns about it rather than refusing.
            var config = PublishingConfig();
            config.RequireReviewBeforeSync = false;
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 1, name: "Movie A", added: 1000, tmdbId: "603"),
                VodStream(streamId: 2, name: "Movie B", added: 1000, tmdbId: "27205")));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(new[] { 603, 27205 }, TmdbIds(ReadWantedSet()));
        }

        // -----------------------------------------------------------------
        // resolved_tmdb_id — a second lookup key for titles the provider cannot name
        // -----------------------------------------------------------------
        //
        // Driven through WriteWantedSet directly rather than a full sync. The fallback lookup
        // runs against Emby's own metadata providers, which a unit test has no instance of, so
        // no amount of HTTP mocking makes it return a value through SyncMoviesAsync. The
        // collection point is covered by the integration tests above; these cover the shape.

        private static Tuple<VodStreamInfo, string> Wanted(
            int streamId, string providerTmdb, string resolvedTmdb, string name = null)
            => Tuple.Create(
                new VodStreamInfo
                {
                    StreamId = streamId,
                    Name = name ?? "Movie " + streamId,
                    TmdbId = providerTmdb,
                },
                resolvedTmdb);

        [Fact]
        public void AnUnidentifiedTitleCarriesTheProvidersRawName()
        {
            // The durable half of the entry. stream_id dies in a re-ingest; the name does not,
            // and it is the string the reader's own rows were built from.
            var config = PublishingConfig();

            MakeService().WriteWantedSet(
                config,
                new[] { Wanted(419883, string.Empty, null, "XX - Example Film (2019)") }.ToList(),
                catalogueComplete: true,
                reviewGateOn: true);

            var entry = ReadWantedSet().GetProperty("unidentified").EnumerateArray().Single();
            Assert.Equal("XX - Example Film (2019)", entry.GetProperty("name").GetString());
        }

        [Fact]
        public async Task TheNameIsPublishedUncleanedEvenWhenNameCleaningIsOn()
        {
            // Deliberately NOT the cleaned name. Cleaning exists to make a tidy folder on this
            // side; it moves the string AWAY from the provider feed the reader's rows came
            // from, which is the only thing this key is for.
            //
            // Driven through a real sync, because calling WriteWantedSet directly could not
            // exercise the cleaning flag at all — it only ever sees the raw model. And the
            // expected difference is derived from the cleaner rather than hardcoded, so if the
            // chosen term stops changing the string the test fails instead of passing hollowly.
            const string Raw = "XX - Example Film (2019)";
            var config = PublishingConfig();
            config.EnableContentNameCleaning = true;
            config.ContentRemoveTerms = "XX -";

            var cleaned = ContentNameCleaner.CleanContentName(Raw, config.ContentRemoveTerms);
            Assert.NotEqual(Raw, cleaned);

            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 419883, name: Raw, added: 1000, tmdbId: "")));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var entry = ReadWantedSet().GetProperty("unidentified").EnumerateArray().Single();
            Assert.Equal(Raw, entry.GetProperty("name").GetString());
        }

        [Fact]
        public async Task TheNameIsPresentRegardlessOfEveryTmdbSetting()
        {
            // The point of this key: it cannot be silently absent. resolved_tmdb_id needs two
            // flags on; this needs nothing, and a full sync with every TMDB setting off still
            // publishes it.
            var config = PublishingConfig();
            config.EnableTmdbFolderNaming = false;
            config.EnableTmdbFallbackLookup = false;
            RegisterVodStreams(VodStreamsJson(
                VodStream(streamId: 419883, name: "No Id Movie", added: 1000, tmdbId: "")));

            await MakeService().SyncMoviesAsync(config, None, SaveConfig);

            var entry = ReadWantedSet().GetProperty("unidentified").EnumerateArray().Single();
            Assert.Equal("No Id Movie", entry.GetProperty("name").GetString());
            Assert.False(entry.TryGetProperty("resolved_tmdb_id", out _));
        }

        [Fact]
        public void AResolvedTmdbIdIsPublishedBesideTheStreamIdNotInsteadOfIt()
        {
            // Beside, never instead: the consumer looks these ids up against its own rows, and
            // a row is unidentified there for the same reason it is here — so a promoted id
            // would resolve to nothing and silently drop the title out of scope.
            var config = PublishingConfig();

            MakeService().WriteWantedSet(
                config,
                new[] { Wanted(419883, string.Empty, "12345") }.ToList(),
                catalogueComplete: true,
                reviewGateOn: true);

            var root = ReadWantedSet();
            Assert.Empty(TmdbIds(root));
            Assert.Equal(0, root.GetProperty("count").GetInt32());

            var entry = root.GetProperty("unidentified").EnumerateArray().Single();
            Assert.Equal(419883, entry.GetProperty("stream_id").GetInt32());
            Assert.Equal(12345, entry.GetProperty("resolved_tmdb_id").GetInt32());
        }

        [Fact]
        public void TheKeyIsOmittedEntirelyWhenNothingWasResolved()
        {
            // Omitted rather than written as null, so "is this key present" is the only test
            // the reader needs.
            var config = PublishingConfig();

            MakeService().WriteWantedSet(
                config,
                new[] { Wanted(419883, string.Empty, null) }.ToList(),
                catalogueComplete: true,
                reviewGateOn: true);

            var entry = ReadWantedSet().GetProperty("unidentified").EnumerateArray().Single();
            Assert.Equal(419883, entry.GetProperty("stream_id").GetInt32());
            Assert.False(entry.TryGetProperty("resolved_tmdb_id", out _),
                "A resolved id we do not have must not appear as a null");
        }

        [Fact]
        public void AnUnusableResolvedIdIsTreatedAsAbsent()
        {
            // The lookup can hand back junk or a zero. Publishing that would give the reader a
            // key that looks usable and matches nothing.
            var config = PublishingConfig();

            MakeService().WriteWantedSet(
                config,
                new[] { Wanted(1, string.Empty, "0"), Wanted(2, string.Empty, "not-an-id") }.ToList(),
                catalogueComplete: true,
                reviewGateOn: true);

            foreach (var entry in ReadWantedSet().GetProperty("unidentified").EnumerateArray())
            {
                Assert.False(entry.TryGetProperty("resolved_tmdb_id", out _));
            }
        }

        [Fact]
        public void ATitleWithAProviderIdNeverCarriesAResolvedOne()
        {
            // The field exists only for titles the provider could not name. A title with a
            // provider id belongs in tmdb_ids and nowhere else.
            var config = PublishingConfig();

            MakeService().WriteWantedSet(
                config,
                new[] { Wanted(1, "603", "12345") }.ToList(),
                catalogueComplete: true,
                reviewGateOn: true);

            var root = ReadWantedSet();
            Assert.Equal(new[] { 603 }, TmdbIds(root));
            Assert.Empty(root.GetProperty("unidentified").EnumerateArray());
        }

        // -----------------------------------------------------------------
        // Republishing, and refusing to republish
        // -----------------------------------------------------------------

        [Fact]
        public async Task ASecondSyncReplacesTheFileInPlace()
        {
            // The file is a projection, recomputed in full: the second run's set must replace
            // the first's rather than merge with it, or a withdrawn title would never leave.
            var config = PublishingConfig();
            Handler.RespondWithSequence("get_vod_streams", new[]
            {
                VodStreamsJson(VodStream(streamId: 1, name: "First Movie", added: 1000, tmdbId: "603")),
                VodStreamsJson(VodStream(streamId: 2, name: "Second Movie", added: 1000, tmdbId: "27205")),
            });

            var svc = MakeService();
            await svc.SyncMoviesAsync(config, None, SaveConfig);
            Assert.Equal(new[] { 603 }, TmdbIds(ReadWantedSet()));

            // The second run's catalogue no longer carries the first title, so the republished
            // set must not still mention it.
            await svc.SyncMoviesAsync(config, None, SaveConfig);

            Assert.Equal(new[] { 27205 }, TmdbIds(ReadWantedSet()));
            Assert.False(File.Exists(PublishedFile + ".tmp"),
                "The temp file should have been renamed over the target, not left beside it");
        }

        [Fact]
        public async Task APartialCatalogueFetchLeavesThePreviousFileAlone()
        {
            // A set missing a whole category is indistinguishable from a smaller set the user
            // chose, and under-reporting demand is a silent instruction to do less work.
            // Leaving the old file ages its generated_at, which the consumer already acts on.
            var config = PublishingConfig();
            config.SelectedVodCategoryIds = new[] { 1, 2 };

            var categoryOne = VodStreamsJson(VodStream(streamId: 1, name: "Movie A", added: 1000, tmdbId: "603"));
            Handler.RespondWithSequence("category_id=1", new[] { categoryOne, categoryOne });

            // Rules are matched in registration order and each is consumed before the next is
            // reached, so category 2 answers the first sync and fails the second.
            Handler.RespondWith("category_id=2",
                VodStreamsJson(VodStream(streamId: 2, name: "Movie B", added: 1000, tmdbId: "27205")));
            Handler.RespondWith("category_id=2", "{}", HttpStatusCode.InternalServerError);

            var svc = MakeService();
            await svc.SyncMoviesAsync(config, None, SaveConfig);
            Assert.Equal(new[] { 603, 27205 }, TmdbIds(ReadWantedSet()));
            var published = File.ReadAllText(PublishedFile);

            await svc.SyncMoviesAsync(config, None, SaveConfig);

            // Byte-for-byte: the set that would have been written lists only category 1, so an
            // unchanged file is conclusive here rather than merely consistent.
            Assert.Equal(published, File.ReadAllText(PublishedFile));
        }
    }
}
