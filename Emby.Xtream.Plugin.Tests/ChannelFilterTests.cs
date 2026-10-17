using System.Collections.Generic;
using System.Linq;
using Emby.Xtream.Plugin.Client.Models;
using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// When the normal channel fetch fails the tuner fetches every channel in one request. That
    /// fallback used to skip the user's filters, so one failed request could show adult channels
    /// and categories the user had switched off.
    /// </summary>
    public class ChannelFilterTests
    {
        private static readonly List<LiveStreamInfo> Channels = new List<LiveStreamInfo>
        {
            new LiveStreamInfo { StreamId = 1, CategoryId = 10 },
            new LiveStreamInfo { StreamId = 2, CategoryId = 20 },
            new LiveStreamInfo { StreamId = 3, CategoryId = 10, IsAdult = 1 },
            new LiveStreamInfo { StreamId = 4, CategoryId = null },
        };

        [Fact]
        public void Fallback_KeepsOnlySelectedCategories_AndDropsAdultChannels()
        {
            var config = new PluginConfiguration { SelectedLiveCategoryIds = new[] { 10 }, IncludeAdultChannels = false };

            var result = LiveTvService.ApplyChannelFilters(Channels, config);

            Assert.Equal(new[] { 1 }, result.Select(c => c.StreamId));
        }

        [Fact]
        public void NoCategoriesSelected_MeansAllCategories()
        {
            var config = new PluginConfiguration { SelectedLiveCategoryIds = new int[0], IncludeAdultChannels = true };

            var result = LiveTvService.ApplyChannelFilters(Channels, config);

            Assert.Equal(new[] { 1, 2, 3, 4 }, result.Select(c => c.StreamId));
        }

        [Fact]
        public void NormalFetch_DoesNotFilterCategoriesAgain()
        {
            // The normal fetch requested category 10 already; a channel the provider tags
            // differently in that response must not be dropped a second time.
            var config = new PluginConfiguration { SelectedLiveCategoryIds = new[] { 10 }, IncludeAdultChannels = false };

            var result = LiveTvService.ApplyChannelFilters(Channels, config, filterCategories: false);

            Assert.Equal(new[] { 1, 2, 4 }, result.Select(c => c.StreamId));
        }
    }
}
