using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Collects the plugin's lines from Emby's log folder for the sanitized log download.
    /// </summary>
    internal static class PluginLogCollector
    {
        private static readonly string[] Keywords = { "XtreamTuner", "Xtream", "Dispatcharr", "LiveTv" };

        internal const int MaxFiles = 5;

        /// <summary>
        /// Emby's own server logs come first and ffmpeg logs last. The folder also holds an ffmpeg log per stream or
        /// transcode, and picking the newest files of any kind let those crowd the server log out,
        /// so the download came back empty (#86).
        /// </summary>
        internal static IReadOnlyList<string> SelectLogFiles(string logDir)
        {
            var all = Directory.GetFiles(logDir, "*.txt")
                .Concat(Directory.GetFiles(logDir, "*.log"))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();

            // Server logs, then anything else, then ffmpeg logs; newest first within each.
            return all
                .OrderBy(f => Rank(Path.GetFileName(f)))
                .Take(MaxFiles)
                .ToList();
        }

        private static int Rank(string fileName)
        {
            if (fileName.StartsWith("embyserver", StringComparison.OrdinalIgnoreCase)) return 0;
            if (fileName.StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase)) return 2;
            return 1;
        }

        /// <summary>
        /// Returns the matching lines, plus a note for each file that could not be read, so a
        /// failure shows in the download instead of silently leaving it empty.
        /// </summary>
        internal static List<string> Collect(string logDir)
        {
            var lines = new List<string>();
            foreach (var logFile in SelectLogFiles(logDir))
            {
                try
                {
                    // Emby holds the current log open for writing. Without FileShare.ReadWrite
                    // opening it fails on Windows, and that is the file that matters most.
                    using (var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(fs, Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (Keywords.Any(kw => line.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0))
                                lines.Add(line);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    lines.Add("[log download] Could not read " + Path.GetFileName(logFile) + ": " + ex.GetType().Name);
                }
            }

            return lines;
        }
    }
}
