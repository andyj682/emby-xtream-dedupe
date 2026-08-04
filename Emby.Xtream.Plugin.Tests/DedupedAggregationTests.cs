using System.Collections.Generic;
using System.Linq;
using Emby.Xtream.Plugin.Api;
using Emby.Xtream.Plugin.Client.Models;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Unit tests for the de-duplicated listing aggregation
    /// (<see cref="XtreamTunerApi.AggregateVodByStreamId"/> /
    /// <see cref="XtreamTunerApi.AggregateSeriesByName"/>). The HTTP fetch is exercised
    /// elsewhere; these cover the grouping the de-dup review view depends on.
    /// </summary>
    public class DedupedAggregationTests
    {
        private static KeyValuePair<int, List<VodStreamInfo>> VodCat(int catId, params VodStreamInfo[] streams) =>
            new KeyValuePair<int, List<VodStreamInfo>>(catId, streams.ToList());

        private static VodStreamInfo Vod(int id, string name) =>
            new VodStreamInfo { StreamId = id, Name = name };

        private static KeyValuePair<int, List<SeriesInfo>> SeriesCat(int catId, params SeriesInfo[] series) =>
            new KeyValuePair<int, List<SeriesInfo>>(catId, series.ToList());

        private static SeriesInfo Series(int id, string name) =>
            new SeriesInfo { SeriesId = id, Name = name };

        // ---- VOD: group by StreamId (a movie shares one id across categories) ----

        [Fact]
        public void Vod_CrossListedMovie_SharesOneIdAcrossCategories()
        {
            var result = XtreamTunerApi.AggregateVodByStreamId(new[]
            {
                VodCat(7, Vod(100, "Movie A")),
                VodCat(58, Vod(100, "Movie A")),
            });

            var dto = Assert.Single(result);
            Assert.Equal("Movie A", dto.Name);
            Assert.Equal(new[] { 100 }, dto.Ids);
            Assert.Equal(new[] { 7, 58 }, dto.Categories.OrderBy(c => c).ToArray());
        }

        [Fact]
        public void Vod_DistinctMovies_ProduceSeparateEntriesSortedByName()
        {
            var result = XtreamTunerApi.AggregateVodByStreamId(new[]
            {
                VodCat(7, Vod(2, "Banana"), Vod(1, "Apple")),
            });

            Assert.Equal(2, result.Count);
            Assert.Equal("Apple", result[0].Name);   // output is sorted by name
            Assert.Equal("Banana", result[1].Name);
            Assert.Equal(new[] { 1 }, result[0].Ids);
            Assert.Equal(new[] { 2 }, result[1].Ids);
        }

        // ---- Series: group by (trimmed, case-insensitive) name ----

        [Fact]
        public void Series_SameNameDifferentIds_MergeIntoOneTitle()
        {
            var result = XtreamTunerApi.AggregateSeriesByName(new[]
            {
                SeriesCat(1, Series(10, "My Drama")),
                SeriesCat(2, Series(20, "My Drama")),
            });

            var dto = Assert.Single(result);
            Assert.Equal("My Drama", dto.Name);
            Assert.Equal(new[] { 10, 20 }, dto.Ids.OrderBy(i => i).ToArray());
            Assert.Equal(new[] { 1, 2 }, dto.Categories.OrderBy(c => c).ToArray());
        }

        [Fact]
        public void Series_NameMatchIsCaseAndWhitespaceInsensitive()
        {
            var result = XtreamTunerApi.AggregateSeriesByName(new[]
            {
                SeriesCat(1, Series(10, "My Drama")),
                SeriesCat(2, Series(20, "  my drama ")),
            });

            var dto = Assert.Single(result);
            Assert.Equal(new[] { 10, 20 }, dto.Ids.OrderBy(i => i).ToArray());
        }

        [Fact]
        public void Series_BlankNames_DoNotCollapseTogether()
        {
            // Unnamed entries fall back to a per-id key so they stay separate instead of
            // all merging into one blank-named group.
            var result = XtreamTunerApi.AggregateSeriesByName(new[]
            {
                SeriesCat(1, Series(10, string.Empty), Series(11, "   ")),
            });

            Assert.Equal(2, result.Count);
            Assert.Equal(new[] { 10, 11 }, result.SelectMany(d => d.Ids).OrderBy(i => i).ToArray());
        }
    }
}
