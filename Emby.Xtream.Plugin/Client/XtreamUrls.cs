using System;
using System.Globalization;

namespace Emby.Xtream.Plugin.Client
{
    /// <summary>
    /// Builds every Xtream URL the plugin requests or writes, so the username and password are
    /// escaped the same way everywhere. Commit 43d7b94 had to fix that escaping one call site at a
    /// time when each site built its own string.
    /// <para>
    /// The stream URL format must not change: a written <c>.strm</c> is compared against it to skip
    /// unchanged files, and <see cref="Service.StrmOwnership"/> recognises the plugin's files by it.
    /// </para>
    /// </summary>
    internal static class XtreamUrls
    {
        /// <summary><c>{base}/player_api.php?username=…&amp;password=…[&amp;action=…][&amp;name=value]</c></summary>
        internal static string PlayerApi(
            string baseUrl, string username, string password,
            string action = null, string paramName = null, object paramValue = null)
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}",
                baseUrl, Escape(username), Escape(password));
            if (!string.IsNullOrEmpty(action))
                url += "&action=" + action;
            if (!string.IsNullOrEmpty(paramName))
                url += "&" + paramName + "=" + Convert.ToString(paramValue, CultureInfo.InvariantCulture);
            return url;
        }

        internal static string PlayerApi(
            PluginConfiguration config, string action = null, string paramName = null, object paramValue = null)
        {
            return PlayerApi(config.BaseUrl, config.Username, config.Password, action, paramName, paramValue);
        }

        /// <summary><c>{base}/xmltv.php?username=…&amp;password=…</c>, the provider's full guide.</summary>
        internal static string Xmltv(PluginConfiguration config)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}/xmltv.php?username={1}&password={2}",
                config.BaseUrl, Escape(config.Username), Escape(config.Password));
        }

        /// <summary><c>{base}/{kind}/{user}/{pass}/{id}.{extension}</c>, kind being live, movie or series.</summary>
        internal static string Stream(PluginConfiguration config, string kind, object id, string extension)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}/{1}/{2}/{3}/{4}.{5}",
                config.BaseUrl, kind, Escape(config.Username), Escape(config.Password),
                Convert.ToString(id, CultureInfo.InvariantCulture), extension);
        }

        private static string Escape(string value) => Uri.EscapeDataString(value ?? string.Empty);
    }
}
