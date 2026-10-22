using Emby.Xtream.Plugin.Service;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    public class LogSanitizerTests
    {
        [Fact]
        public void RedactsIpAddresses()
        {
            var result = LogSanitizer.SanitizeLine(
                "Connected to 192.168.1.100 on port 8080", "", "", "", "");
            Assert.Equal("Connected to <ip-redacted> on port 8080", result);
        }

        [Fact]
        public void RedactsMultipleIps()
        {
            var result = LogSanitizer.SanitizeLine(
                "From 10.0.0.1 to 172.16.0.1", "", "", "", "");
            Assert.Equal("From <ip-redacted> to <ip-redacted>", result);
        }

        [Fact]
        public void RedactsLongerCredentialBeforeShorterOverlappingOne()
        {
            // Username "abc" is a prefix of password "abc/secret". Redacting the short one first
            // rewrites the long one out of existence before its own turn, and the "/secret" tail
            // survives into the downloadable log.
            var result = LogSanitizer.SanitizeLine(
                "auth abc/secret here", "abc", "abc/secret", "", "");

            Assert.DoesNotContain("secret", result);
        }

        [Fact]
        public void RedactsLongerCredentialBeforeShorterOverlappingOne_EscapedForm()
        {
            var result = LogSanitizer.SanitizeLine(
                "url http://h/movie/abc/abc%2Fsecret/1.mkv", "abc", "abc/secret", "", "");

            Assert.DoesNotContain("secret", result);
        }

        [Fact]
        public void ShortCredentialCannotSplitAnotherCredentialsEscapedForm()
        {
            // "%20" lands inside the escaped form of "p q" ("p%20q"). Replacing one value at a
            // time would split it, so the longer value stops matching and its readable characters
            // survive. One pass over all forms, longest first, cannot do that.
            var result = LogSanitizer.SanitizeLine(
                "url http://h/movie/u/p%20q/1.mkv", "%20", "p q", "", "");

            Assert.DoesNotContain("p%20q", result);
            Assert.DoesNotContain("%20q", result);
        }

        [Fact]
        public void RedactionMarkerIsNotRescannedByLaterValues()
        {
            // A credential that occurs inside the marker text must not chew up an earlier
            // redaction. A single pass never revisits what it already replaced.
            //
            // Assert the whole line: a Contains check would also pass on "<<redacted>>", which is
            // exactly the regression this test exists to catch.
            var result = LogSanitizer.SanitizeLine(
                "user bob logged in", "bob", "redacted", "", "");

            Assert.Equal("user <redacted> logged in", result);
        }

        [Fact]
        public void RedactsPercentEncodedCredentialsInUrls()
        {
            // Credentials go into generated URLs escaped, so the escaped form is what actually
            // appears in a logged stream URL. Redacting only the raw form leaks it into the log a
            // user attaches to a bug report.
            var result = LogSanitizer.SanitizeLine(
                "Playing http://host/movie/us%20er/p%2Fw/1.mkv", "us er", "p/w", "", "");

            Assert.DoesNotContain("us%20er", result);
            Assert.DoesNotContain("p%2Fw", result);
        }

        [Fact]
        public void RedactsRawCredentialsEvenWhenEscapedFormDiffers()
        {
            // The raw form still has to go: not every log line is a URL.
            var result = LogSanitizer.SanitizeLine(
                "Configured account us er / p/w", "us er", "p/w", "", "");

            Assert.DoesNotContain("us er", result);
            Assert.DoesNotContain("p/w", result);
        }

        [Fact]
        public void RedactsPercentEncodedDispatcharrCredentials()
        {
            var result = LogSanitizer.SanitizeLine(
                "Dispatcharr auth for d%20user", "", "", "d user", "d pass");

            Assert.DoesNotContain("d%20user", result);
        }

        [Fact]
        public void RedactsConfiguredUsername()
        {
            var result = LogSanitizer.SanitizeLine(
                "Login as myuser succeeded", "myuser", "mypass", "", "");
            Assert.Equal("Login as <redacted> succeeded", result);
        }

        [Fact]
        public void RedactsConfiguredPassword()
        {
            var result = LogSanitizer.SanitizeLine(
                "Using password s3cret123", "", "s3cret123", "", "");
            Assert.Equal("Using password <redacted>", result);
        }

        [Fact]
        public void RedactsDispatcharrCredentials()
        {
            var result = LogSanitizer.SanitizeLine(
                "Dispatcharr user=admin pass=hunter2",
                "", "", "admin", "hunter2");
            Assert.Equal("Dispatcharr user=<redacted> pass=<redacted>", result);
        }

        [Fact]
        public void RedactsEmailAddresses()
        {
            var result = LogSanitizer.SanitizeLine(
                "User email is user@example.com logged in", "", "", "", "");
            Assert.Equal("User email is <email-redacted> logged in", result);
        }

        [Fact]
        public void RedactsXtreamUrlCredentials()
        {
            var result = LogSanitizer.SanitizeLine(
                "Stream URL: http://example.com/live/john/pass123/12345.ts", "", "", "", "");
            Assert.Contains("/live/<user>/<pass>/", result);
            Assert.DoesNotContain("john", result);
            Assert.DoesNotContain("pass123", result);
        }

        [Fact]
        public void RedactsProviderHostname()
        {
            var result = LogSanitizer.SanitizeLine(
                "Fetching http://myprovider.com:8080/player_api.php?action=get_live", "", "", "", "");
            Assert.Contains("<provider-host>", result);
            Assert.DoesNotContain("myprovider.com", result);
        }

        [Fact]
        public void NoFalsePositivesOnNormalText()
        {
            const string line = "XtreamTuner loaded 150 channels successfully";
            var result = LogSanitizer.SanitizeLine(line, "", "", "", "");
            Assert.Equal(line, result);
        }

        [Fact]
        public void EmptyCredentialsSkipped()
        {
            const string line = "Normal log line with no secrets";
            var result = LogSanitizer.SanitizeLine(line, "", "", null, null);
            Assert.Equal(line, result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void HandlesNullOrEmptyLine(string input)
        {
            Assert.Equal(input, LogSanitizer.SanitizeLine(input, "user", "pass", "du", "dp"));
        }

        [Fact]
        public void RedactsProviderHostWithoutPort()
        {
            var result = LogSanitizer.SanitizeLine(
                "Fetching http://myprovider.com/player_api.php?action=get_live", "", "", "", "");
            Assert.Contains("<provider-host>", result);
            Assert.DoesNotContain("myprovider.com", result);
        }

        [Theory]
        [InlineData("http://host.com/movie/123.mp4", "/movie/")]
        [InlineData("http://host.com/series/456.mp4", "/series/")]
        public void RedactsMovieAndSeriesUrlPaths(string input, string pathKept)
        {
            var result = LogSanitizer.SanitizeLine(input, "", "", "", "");
            Assert.Contains("<provider-host>", result);
            Assert.Contains(pathKept, result);
            Assert.DoesNotContain("host.com", result);
        }

        [Fact]
        public void RedactsHttpsUrls()
        {
            var result = LogSanitizer.SanitizeLine(
                "Fetching https://secure.provider.com:443/live/user1/pass1/123.ts", "", "", "", "");
            Assert.Contains("<provider-host>", result);
            Assert.DoesNotContain("secure.provider.com", result);
        }

        [Fact]
        public void MultiplePiiTypesInOneLine()
        {
            var result = LogSanitizer.SanitizeLine(
                "User myuser at 10.0.0.1 sent email test@example.com via http://host.com/live/u/p/1.ts",
                "myuser", "mypass", "", "");
            Assert.DoesNotContain("myuser", result);
            Assert.DoesNotContain("10.0.0.1", result);
            Assert.DoesNotContain("test@example.com", result);
            Assert.DoesNotContain("host.com", result);
        }

        [Theory]
        [InlineData("Loading Plugin, Version=1.2.0.0, Culture=neutral")]
        [InlineData("File Emby.dll has version 4.8.0.80")]
        public void PreservesVersionNumbers(string input)
        {
            var result = LogSanitizer.SanitizeLine(input, "", "", "", "");
            Assert.Equal(input, result);
        }

        [Fact]
        public void CredentialSubstringReplacesPartialWords()
        {
            // string.Replace matches substrings — documents this known behavior
            var result = LogSanitizer.SanitizeLine(
                "the password field is empty", "", "pass", "", "");
            Assert.Equal("the <redacted>word field is empty", result);
        }

        // Emby request lines carry the session token in the query string, with the value wrapped
        // in zero-width non-joiners. These reached the "sanitized" download unredacted.
        private const string FakeToken = "0123456789abcdef0123456789abcdef";

        [Fact]
        public void EmbyRequestLine_TokenInQueryString_IsRedacted()
        {
            var line = "2026-03-01 10:00:00.000 Debug Server: http/1.1 GET http://host:8096/emby/LiveTV/Folder"
                + "?X-Emby-Client=Emby%20Web&X-Emby-Token=\u200c" + FakeToken + "\u200c&X-Emby-Language=en-us. Time: 2ms";

            var result = LogSanitizer.SanitizeLine(line, "user", "pass", null, null);

            Assert.DoesNotContain(FakeToken, result);
            Assert.Contains("X-Emby-Token=<token-redacted>", result);
            Assert.Contains("X-Emby-Language=en-us", result);
        }

        [Theory]
        [InlineData("GET /emby/Items?api_key=" + FakeToken + "&Limit=10")]
        [InlineData("GET /emby/Items?X-MediaBrowser-Token=" + FakeToken)]
        [InlineData("token " + FakeToken + " in some other form")]
        public void EmbyToken_InOtherForms_IsRedacted(string line)
        {
            var result = LogSanitizer.SanitizeLine(line, null, null, null, null);

            Assert.DoesNotContain(FakeToken, result);
        }

        [Fact]
        public void Bare32Hex_WrappedInZeroWidthCharacters_IsRedacted()
        {
            // UserId is not one of the named token parameters, so only the backstop sees it. Emby
            // wraps it in zero-width non-joiners, which .NET treats as word characters.
            var line = "GET /emby/Users/x?UserId=\u200c" + FakeToken + "\u200c&Fields=Name";

            var result = LogSanitizer.SanitizeLine(line, null, null, null, null);

            Assert.DoesNotContain(FakeToken, result);
        }

        [Fact]
        public void AuthorizationHeaderToken_IsRedacted_WhateverItsLength()
        {
            // Not 32 hex, so only the header rule can catch it.
            var line = "X-Emby-Authorization: MediaBrowser Client=\"Emby Web\", Token=\"short-token-value\"";

            var result = LogSanitizer.SanitizeLine(line, null, null, null, null);

            Assert.DoesNotContain("short-token-value", result);
            Assert.Contains("Token=\"<token-redacted>\"", result);
        }

        [Fact]
        public void WordsEndingInToken_AreLeftAlone()
        {
            var line = "Refreshing Dispatcharr JWT; cancellationToken was not cancelled";

            Assert.Equal(line, LogSanitizer.SanitizeLine(line, null, null, null, null));
        }
    }
}
