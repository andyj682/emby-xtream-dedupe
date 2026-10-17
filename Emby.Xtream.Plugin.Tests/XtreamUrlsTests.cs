using System.Globalization;
using Emby.Xtream.Plugin.Client;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// Every Xtream URL goes through <see cref="XtreamUrls"/>. The stream URL text is written into
    /// .strm files and compared on the next sync, so these pin it to the exact format it had when
    /// each call site built it by hand: any change rewrites every file in the library.
    /// </summary>
    public class XtreamUrlsTests
    {
        private static PluginConfiguration Config() => new PluginConfiguration
        {
            BaseUrl = "http://provider.example:8080",
            Username = "user name",
            Password = "p&ss/word",
        };

        [Fact]
        public void PlayerApi_EscapesCredentials_AndAddsActionAndParameter()
        {
            Assert.Equal(
                "http://provider.example:8080/player_api.php?username=user%20name&password=p%26ss%2Fword&action=get_vod_streams&category_id=12",
                XtreamUrls.PlayerApi(Config(), "get_vod_streams", "category_id", 12));
        }

        [Fact]
        public void PlayerApi_WithoutAction_IsTheLoginUrl()
        {
            Assert.Equal(
                "http://provider.example:8080/player_api.php?username=user%20name&password=p%26ss%2Fword",
                XtreamUrls.PlayerApi("http://provider.example:8080", "user name", "p&ss/word"));
        }

        [Fact]
        public void PlayerApi_NullCredentials_AreEmpty()
        {
            Assert.Equal(
                "http://x/player_api.php?username=&password=&action=get_live_categories",
                XtreamUrls.PlayerApi("http://x", null, null, "get_live_categories"));
        }

        [Theory]
        [InlineData("movie", 101, "mkv")]
        [InlineData("series", 202, "mp4")]
        [InlineData("live", 303, "ts")]
        public void Stream_MatchesTheFormatWrittenToStrmFiles(string kind, int id, string ext)
        {
            var c = Config();
            // The exact expression the call sites used before XtreamUrls existed.
            var before = string.Format(CultureInfo.InvariantCulture, "{0}/" + kind + "/{1}/{2}/{3}.{4}",
                c.BaseUrl, System.Uri.EscapeDataString(c.Username ?? string.Empty),
                System.Uri.EscapeDataString(c.Password ?? string.Empty), id, ext);

            Assert.Equal(before, XtreamUrls.Stream(c, kind, id, ext));
        }
    }
}
