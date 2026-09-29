using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Emby.Xtream.Plugin.Client.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using STJ = System.Text.Json;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// The parts of the sync that delete files from the library, plus the helpers that decide
    /// what counts as in use. Kept in their own file so the mutation tests (stryker-config.json)
    /// can target exactly this code: Stryker could not compile its changes to the whole of
    /// StrmSyncService.cs, and a line range into that file went stale whenever code was added
    /// above it (issue #75).
    ///
    /// Mutants that survive on purpose, so nobody chases them:
    /// - conditions that only decide whether to log (the logger calls themselves are skipped
    ///   through ignore-methods in stryker-config.json);
    /// - the "*.strm" search patterns: ownership is decided by file content, so the glob only
    ///   saves reading other files (the same survivor is noted in StrmOwnership);
    /// - the empty-folder loop in CleanupOrphans: Directory.Delete is not recursive and refuses
    ///   a folder that still has files, and the library folder itself cannot become empty,
    ///   because cleanup refuses to run when the sync wrote nothing (ADR-013).
    /// </summary>
    public partial class StrmSyncService
    {
        /// <summary>
        /// Finds the STRM files already on disk for <paramref name="seriesName"/>, if any.
        /// </summary>
        /// <remarks>
        /// Used only on the empty-detail path, so the per-series readdir stays off the hot loop.
        /// Folder names carry an optional metadata-ID suffix, so the comparison strips it the same
        /// way the pre-fetch smart-skip index does.
        /// </remarks>
        private string[] FindExistingSeriesStrms(PluginConfiguration config, string subFolder, string seriesName)
        {
            try
            {
                var parent = Path.Combine(config.StrmLibraryPath, subFolder);
                if (!Directory.Exists(parent))
                {
                    return Array.Empty<string>();
                }

                foreach (var dir in Directory.GetDirectories(parent))
                {
                    var stripped = StripFolderIdSuffix(Path.GetFileName(dir));
                    if (string.Equals(stripped, seriesName, StringComparison.OrdinalIgnoreCase))
                    {
                        return Directory.GetFiles(dir, "*.strm", SearchOption.AllDirectories);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Could not probe existing STRM files for series '{0}': {1}", seriesName, ex.Message);
            }

            return Array.Empty<string>();
        }

        /// <summary>
        /// Deletes the on-disk folders of items the user has explicitly excluded.
        /// </summary>
        /// <remarks>
        /// Runs independently of <see cref="PluginConfiguration.CleanupOrphans"/> and ignores
        /// <see cref="PluginConfiguration.OrphanSafetyThreshold"/>. That threshold exists to survive
        /// a provider returning a truncated catalogue; an exclusion is a deliberate user action, so
        /// suppressing the delete would just look like the filter doing nothing.
        ///
        /// Folder matching strips any metadata-ID suffix, so an excluded title is found whether it
        /// was written as "Some Movie", "Some Movie [tmdbid=123]" or "Some Show [tvdbid=456]".
        ///
        /// A folder this run wrote into is never touched. Matching is by cleaned name, ignoring
        /// case, while the exclusion itself is by stream ID, so a kept entry whose name differs
        /// only in case (or cleans to the same name) shares the excluded entry's folder. Without
        /// this guard the kept title was deleted on every sync. Callers skip this pass when a
        /// category failed to load, because titles from it never reached writtenPaths and so
        /// are not protected. See ADR-018.
        /// </remarks>
        /// <param name="config">Active plugin configuration (supplies the library root).</param>
        /// <param name="excludedItems">Cleaned display name + category ID for each excluded item.</param>
        /// <param name="folderMode">"single", "multiple" or "custom".</param>
        /// <param name="categoryNames">Category ID → name, used by "multiple" mode.</param>
        /// <param name="folderMappings">Category ID → folder, used by "custom" mode.</param>
        /// <param name="rootFolder">"Movies" or "Shows".</param>
        /// <param name="writtenPaths">Every STRM this run wrote or kept.</param>
        /// <returns>The number of folders deleted.</returns>
        private int RemoveExcludedContent(
            PluginConfiguration config,
            List<Tuple<string, int?>> excludedItems,
            string folderMode,
            Dictionary<int, string> categoryNames,
            Dictionary<int, string> folderMappings,
            string rootFolder,
            HashSet<string> writtenPaths)
        {
            if (excludedItems == null || excludedItems.Count == 0)
            {
                return 0;
            }

            var removed = 0;
            var writtenDirs = BuildWrittenDirectories(writtenPaths, config.StrmLibraryPath);

            // subFolder → { folderNameWithoutIdSuffix → fullPath }. One readdir per subfolder.
            var dirIndexCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in excludedItems)
            {
                var sanitized = SanitizeFileName(item.Item1);
                if (string.IsNullOrWhiteSpace(sanitized))
                {
                    continue;
                }

                var subFolder = BuildContentFolderPath(
                    folderMode, item.Item2, categoryNames, folderMappings, rootFolder);
                if (subFolder == null)
                {
                    continue;
                }

                Dictionary<string, string> dirIndex;
                if (!dirIndexCache.TryGetValue(subFolder, out dirIndex))
                {
                    dirIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var fullPath = Path.Combine(config.StrmLibraryPath, subFolder);
                    if (Directory.Exists(fullPath))
                    {
                        foreach (var dir in Directory.GetDirectories(fullPath))
                        {
                            var stripped = StripFolderIdSuffix(Path.GetFileName(dir));
                            if (!string.IsNullOrEmpty(stripped) && !dirIndex.ContainsKey(stripped))
                            {
                                dirIndex[stripped] = dir;
                            }
                        }
                    }

                    dirIndexCache[subFolder] = dirIndex;
                }

                string existingDir;
                if (!dirIndex.TryGetValue(sanitized, out existingDir))
                {
                    continue;
                }

                if (writtenDirs.Contains(NormalizeDirectory(existingDir)))
                {
                    _logger.Info(
                        "Keeping '{0}' for excluded item '{1}': this sync wrote an included title into the same folder",
                        existingDir, sanitized);
                    continue;
                }

                try
                {
                    // Delete only the files this plugin actually wrote, verified by content, then
                    // prune whatever that emptied. Matching is by title alone, so a match is not
                    // proof of ownership: without this a user's own "Ben-Hur" folder would be
                    // destroyed by excluding the provider's "Ben-Hur", and a hand-written .nfo or a
                    // trailer.strm sitting beside our output would go with it. See ADR-014.
                    var deletedFiles = StrmOwnership.DeleteOwnedFiles(
                        existingDir, config.BaseUrl, config.DispatcharrUrl, out var folderGone);

                    if (deletedFiles == 0)
                    {
                        _logger.Debug(
                            "Skipping '{0}' for excluded item '{1}': nothing in it was written by this plugin",
                            existingDir, sanitized);
                        continue;
                    }

                    dirIndex.Remove(sanitized);
                    removed++;
                    _logger.Info(
                        folderGone
                            ? "Removed excluded item folder: {0}"
                            : "Removed plugin files for excluded item, folder kept (still has other content): {0}",
                        existingDir);
                }
                catch (Exception ex)
                {
                    _logger.Warn("Failed to remove excluded item folder '{0}': {1}", existingDir, ex.Message);
                }
            }

            if (removed > 0)
            {
                _logger.Info("Removed {0} folder(s) for explicitly excluded items under {1}", removed, rootFolder);
            }


            return removed;
        }


        /// <summary>
        /// Every directory that holds a path in <paramref name="writtenPaths"/>, and its ancestors
        /// up to (not including) the library root. Episodes sit in a season folder below the
        /// show folder an exclusion matches, so the ancestors count as written too.
        /// </summary>
        private static HashSet<string> BuildWrittenDirectories(HashSet<string> writtenPaths, string libraryRoot)
        {
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (writtenPaths == null)
            {
                return dirs;
            }

            var root = NormalizeDirectory(libraryRoot);
            string[] snapshot;
            lock (writtenPaths) { snapshot = writtenPaths.ToArray(); }

            foreach (var path in snapshot)
            {
                var dir = Path.GetDirectoryName(path);
                while (!string.IsNullOrEmpty(dir))
                {
                    var normalized = NormalizeDirectory(dir);
                    if (string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase) || !dirs.Add(normalized))
                    {
                        break;
                    }

                    dir = Path.GetDirectoryName(dir);
                }
            }

            return dirs;
        }

        /// <summary>
        /// Adds the STRM files already in <paramref name="dir"/> to <paramref name="writtenPaths"/>.
        /// Used when writing an item failed, so its existing files still count as in use.
        /// </summary>
        private void RecordExistingStrms(string dir, HashSet<string> writtenPaths)
        {
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            try
            {
                if (Directory.Exists(dir))
                {
                    RecordStrms(Directory.GetFiles(dir, "*.strm"), writtenPaths);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Could not list existing STRM files in '{0}': {1}", dir, ex.Message);
            }
        }

        private static void RecordStrms(IEnumerable<string> paths, HashSet<string> writtenPaths)
        {
            lock (writtenPaths)
            {
                foreach (var path in paths)
                {
                    writtenPaths.Add(path);
                }
            }
        }

        private static string NormalizeDirectory(string path)
            => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private int CleanupOrphans(
            string rootPath, HashSet<string> validPaths, double safetyThreshold, PluginConfiguration config)
        {
            if (!Directory.Exists(rootPath))
            {
                return 0;
            }

            var existingStrms = Directory.GetFiles(rootPath, "*.strm", SearchOption.AllDirectories);

            // Nothing was written or preserved this run, but files exist on disk. That is a
            // provider returning an empty catalogue, not the user deleting their whole library.
            // The ratio guard below cannot catch this at small N (it only applies above 10 files),
            // so refuse outright rather than emptying the library. See ADR-013.
            if (validPaths.Count == 0 && existingStrms.Length > 0)
            {
                _logger.Warn(
                    "Orphan cleanup skipped: the catalogue produced no files this run but {0} STRM file(s) exist under {1} — refusing to treat an empty catalogue as a deletion",
                    existingStrms.Length, rootPath);
                return 0;
            }

            // Being a .strm under our library root is not proof we wrote it. Verify ownership
            // before considering anything for deletion — a user's own STRM that the provider
            // never listed would otherwise look exactly like an orphan. Only orphan candidates
            // are read, so the cost stays proportional to deletions, not to library size.
            var orphans = existingStrms
                .Where(s => !validPaths.Contains(s))
                .Where(s => StrmOwnership.IsOwnedStrm(s, config.BaseUrl, config.DispatcharrUrl))
                .ToList();

            var foreignCount = existingStrms.Length - validPaths.Count - orphans.Count;
            if (foreignCount > 0)
            {
                _logger.Info(
                    "Orphan cleanup: leaving {0} STRM file(s) under {1} that this plugin did not write",
                    foreignCount, rootPath);
            }

            // Ratio is taken over the files we could actually have written (what we wrote or kept,
            // plus the orphans we own). Counting foreign files in the denominator would dilute the
            // ratio and make the guard fire less often than intended.
            var ownedTotal = validPaths.Count + orphans.Count;
            if (safetyThreshold > 0 && ownedTotal > 10 && orphans.Count > 0)
            {
                double ratio = (double)orphans.Count / ownedTotal;
                if (ratio > safetyThreshold)
                {
                    _logger.Warn(
                        "Orphan cleanup skipped: {0}/{1} ({2:P0}) exceeds safety threshold {3:P0} — possible provider issue",
                        orphans.Count, ownedTotal, ratio, safetyThreshold);
                    return 0;
                }
            }

            var removed = 0;

            foreach (var strmFile in orphans)
            {
                try
                {
                    // delete-ok: `orphans` is already filtered through StrmOwnership.IsOwnedStrm
                    // above, so every path here is one this plugin wrote.
                    File.Delete(strmFile);
                    removed++;

                    // Remove empty parent directories
                    var dir = Path.GetDirectoryName(strmFile);
                    while (!string.IsNullOrEmpty(dir) &&
                           !string.Equals(dir, rootPath, StringComparison.OrdinalIgnoreCase) &&
                           Directory.Exists(dir) &&
                           Directory.GetFileSystemEntries(dir).Length == 0)
                    {
                        // delete-ok: prunes a directory the loop condition just proved empty.
                        Directory.Delete(dir);
                        dir = Path.GetDirectoryName(dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug("Failed to cleanup orphan '{0}': {1}", strmFile, ex.Message);
                }
            }

            if (removed > 0)
            {
                _logger.Info("Removed {0} orphaned STRM files from {1}", removed, rootPath);
            }

            return removed;
        }
    }
}
