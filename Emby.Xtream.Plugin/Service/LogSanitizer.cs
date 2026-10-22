using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Emby.Xtream.Plugin.Service
{
    public static class LogSanitizer
    {
        private static readonly Regex IpRegex = new Regex(
            @"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}",
            RegexOptions.Compiled);

        private static readonly Regex VersionContextRegex = new Regex(
            @"(?:Version[= ]|version )\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}",
            RegexOptions.Compiled);

        private static readonly Regex XtreamCredRegex = new Regex(
            @"/live/[^/]+/[^/]+/",
            RegexOptions.Compiled);

        private static readonly Regex EmailRegex = new Regex(
            @"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}",
            RegexOptions.Compiled);

        // Emby writes its own request lines to the same log, and a client that sends its access
        // token in the query string gets it logged: "...&X-Emby-Token=<32 hex>&...". Those lines
        // match the "LiveTv" keyword, so without this the "sanitized" download carried live Emby
        // session tokens that work as admin on a reachable server. Emby wraps logged values in
        // zero-width characters, which [^&\s"'] keeps inside the redacted value.
        private static readonly Regex TokenParamRegex = new Regex(
            @"(?i)\b(x-emby-token|x-mediabrowser-token|api_key|apikey|access_?token|x-emby-device-id|deviceid)=[^&\s""']+",
            RegexOptions.Compiled);

        // The same token in an authorization header: Token="<value>".
        private static readonly Regex TokenHeaderRegex = new Regex(
            @"(?i)\b(token|x-emby-token|x-mediabrowser-token)(\s*[:=]\s*)""[^""]*""",
            RegexOptions.Compiled);

        // Backstop for a token in a form the rules above miss: Emby tokens and API keys are
        // 32 hex characters. Also hides Emby user and item IDs, which a bug report can do without.
        // Explicit lookarounds rather than \b: .NET counts the zero-width characters Emby wraps
        // values in as word characters, so \b never fires next to them.
        private static readonly Regex HexTokenRegex = new Regex(
            @"(?<![0-9A-Za-z])[0-9a-fA-F]{32}(?![0-9A-Za-z])",
            RegexOptions.Compiled);

        private static readonly Regex ProviderHostRegex = new Regex(
            @"(https?://)([^/:]+)(:\d+)?(/player_api\.php|/live/|/movie/|/series/)",
            RegexOptions.Compiled);

        /// <summary>
        /// Sanitizes a single log line by redacting PII: known credentials, Emby access tokens,
        /// IP addresses, Xtream URL credentials, emails, and provider hostnames.
        /// </summary>
        public static string SanitizeLine(string line,
            string username, string password,
            string dispatcharrUser, string dispatcharrPass)
        {
            if (string.IsNullOrEmpty(line)) return line;

            var s = line;

            s = RedactCredentials(s, username, password, dispatcharrUser, dispatcharrPass);

            // Emby access tokens and API keys, before anything else can split them.
            s = TokenParamRegex.Replace(s, "$1=<token-redacted>");
            s = TokenHeaderRegex.Replace(s, "$1$2\"<token-redacted>\"");
            s = HexTokenRegex.Replace(s, "<hex-redacted>");

            // Redact IP addresses, but preserve version numbers (e.g. Version=1.2.0.0)
            // Replace version patterns with placeholders first, then redact IPs, then restore
            var versionMatches = VersionContextRegex.Matches(s);
            for (int i = versionMatches.Count - 1; i >= 0; i--)
            {
                var vm = versionMatches[i];
                s = s.Substring(0, vm.Index) + "\x00VER" + i + "\x00" + s.Substring(vm.Index + vm.Length);
            }
            s = IpRegex.Replace(s, "<ip-redacted>");
            for (int i = 0; i < versionMatches.Count; i++)
            {
                s = s.Replace("\x00VER" + i + "\x00", versionMatches[i].Value);
            }

            // Redact Xtream credentials in URLs: /live/user/pass/
            s = XtreamCredRegex.Replace(s, "/live/<user>/<pass>/");

            // Redact email patterns
            s = EmailRegex.Replace(s, "<email-redacted>");

            // Redact hostnames in stream URLs
            s = ProviderHostRegex.Replace(s, "$1<provider-host>$3$4");

            return s;
        }

        /// <summary>
        /// Redacts every configured credential from <paramref name="line"/>, in both its raw and
        /// percent-encoded form.
        /// </summary>
        /// <remarks>
        /// Credentials go into generated URLs escaped, so a password of <c>p/w</c> appears in a
        /// stream URL as <c>p%2Fw</c>. Redacting only one form leaks the other into the log a user
        /// attaches to a bug report.
        ///
        /// Every form of every credential goes into one alternation, longest first, applied in a
        /// single pass. Replacing them one at a time is not safe no matter how the credentials are
        /// ordered: a short value can land inside another value's escaped form and split it, which
        /// both stops the longer one from matching and leaves readable pieces of it behind. A
        /// single pass also cannot match inside text it has already replaced.
        /// </remarks>
        private static string RedactCredentials(string line, params string[] values)
        {
            var candidates = values
                .Where(v => !string.IsNullOrEmpty(v))
                .SelectMany(v => new[] { v, TryEscape(v) })
                .Where(v => !string.IsNullOrEmpty(v))
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(v => v.Length)
                .ToList();

            if (candidates.Count == 0)
            {
                return line;
            }

            // Alternation is leftmost-first, so listing longest first makes the longest candidate
            // win at any position where several could match.
            var pattern = string.Join("|", candidates.Select(Regex.Escape));
            return Regex.Replace(line, pattern, "<redacted>");
        }

        private static string TryEscape(string value)
        {
            try
            {
                return Uri.EscapeDataString(value);
            }
            catch (UriFormatException)
            {
                // Nothing to add for a value the escaper rejects; the raw form is still covered.
                return null;
            }
        }
    }
}
