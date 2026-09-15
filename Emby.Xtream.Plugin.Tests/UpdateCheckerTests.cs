using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class UpdateCheckerTests
    {
        [Fact]
        public void NewerVersionAvailable()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "v1.1.0", "https://github.com/release", "Bug fixes", "2025-01-01");
            Assert.True(result.UpdateAvailable);
            Assert.Equal("1.0.0", result.CurrentVersion);
            Assert.Equal("1.1.0", result.LatestVersion);
            Assert.Equal("https://github.com/release", result.ReleaseUrl);
            Assert.Equal("Bug fixes", result.ReleaseNotes);
        }

        [Fact]
        public void SameVersionNotAvailable()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "v1.0.0", "https://github.com/release", "", "");
            Assert.False(result.UpdateAvailable);
            Assert.Equal("1.0.0", result.LatestVersion);
        }

        [Fact]
        public void OlderVersionNotAvailable()
        {
            var result = UpdateChecker.CompareVersions("2.0.0", "v1.5.0", "https://github.com/release", "", "");
            Assert.False(result.UpdateAvailable);
        }

        [Fact]
        public void StripLeadingV()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "v1.2.3", "", "", "");
            Assert.True(result.UpdateAvailable);
            Assert.Equal("1.2.3", result.LatestVersion);
        }

        [Fact]
        public void StripLeadingUpperV()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "V2.0.0", "", "", "");
            Assert.True(result.UpdateAvailable);
            Assert.Equal("2.0.0", result.LatestVersion);
        }

        [Fact]
        public void TagWithoutPrefix()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "1.2.0", "", "", "");
            Assert.True(result.UpdateAvailable);
            Assert.Equal("1.2.0", result.LatestVersion);
        }

        [Fact]
        public void MalformedTagReturnsError()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "not-a-version", "", "", "");
            Assert.False(result.UpdateAvailable);
            Assert.False(string.IsNullOrEmpty(result.Error));
            Assert.Contains("Could not parse", result.Error);
        }

        [Fact]
        public void NullTagReturnsError()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", null, "", "", "");
            Assert.False(result.UpdateAvailable);
            Assert.Contains("No tag found", result.Error);
        }

        [Fact]
        public void EmptyTagReturnsError()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "", "", "", "");
            Assert.False(result.UpdateAvailable);
            Assert.Contains("No tag found", result.Error);
        }

        [Fact]
        public void ThreePartVersionComparison()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "v1.0.1", "", "", "");
            Assert.True(result.UpdateAvailable);
        }

        [Fact]
        public void FourPartVersionComparison()
        {
            var result = UpdateChecker.CompareVersions("1.0.0.0", "v1.0.0.1", "", "", "");
            Assert.True(result.UpdateAvailable);
        }

        [Fact]
        public void MajorVersionBump()
        {
            var result = UpdateChecker.CompareVersions("1.9.9", "v2.0.0", "", "", "");
            Assert.True(result.UpdateAvailable);
        }

        [Fact]
        public void CurrentVersionHigherMajor()
        {
            var result = UpdateChecker.CompareVersions("3.0.0", "v2.9.9", "", "", "");
            Assert.False(result.UpdateAvailable);
        }

        [Fact]
        public void PreservesReleaseUrlAndNotes()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "v1.0.0",
                "https://github.com/org/repo/releases/tag/v1.0.0",
                "Release notes here",
                "2025-06-15T10:00:00Z");
            Assert.Equal("https://github.com/org/repo/releases/tag/v1.0.0", result.ReleaseUrl);
            Assert.Equal("Release notes here", result.ReleaseNotes);
            Assert.Equal("2025-06-15T10:00:00Z", result.PublishedAt);
        }

        [Fact]
        public void NullReleaseFieldsDefaultToEmpty()
        {
            var result = UpdateChecker.CompareVersions("1.0.0", "v1.0.0", null, null, null);
            Assert.Equal("", result.ReleaseUrl);
            Assert.Equal("", result.ReleaseNotes);
            Assert.Equal("", result.PublishedAt);
        }

        [Fact]
        public void TwoPartVersionParsedCorrectly()
        {
            var result = UpdateChecker.CompareVersions("1.0", "v1.1", "", "", "");
            Assert.True(result.UpdateAvailable);
        }

        // ---- ExtractDllDownloadUrl tests ----

        [Fact]
        public void ExtractDllDownloadUrl_FindsCorrectAsset()
        {
            var json = @"{
                ""tag_name"": ""v1.2.0"",
                ""assets"": [
                    {
                        ""name"": ""source.zip"",
                        ""browser_download_url"": ""https://github.com/example/source.zip""
                    },
                    {
                        ""name"": ""Emby.Xtream.Plugin.dll"",
                        ""browser_download_url"": ""https://github.com/example/Emby.Xtream.Plugin.dll""
                    },
                    {
                        ""name"": ""README.md"",
                        ""browser_download_url"": ""https://github.com/example/README.md""
                    }
                ]
            }";

            var url = UpdateChecker.ExtractDllDownloadUrl(json, "Emby.Xtream.Plugin.dll");
            Assert.Equal("https://github.com/example/Emby.Xtream.Plugin.dll", url);
        }

        [Fact]
        public void ExtractDllDownloadUrl_ReturnsNullWhenNoMatch()
        {
            var json = @"{
                ""tag_name"": ""v1.2.0"",
                ""assets"": [
                    {
                        ""name"": ""source.zip"",
                        ""browser_download_url"": ""https://github.com/example/source.zip""
                    }
                ]
            }";

            var url = UpdateChecker.ExtractDllDownloadUrl(json, "Emby.Xtream.Plugin.dll");
            Assert.Null(url);
        }

        [Fact]
        public void ExtractDllDownloadUrl_ReturnsNullWhenAssetsEmpty()
        {
            var json = @"{
                ""tag_name"": ""v1.2.0"",
                ""assets"": []
            }";

            var url = UpdateChecker.ExtractDllDownloadUrl(json, "Emby.Xtream.Plugin.dll");
            Assert.Null(url);
        }

        [Fact]
        public void ExtractDllDownloadUrl_CaseInsensitiveMatch()
        {
            var json = @"{
                ""assets"": [
                    {
                        ""name"": ""emby.xtream.plugin.dll"",
                        ""browser_download_url"": ""https://github.com/example/plugin.dll""
                    }
                ]
            }";

            var url = UpdateChecker.ExtractDllDownloadUrl(json, "Emby.Xtream.Plugin.dll");
            Assert.Equal("https://github.com/example/plugin.dll", url);
        }

        [Fact]
        public void ExtractDllDownloadUrl_ReturnsNullForNullJson()
        {
            var url = UpdateChecker.ExtractDllDownloadUrl(null, "Emby.Xtream.Plugin.dll");
            Assert.Null(url);
        }

        [Fact]
        public void ExtractDllDownloadUrl_ReturnsNullForNullAssetName()
        {
            var json = @"{ ""assets"": [] }";
            var url = UpdateChecker.ExtractDllDownloadUrl(json, null);
            Assert.Null(url);
        }

        // ---- Asset selection tests (item 29: the updater must not downgrade a 4.10 server) ----

        /// <summary>
        /// A release as published today: both builds, 4.9 first, exactly as release.yml uploads them.
        /// </summary>
        private const string DualAssetReleaseJson = @"{
            ""tag_name"": ""dedupe-v1.7.0"",
            ""assets"": [
                {
                    ""name"": ""Emby.Xtream.Plugin.dll"",
                    ""browser_download_url"": ""https://example.invalid/Emby.Xtream.Plugin.dll""
                },
                {
                    ""name"": ""Emby.Xtream.Plugin-4.10.dll"",
                    ""browser_download_url"": ""https://example.invalid/Emby.Xtream.Plugin-4.10.dll""
                }
            ]
        }";

        /// <summary>A release from before the dual build: the 4.9 asset only.</summary>
        private const string SingleAssetReleaseJson = @"{
            ""tag_name"": ""dedupe-v1.1.0"",
            ""assets"": [
                {
                    ""name"": ""Emby.Xtream.Plugin.dll"",
                    ""browser_download_url"": ""https://example.invalid/Emby.Xtream.Plugin.dll""
                }
            ]
        }";

        [Theory]
        // Emby 4.9 — the plain asset, unchanged behaviour.
        [InlineData("4.9.5.0", "Emby.Xtream.Plugin.dll")]
        [InlineData("4.8.11.0", "Emby.Xtream.Plugin.dll")]
        // The SDK floor is inclusive, and GA is well past it.
        [InlineData("4.10.0.17", "Emby.Xtream.Plugin-4.10.dll")]
        [InlineData("4.10.0.40", "Emby.Xtream.Plugin-4.10.dll")]
        [InlineData("4.10.1.0", "Emby.Xtream.Plugin-4.10.dll")]
        [InlineData("5.0.0.0", "Emby.Xtream.Plugin-4.10.dll")]
        // A 4.10 beta below the floor is not what the 4.10 build targets.
        [InlineData("4.10.0.16", "Emby.Xtream.Plugin.dll")]
        // Unreadable version: fall back to the asset every release has always published.
        [InlineData(null, "Emby.Xtream.Plugin.dll")]
        [InlineData("", "Emby.Xtream.Plugin.dll")]
        [InlineData("   ", "Emby.Xtream.Plugin.dll")]
        [InlineData("not-a-version", "Emby.Xtream.Plugin.dll")]
        public void SelectDllAssetName_PicksBuildForServerVersion(string appVersion, string expected)
        {
            Assert.Equal(expected, UpdateChecker.SelectDllAssetName(appVersion));
        }

        [Fact]
        public void ApplyAssetSelection_Emby410_TakesThe410Asset()
        {
            var result = new UpdateCheckResult();
            UpdateChecker.ApplyAssetSelection(result, DualAssetReleaseJson, "4.10.0.40");

            Assert.Equal("Emby.Xtream.Plugin-4.10.dll", result.AssetName);
            Assert.Equal("https://example.invalid/Emby.Xtream.Plugin-4.10.dll", result.DownloadUrl);
            Assert.Null(result.AssetNote);
        }

        [Fact]
        public void ApplyAssetSelection_Emby49_TakesThePlainAsset()
        {
            var result = new UpdateCheckResult();
            UpdateChecker.ApplyAssetSelection(result, DualAssetReleaseJson, "4.9.5.0");

            Assert.Equal("Emby.Xtream.Plugin.dll", result.AssetName);
            Assert.Equal("https://example.invalid/Emby.Xtream.Plugin.dll", result.DownloadUrl);
            Assert.Null(result.AssetNote);
        }

        /// <summary>
        /// The bug this whole change exists for: before the fix a 4.10 server was handed the 4.9
        /// URL out of exactly this JSON. Pin that the two assets are told apart.
        /// </summary>
        [Fact]
        public void ApplyAssetSelection_TheTwoAssetsAreNotInterchangeable()
        {
            var forFourTen = new UpdateCheckResult();
            UpdateChecker.ApplyAssetSelection(forFourTen, DualAssetReleaseJson, "4.10.0.40");

            var forFourNine = new UpdateCheckResult();
            UpdateChecker.ApplyAssetSelection(forFourNine, DualAssetReleaseJson, "4.9.5.0");

            Assert.NotEqual(forFourNine.DownloadUrl, forFourTen.DownloadUrl);
        }

        /// <summary>
        /// A release predating the dual build offers a 4.10 server nothing rather than the 4.9
        /// build. Offering it would let one click replace a working plugin with one that cannot
        /// load — the exact outcome this whole change exists to prevent, arrived at politely.
        /// </summary>
        [Fact]
        public void ApplyAssetSelection_Emby410_RefusesAReleaseCarryingOnlyThe49Build()
        {
            var result = new UpdateCheckResult();
            UpdateChecker.ApplyAssetSelection(result, SingleAssetReleaseJson, "4.10.0.40");

            Assert.Null(result.DownloadUrl);
            Assert.Equal("Emby.Xtream.Plugin-4.10.dll", result.AssetName);
            Assert.False(string.IsNullOrEmpty(result.AssetNote));
        }

        /// <summary>
        /// Guards the refusal against the obvious regression: reintroducing a fallback would show
        /// up here as the 4.9 URL appearing, note or no note.
        /// </summary>
        [Fact]
        public void ApplyAssetSelection_Emby410_NeverYieldsThe49Url()
        {
            var result = new UpdateCheckResult();
            UpdateChecker.ApplyAssetSelection(result, SingleAssetReleaseJson, "4.10.0.40");

            Assert.NotEqual("https://example.invalid/Emby.Xtream.Plugin.dll", result.DownloadUrl);
        }

        /// <summary>
        /// A release with no assets at all is just broken — there is nothing being withheld, so no
        /// note is owed. Keeps the note meaning one specific thing.
        /// </summary>
        [Fact]
        public void ApplyAssetSelection_NoAssetsAtAll_LeavesNothingToInstallAndSaysNothing()
        {
            var result = new UpdateCheckResult();
            UpdateChecker.ApplyAssetSelection(result, @"{ ""assets"": [] }", "4.10.0.40");

            Assert.Null(result.DownloadUrl);
            Assert.Null(result.AssetNote);
        }

        [Fact]
        public void ApplyAssetSelection_NullResultIsIgnored()
        {
            UpdateChecker.ApplyAssetSelection(null, DualAssetReleaseJson, "4.10.0.40");
        }

        // ---- ExtractFirstRelease tests ----

        [Fact]
        public void ExtractFirstRelease_ReturnsFirstObject()
        {
            var json = @"[
                {""tag_name"":""v1.2.0"",""prerelease"":true,""assets"":[]},
                {""tag_name"":""v1.1.0"",""prerelease"":false,""assets"":[]}
            ]";
            var first = UpdateChecker.ExtractFirstRelease(json);
            Assert.Contains("v1.2.0", first);
            Assert.DoesNotContain("v1.1.0", first);
        }

        [Fact]
        public void ExtractFirstRelease_EmptyArray()
        {
            var json = @"[]";
            var first = UpdateChecker.ExtractFirstRelease(json);
            Assert.Equal("{}", first);
        }

        [Fact]
        public void ExtractFirstRelease_NullInput()
        {
            var first = UpdateChecker.ExtractFirstRelease(null);
            Assert.Equal("{}", first);
        }

        [Fact]
        public void ExtractFirstRelease_EmptyInput()
        {
            var first = UpdateChecker.ExtractFirstRelease("");
            Assert.Equal("{}", first);
        }

        // ---- ExtractJsonBool tests ----

        [Fact]
        public void ExtractJsonBool_True()
        {
            var json = @"{""prerelease"": true, ""draft"": false}";
            Assert.True(UpdateChecker.ExtractJsonBool(json, "prerelease"));
        }

        [Fact]
        public void ExtractJsonBool_False()
        {
            var json = @"{""prerelease"": false}";
            Assert.False(UpdateChecker.ExtractJsonBool(json, "prerelease"));
        }

        [Fact]
        public void ExtractJsonBool_Missing()
        {
            var json = @"{""tag_name"": ""v1.0.0""}";
            Assert.False(UpdateChecker.ExtractJsonBool(json, "prerelease"));
        }
    }
}
