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
    public class SyncProgress
    {
        public string Phase = string.Empty;
        public int Total;
        public int Completed;
        public int Skipped;
        public int Failed;
        public int Added;
        public int Deleted;
        public bool IsRunning;

        /// <summary>Set when sync exits early (e.g. invalid folder configuration).</summary>
        public string AbortReason = string.Empty;
    }

    public class SyncHistoryEntry
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public bool Success { get; set; }
        public int MoviesTotal { get; set; }
        public int MoviesCompleted { get; set; }
        public int MoviesAdded { get; set; }
        public int MoviesSkipped { get; set; }
        public int MoviesFailed { get; set; }
        public int MoviesDeleted { get; set; }
        public int SeriesTotal { get; set; }
        public int SeriesCompleted { get; set; }
        public int SeriesAdded { get; set; }
        public int SeriesSkipped { get; set; }
        public int SeriesFailed { get; set; }
        public int SeriesDeleted { get; set; }
        public int EpisodeTotal { get; set; }
        public int EpisodeAdded { get; set; }
        public int EpisodeSkipped { get; set; }
        public int EpisodeFailed { get; set; }
        public int EpisodeDeleted { get; set; }
        public bool WasMovieSync { get; set; }
        public bool WasSeriesSync { get; set; }
        public List<string> AddedMovieTitles { get; set; } = new List<string>();
        public List<string> AddedSeriesTitles { get; set; } = new List<string>();
    }

    public class FailedSyncItem
    {
        public string ItemType { get; set; }   // "Movie" | "Series"
        public int StreamId { get; set; }
        public string Name { get; set; }
        public int? CategoryId { get; set; }
        public string TmdbId { get; set; }
        public string ContainerExtension { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime FailedAt { get; set; } = DateTime.UtcNow;
    }

    public class StrmSyncService
    {
        private static readonly STJ.JsonSerializerOptions JsonOptions = new STJ.JsonSerializerOptions
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
            // Providers sometimes send string fields (e.g. info.releasedate, rating, tmdb) as
            // numbers, booleans, null, or empty arrays, and integer fields (e.g. category_id) as
            // empty/non-numeric strings or arrays. Coerce them instead of failing the sync.
            Converters =
            {
                new Client.Models.TolerantStringConverter(),
                new Client.Models.TolerantNullableIntConverter(),
            },
        };

        private static readonly Regex InvalidFileCharsRegex = new Regex(
            @"[<>:""/\\|?*\x00-\x1F]",
            RegexOptions.Compiled);

        private static readonly Regex YearInTitleRegex = new Regex(
            @"\((\d{4})\)\s*$",
            RegexOptions.Compiled);

        private static readonly Regex FolderIdSuffixRegex = new Regex(
            @" \[(?:tmdbid|tvdbid)=\d+\]$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Same suffix, but capturing the TMDB id so an existing library folder can be read
        // back as "the user already keeps this title" (see BuildLibraryIdentityIndex).
        private static readonly Regex FolderTmdbIdRegex = new Regex(
            @"\[tmdbid=(\d+)\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Season folders are written by this plugin as "Season {0:D2}" (see the episode write
        // paths), so this matches a shape we control rather than guessing at what a user or
        // another tool might have named things. Used ONLY to keep season subfolders out of the
        // "N shows already on disk" count — never to decide what goes into the index.
        private static readonly Regex SeasonFolderRegex = new Regex(
            @"^Season \d+$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Matches the old title-bearing episode filename, capturing the part to keep:
        // "Show - S01E02 - Some Title" → "Show - S01E02". Lazy so a title that itself
        // contains an episode code ("Recap of S01E01") splits at the first code, not the last.
        private static readonly Regex TitledEpisodeFileRegex = new Regex(
            @"^(?<base>.+? - S\d{2,}E\d{2,}) - .+$",
            RegexOptions.Compiled);

        private static readonly int MaxHistoryEntries = 10;
        private static readonly HttpClient SharedHttpClient = new HttpClient(
            new XtreamRateLimitHandler { InnerHandler = new HttpClientHandler() })
        { Timeout = TimeSpan.FromSeconds(30) };

        // Increment when naming logic changes so existing installs force a full re-sync on next run.
        internal const int CurrentStrmNamingVersion = 1;

        // Increment when episode filenames change shape so existing libraries are renamed
        // in place rather than rewritten. See MigrateEpisodeFilenames.
        internal const int CurrentEpisodeFilenameVersion = 1;

        private static void ApplyUserAgentToSharedClient()
        {
            var ua = Plugin.InstanceOrNull?.Configuration?.HttpUserAgent;
            SharedHttpClient.DefaultRequestHeaders.Remove("User-Agent");
            if (!string.IsNullOrEmpty(ua))
                SharedHttpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ua);
        }

        private readonly ILogger _logger;
        private readonly TmdbLookupService _tmdbLookupService;
        private readonly HttpClient _httpClient;
        private List<SyncHistoryEntry> _syncHistory;
        private readonly object _historyLock = new object();
        private readonly List<FailedSyncItem> _failedItems = new List<FailedSyncItem>();
        private readonly object _failedItemsLock = new object();

        private SyncProgress _movieProgress = new SyncProgress();
        private SyncProgress _seriesProgress = new SyncProgress();
        private SyncProgress _episodeProgress = new SyncProgress();

        // get_series_info retry tuning (see FetchSeriesDetailAsync). Internal so tests can
        // zero the delay; prod defaults re-fetch a transient empty episode list a few times.
        internal int SeriesDetailMaxAttempts = 3;
        internal int SeriesDetailRetryBaseDelayMs = 500;

        /// <summary>
        /// Where the full deleted-path record is written when a cleanup removes more than the
        /// logged sample (ADR-F005). Null means "ask Emby for its log directory", which is what
        /// production does. Tests set it directly: <c>Plugin.Instance</c> is not available to
        /// them, and reaching for it here would throw rather than degrade.
        /// </summary>
        internal string DeletionRecordDirectory;

        /// <summary>
        /// The configuration file to take rollback copies of. Null means "ask Emby", which is
        /// what production does. Tests point it at a temp file: <c>Plugin.Instance</c> is not
        /// available to them, and the real filename is not derivable anyway — Emby names the
        /// configuration after the plugin DLL, so an install using the Emby 4.10 asset under its
        /// published name has a differently-named configuration file.
        /// </summary>
        internal string ConfigRollbackSourcePath;

        // Single-flight gates. Each sync replaces its progress object wholesale and shares a
        // written-path set, so two overlapping runs of the same kind corrupt each other's state.
        // Movies and series are gated separately because they touch different roots and are
        // deliberately runnable at the same time. RetryFailedAsync takes the movie gate: it writes
        // movie files and reuses _movieProgress.
        private readonly SemaphoreSlim _movieSyncGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _seriesSyncGate = new SemaphoreSlim(1, 1);

        /// <summary>Lowest usable value for <see cref="PluginConfiguration.SyncParallelism"/>.</summary>
        private const int MinSyncParallelism = 1;

        /// <summary>Highest usable value for <see cref="PluginConfiguration.SyncParallelism"/>.</summary>
        private const int MaxSyncParallelism = 10;

        /// <summary>
        /// Returns a usable parallelism, correcting a persisted value that is out of range.
        /// </summary>
        /// <remarks>
        /// The config UI validates this, but the value is persisted to XML and survives hand edits
        /// and migrations. Zero is the dangerous one: <c>new SemaphoreSlim(0)</c> has no permits, so
        /// the first task waits forever and the sync hangs with no way out but editing the file.
        /// </remarks>
        internal static int GetSyncParallelism(PluginConfiguration config)
        {
            var configured = config.SyncParallelism;
            if (configured >= MinSyncParallelism && configured <= MaxSyncParallelism)
            {
                return configured;
            }

            return configured < MinSyncParallelism ? MinSyncParallelism : MaxSyncParallelism;
        }

        private int ResolveSyncParallelism(PluginConfiguration config)
        {
            var resolved = GetSyncParallelism(config);
            if (resolved != config.SyncParallelism)
            {
                _logger.Warn(
                    "SyncParallelism is {0}, which is outside the usable range {1}-{2} — using {3} for this run",
                    config.SyncParallelism, MinSyncParallelism, MaxSyncParallelism, resolved);
            }

            return resolved;
        }

        private static void ReportTaskProgress(SyncProgress syncProgress, IProgress<double> taskProgress)
        {
            if (taskProgress == null) return;
            var total = Volatile.Read(ref syncProgress.Total);
            if (total <= 0) return;
            var completed = Volatile.Read(ref syncProgress.Completed);
            var pct = Math.Min(100.0, (double)completed / total * 100.0);
            taskProgress.Report(pct);
        }

        public StrmSyncService(ILogger logger, HttpClient httpClient = null)
        {
            _logger = logger;
            _tmdbLookupService = new TmdbLookupService(logger);
            _httpClient = httpClient ?? SharedHttpClient;
        }

        /// <summary>
        /// Computes a stable hash of a series' episodes for change detection.
        /// Covers episode ID per episode, sorted by season+episode to be order-independent of the
        /// JSON layout. The container extension is deliberately EXCLUDED: Dispatcharr resolves the
        /// stream by episode ID and ignores the URL suffix, and its reported extension can flip
        /// (mkv↔mp4) across refreshes for the same episode — including it caused spurious rewrites.
        /// </summary>
        internal static string ComputeSeriesEpisodeHash(Dictionary<string, List<EpisodeInfo>> episodes)
        {
            var sb = new StringBuilder();
            foreach (var seasonEntry in episodes.OrderBy(e => e.Key))
            {
                foreach (var ep in seasonEntry.Value.OrderBy(e => e.Season).ThenBy(e => e.EpisodeNum))
                {
                    sb.Append(ep.Id);
                    sb.Append('|');
                }
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        /// <summary>
        /// Computes a stable hash of the channel list for change detection.
        /// </summary>
        internal static string ComputeChannelListHash(List<LiveStreamInfo> channels)
        {
            var sorted = channels.OrderBy(c => c.StreamId);
            var sb = new StringBuilder();
            foreach (var c in sorted)
            {
                sb.Append(c.StreamId);
                sb.Append(':');
                sb.Append(c.Name ?? string.Empty);
                sb.Append(':');
                sb.Append(c.EpgChannelId ?? string.Empty);
                sb.Append(':');
                sb.Append(c.CategoryId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                sb.Append('|');
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        /// <summary>
        /// Reads a reviewed-checkpoint store (a JSON array of ids).
        /// </summary>
        /// <returns>
        /// The ids, or <c>null</c> when the field holds something that will not parse.
        /// Null and empty are deliberately distinguishable: a store that failed to read is
        /// not a store that says "nothing is reviewed", and a caller gating content on it
        /// must be able to stand down rather than withhold the whole catalogue.
        /// </returns>
        internal static HashSet<int> DeserializeIdSet(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new HashSet<int>();
            }

            try
            {
                var ids = STJ.JsonSerializer.Deserialize<List<long>>(json);
                if (ids == null)
                {
                    return null;
                }

                var set = new HashSet<int>();
                foreach (var id in ids)
                {
                    if (id > 0 && id <= int.MaxValue)
                    {
                        set.Add((int)id);
                    }
                }

                return set;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Writes a reviewed-checkpoint store back out as a JSON array.
        /// </summary>
        /// <summary>
        /// Reports the size of all four decision stores at the end of a sync (ADR-F005).
        /// <para>
        /// The exclusions and reviewed marks are the expensive, irreplaceable part of this
        /// plugin's state — tens of thousands of individual decisions that cannot be
        /// reconstructed. Nothing used to surface their size, so a store shrinking was
        /// invisible until someone noticed the review queue looked wrong, which could be
        /// weeks. The sync already reads every one of them, so a line per run costs nothing
        /// and turns a single reading into a trend.
        /// </para>
        /// <para>
        /// Deliberately does not warn or alarm on a drop. The plugin cannot tell a user
        /// bulk-unexcluding several thousand titles from a store being eaten, and a false
        /// alarm on a legitimate action is worse than a number in a log.
        /// </para>
        /// </summary>
        private void LogDecisionStoreSizes(PluginConfiguration config)
        {
            _logger.Info(
                "Decision stores: {0} excluded movies, {1} excluded series, {2} reviewed movies, {3} reviewed series",
                config.ExcludedVodStreamIds?.Length ?? 0,
                config.ExcludedSeriesIds?.Length ?? 0,
                DescribeIdSetSize(config.ReviewedVodStreamIdsJson),
                DescribeIdSetSize(config.ReviewedSeriesIdsJson));
        }

        /// <summary>
        /// The count the plugin actually acts on, or <c>UNPARSEABLE</c>.
        /// <para>
        /// Reporting an unreadable store as 0 would be the whole bug: empty and unreadable
        /// look identical in a number and mean opposite things — <see cref="DeserializeIdSet"/>
        /// returns an empty set for the first and <c>null</c> for the second, and the sync
        /// fails open on null. A store that reads 0 because it cannot be parsed is the single
        /// most alarming thing this line can say, so it must not be able to say it quietly.
        /// </para>
        /// </summary>
        private static string DescribeIdSetSize(string json)
        {
            var ids = DeserializeIdSet(json);
            return ids == null
                ? "UNPARSEABLE"
                : ids.Count.ToString(CultureInfo.InvariantCulture);
        }

        internal static string SerializeIdSet(HashSet<int> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                return string.Empty;
            }

            var ordered = new List<int>(ids);
            ordered.Sort();
            return STJ.JsonSerializer.Serialize(ordered);
        }

        /// <summary>
        /// Indexes an existing STRM library tree by the identity its folder names carry: the
        /// TMDB ID from a <c>[tmdbid=N]</c> suffix, and the ID-stripped folder name.
        /// </summary>
        /// <remarks>
        /// This is the record of what the user has previously chosen to keep, and it outlives
        /// the provider IDs those choices were stored against — which is what makes it usable
        /// as an exemption for <see cref="PluginConfiguration.RequireReviewBeforeSync"/>.
        ///
        /// Both markers are collected on purpose. TMDB alone would miss folders written before
        /// <see cref="PluginConfiguration.EnableTmdbFolderNaming"/> was switched on, and those
        /// are exactly the titles that must not be withheld: a held title's files are not added
        /// to the written set, so orphan cleanup would treat them as stale and delete them.
        /// Matching on the stripped name as well means anything actually on disk is recognised.
        ///
        /// The walk is deliberately recursive, and that is load-bearing rather than sloppy: in
        /// single-folder mode a show sits at <c>Shows/&lt;Show&gt;</c>, but in Multiple/Custom
        /// folder mode at <c>Shows/&lt;Category&gt;/&lt;Show&gt;</c>. A top-level-only walk would
        /// index the category folders instead of the shows, every
        /// <c>folderNames.Contains(seriesName)</c> would miss, and the review gate would withhold
        /// shows that are sitting on disk. Recursing keeps the index folder-mode-agnostic.
        /// </remarks>
        /// <returns>
        /// The number of distinct title-level folder names indexed — <paramref name="folderNames"/>
        /// less the season subfolders that recursing unavoidably picks up. Only the COUNT excludes
        /// them; both sets are still populated exactly as before, so matching is unaffected. This
        /// exists because the count is what gets logged as "N shows already on disk", and counting
        /// the raw set overstated it (934 reported against 881 real shows on a live run).
        /// </returns>
        internal static int BuildLibraryIdentityIndex(
            string libraryPath, string rootFolder, HashSet<int> tmdbIds, HashSet<string> folderNames)
        {
            var titleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var root = Path.Combine(libraryPath, rootFolder);
            if (!Directory.Exists(root))
            {
                return 0;
            }

            foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
            {
                var leaf = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(leaf))
                {
                    continue;
                }

                var match = FolderTmdbIdRegex.Match(leaf);
                if (match.Success)
                {
                    int id;
                    if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out id)
                        && id > 0)
                    {
                        tmdbIds.Add(id);
                    }
                }

                var stripped = StripFolderIdSuffix(leaf);
                if (!string.IsNullOrEmpty(stripped))
                {
                    folderNames.Add(stripped);
                    if (!SeasonFolderRegex.IsMatch(stripped))
                    {
                        titleNames.Add(stripped);
                    }
                }
            }

            return titleNames.Count;
        }

        internal static Dictionary<string, string> DeserializeEpisodeHashes(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, string>();
            try
            {
                return STJ.JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                       ?? new Dictionary<string, string>();
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }

        internal static string SerializeEpisodeHashes(ConcurrentDictionary<string, string> hashes)
        {
            if (hashes == null || hashes.IsEmpty)
                return string.Empty;
            return STJ.JsonSerializer.Serialize(hashes);
        }

        public SyncProgress MovieProgress => _movieProgress;
        public SyncProgress SeriesProgress => _seriesProgress;

        public IReadOnlyList<FailedSyncItem> FailedItems
        {
            get { lock (_failedItemsLock) { return _failedItems.ToList(); } }
        }

        // Lazy-loaded from PluginConfiguration.SyncHistoryJson so history survives restarts.
        // Must be called inside _historyLock.
        private List<SyncHistoryEntry> GetOrLoadHistory()
        {
            if (_syncHistory != null) return _syncHistory;

            _syncHistory = new List<SyncHistoryEntry>();
            try
            {
                var json = Plugin.InstanceOrNull?.Configuration?.SyncHistoryJson;
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var loaded = STJ.JsonSerializer.Deserialize<List<SyncHistoryEntry>>(json, JsonOptions);
                    if (loaded != null) _syncHistory.AddRange(loaded);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Failed to load sync history from config: {0}", ex.Message);
            }

            return _syncHistory;
        }

        public List<SyncHistoryEntry> GetSyncHistory()
        {
            lock (_historyLock)
            {
                return new List<SyncHistoryEntry>(GetOrLoadHistory());
            }
        }

        /// <summary>
        /// Checks whether the stored STRM naming version is current. If not, resets sync timestamps
        /// so the next run performs a full re-sync and regenerates files with corrected names.
        /// Returns true when a version upgrade was applied (timestamps were reset), false otherwise.
        /// </summary>
        internal bool CheckAndUpgradeNamingVersion(PluginConfiguration config, Action saveConfig)
        {
            if (config.StrmNamingVersion >= CurrentStrmNamingVersion)
                return false;

            _logger.Info("STRM naming version upgraded ({0} → {1}); resetting sync timestamps for full re-sync",
                config.StrmNamingVersion, CurrentStrmNamingVersion);

            config.StrmNamingVersion = CurrentStrmNamingVersion;
            config.LastMovieSyncTimestamp = 0;
            config.LastSeriesSyncTimestamp = 0;
            config.SeriesEpisodeHashesJson = string.Empty;
            saveConfig?.Invoke();
            return true;
        }

        /// <summary>
        /// Tells Emby that a library folder changed, so newly written content appears without
        /// waiting for a scheduled scan.
        ///
        /// Only called when the sync actually added or removed files: an unchanged run must not
        /// trigger a scan, or every no-op sync would spin the library (and the disk) for nothing.
        /// The path is the library root this sync writes to — <c>{StrmLibraryPath}/Movies</c> or
        /// <c>/Shows</c> — which is what the user adds to Emby as a library, and which also
        /// covers Multiple/Custom folder mode since those write category subfolders beneath it.
        ///
        /// Reports the change rather than forcing a full validation: Emby coalesces the report
        /// and refreshes just that subtree, where a full library validation would scan
        /// everything including libraries this plugin has nothing to do with.
        /// </summary>
        private void NotifyEmbyLibraryChanged(
            PluginConfiguration config, string rootFolderName, int added, int deleted)
        {
            if (!config.RefreshEmbyLibraryAfterSync) return;
            if (added <= 0 && deleted <= 0) return;

            // Null outside a running Emby (unit tests construct the service directly).
            var host = Plugin.InstanceOrNull?.ApplicationHost;
            if (host == null) return;

            var path = Path.Combine(config.StrmLibraryPath ?? string.Empty, rootFolderName);

            try
            {
                var monitor = host.Resolve<ILibraryMonitor>();
                if (monitor == null)
                {
                    _logger.Warn(
                        "Library refresh: Emby's library monitor was not available — '{0}' will be picked up by the next scheduled scan",
                        path);
                    return;
                }

                monitor.ReportFileSystemChanged(path);
                _logger.Info(
                    "Library refresh: notified Emby that '{0}' changed ({1} added, {2} removed)",
                    path, added, deleted);
            }
            catch (Exception ex)
            {
                // Never fail a sync over this — the files are already written correctly, and
                // Emby's scheduled scan remains the backstop.
                _logger.Warn("Library refresh failed for '{0}': {1}", path, ex.Message);
            }
        }

        /// <summary>
        /// One-time rename of episode STRM files from the old title-bearing form
        /// ("Show - S01E02 - Some Title.strm") to the title-free form
        /// ("Show - S01E02.strm").
        ///
        /// Renaming in place matters. Letting the sync converge on the new names instead
        /// would write every episode afresh and orphan every old one — on a large library
        /// that is an orphan ratio around 50%, far above
        /// <see cref="PluginConfiguration.OrphanSafetyThreshold"/>, so cleanup would refuse
        /// and the tree would carry two copies of everything until someone raised the
        /// threshold by hand.
        ///
        /// Deliberately does NOT touch the delta watermark or the stored episode hashes
        /// (unlike <see cref="CheckAndUpgradeNamingVersion"/>): the files land exactly where
        /// the next sync expects them, so nothing needs re-fetching.
        /// </summary>
        /// <returns>Number of files renamed or removed.</returns>
        internal int MigrateEpisodeFilenames(PluginConfiguration config, Action saveConfig)
        {
            if (config.EpisodeFilenameMigrationVersion >= CurrentEpisodeFilenameVersion)
                return 0;

            var showsRoot = Path.Combine(config.StrmLibraryPath ?? string.Empty, "Shows");
            var renamed = 0;
            var collapsed = 0;

            if (Directory.Exists(showsRoot))
            {
                // Runs once, but walks the whole tree — surface it rather than leaving an
                // unexplained pause at the start of the sync.
                _seriesProgress.Phase = "Migrating episode filenames";

                string[] files;
                try
                {
                    files = Directory.GetFiles(showsRoot, "*.strm", SearchOption.AllDirectories);
                }
                catch (Exception ex)
                {
                    // Leave the version unset so the migration is retried next run rather
                    // than being silently skipped on a transient I/O error.
                    _logger.Warn("Episode filename migration: could not scan '{0}': {1}", showsRoot, ex.Message);
                    return 0;
                }

                foreach (var path in files)
                {
                    var match = TitledEpisodeFileRegex.Match(Path.GetFileNameWithoutExtension(path) ?? string.Empty);
                    if (!match.Success) continue;

                    // Only touch files this plugin wrote — the same guard orphan cleanup uses,
                    // so a hand-placed .strm that happens to match the pattern is left alone.
                    if (!StrmOwnership.IsOwnedStrm(path, config.BaseUrl, config.DispatcharrUrl)) continue;

                    var dir = Path.GetDirectoryName(path);
                    if (string.IsNullOrEmpty(dir)) continue;
                    var target = Path.Combine(dir, match.Groups["base"].Value + ".strm");

                    try
                    {
                        if (File.Exists(target))
                        {
                            // Both forms already present — the pair of files this change exists
                            // to prevent. The title-free one is canonical and the next sync
                            // corrects its URL if it differs, so drop the titled twin.
                            // delete-ok: the loop skips every path StrmOwnership.IsOwnedStrm
                            // rejects, so this only ever removes a STRM the plugin wrote, and
                            // only when the file it would have been renamed to already exists.
                            File.Delete(path);
                            collapsed++;
                        }
                        else
                        {
                            File.Move(path, target);
                            renamed++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("Episode filename migration: could not rename '{0}': {1}", path, ex.Message);
                    }
                }
            }

            config.EpisodeFilenameMigrationVersion = CurrentEpisodeFilenameVersion;
            saveConfig?.Invoke();

            if (renamed > 0 || collapsed > 0)
            {
                _logger.Info(
                    "Episode filename migration: renamed {0} file(s) to the title-free form, removed {1} duplicate(s)",
                    renamed, collapsed);
            }

            return renamed + collapsed;
        }

        /// <summary>
        /// Syncs movie STRM files. At most one movie sync runs at a time.
        /// </summary>
        /// <returns>
        /// False when a movie sync was already in progress and this request was ignored.
        /// True when this call ran (check <see cref="SyncProgress.AbortReason"/> for a run that
        /// started and then bailed on configuration).
        /// </returns>
        public async Task<bool> SyncMoviesAsync(PluginConfiguration config, CancellationToken cancellationToken, Action saveConfig = null, IProgress<double> taskProgress = null)
        {
            // Single-flight gate, held for the whole operation. Callers check IsRunning first, but
            // that is a fast path, not a lock: between the check and the assignment below, a second
            // request or the scheduled task can pass it too. Two runs then share writtenPaths and
            // the progress object, and whichever finishes first clears IsRunning while the other is
            // still writing — admitting a third run mid-cleanup.
            if (!await _movieSyncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _logger.Warn("Movie sync requested while one is already running — ignoring the duplicate request");
                return false;
            }

            try
            {
                await SyncMoviesCoreAsync(config, cancellationToken, saveConfig, taskProgress).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _movieSyncGate.Release();
            }
        }

        private async Task SyncMoviesCoreAsync(PluginConfiguration config, CancellationToken cancellationToken, Action saveConfig, IProgress<double> taskProgress)
        {
            ApplyUserAgentToSharedClient();
            // Before anything that writes. CheckAndUpgradeNamingVersion saves, and so does the
            // review gate's write-back later, so a copy taken further down would already be of
            // the post-write state.
            SnapshotConfigurationForRollback(config);
            CheckAndUpgradeNamingVersion(config, saveConfig);
            _movieProgress = new SyncProgress { IsRunning = true, Phase = "Starting movie sync" };
            lock (_failedItemsLock) { _failedItems.Clear(); }
            var movieSyncStart = DateTime.UtcNow;
            var movieSyncSuccess = true;
            var addedMovieTitles = new List<string>();

            try
            {
                EnsureStrmLibraryPath(config.StrmLibraryPath);

                var folderMappings = FolderMappingParser.Parse(config.MovieFolderMappings);
                if (string.Equals(config.MovieFolderMode, "custom", StringComparison.OrdinalIgnoreCase) &&
                    folderMappings.Count == 0)
                {
                    movieSyncSuccess = false;
                    _movieProgress.AbortReason =
                        "Multiple Folders mode is on but no categories are assigned to any folder. " +
                        "Click + Add Folder, name it, use Refresh Categories, tick the VOD categories for that folder, then save plugin settings. " +
                        "Or switch back to Single Folder to use the flat category list.";
                    _movieProgress.Phase = "Configuration needed";
                    _logger.Warn("Movie sync aborted: {0}", _movieProgress.AbortReason);
                    return;
                }

                var categoryNames = new Dictionary<int, string>();

                // Fetch category names if needed for folder organization
                if (!string.Equals(config.MovieFolderMode, "single", StringComparison.OrdinalIgnoreCase))
                {
                    _movieProgress.Phase = "Fetching VOD categories";
                    var categories = await FetchCategoriesAsync("get_vod_categories", config, cancellationToken).ConfigureAwait(false);
                    foreach (var cat in categories)
                    {
                        categoryNames[cat.CategoryId] = cat.CategoryName;
                    }
                }

                // Fetch streams for selected categories
                _movieProgress.Phase = "Fetching VOD streams";
                var vodFetch = await FetchVodStreamsAsync(config.SelectedVodCategoryIds, config, cancellationToken).ConfigureAwait(false);
                var fetchedStreams = vodFetch.Items;

                if (vodFetch.HadFailures)
                {
                    _logger.Warn(
                        "{0} of {1} VOD categories failed to answer — orphan cleanup will be skipped this run to avoid deleting files for the categories that did not report",
                        vodFetch.FailedCategoryCount, vodFetch.RequestedCategoryCount);
                }

                // Per-item exclusions (issue #57): split the catalogue before anything else reads it.
                // The excluded half is kept so its on-disk folders can be removed below.
                var excludedVodSet = ContentExclusionFilter.BuildSet(config.ExcludedVodStreamIds);
                var excludedMovies = new List<Tuple<string, int?>>();
                var allStreams = fetchedStreams;
                if (excludedVodSet.Count > 0)
                {
                    allStreams = new List<VodStreamInfo>();
                    foreach (var s in fetchedStreams)
                    {
                        if (ContentExclusionFilter.IsExcluded(excludedVodSet, s.StreamId))
                        {
                            var excludedName = config.EnableContentNameCleaning
                                ? ContentNameCleaner.CleanContentName(s.Name, config.ContentRemoveTerms)
                                : s.Name;
                            excludedMovies.Add(Tuple.Create(excludedName, s.CategoryId));
                        }
                        else
                        {
                            allStreams.Add(s);
                        }
                    }

                    _logger.Info("Per-item exclusions: skipping {0} of {1} movies",
                        excludedMovies.Count, fetchedStreams.Count);
                }

                // Delta sync: split into new (not yet synced) and existing
                var lastMovieTs = config.LastMovieSyncTimestamp;
                var newStreams = lastMovieTs > 0
                    ? allStreams.Where(m => m.Added > lastMovieTs).ToList()
                    : allStreams;
                var existingStreams = lastMovieTs > 0
                    ? allStreams.Where(m => m.Added <= lastMovieTs).ToList()
                    : new List<VodStreamInfo>();

                _logger.Info("Delta movie sync: {0} new, {1} existing (since timestamp {2})",
                    newStreams.Count, existingStreams.Count, lastMovieTs);

                _movieProgress.Total = allStreams.Count;
                _movieProgress.Phase = "Writing STRM files";

                // Log TMDB statistics
                if (config.EnableTmdbFolderNaming)
                {
                    var withTmdb = allStreams.Count(m => IsValidTmdbId(m.TmdbId));
                    var without = allStreams.Count - withTmdb;
                    var pct = allStreams.Count > 0 ? (int)(100.0 * withTmdb / allStreams.Count) : 0;
                    _logger.Info("TMDB IDs available: {0}/{1} movies ({2}%){3}",
                        withTmdb, allStreams.Count, pct,
                        config.EnableTmdbFallbackLookup
                            ? string.Format(CultureInfo.InvariantCulture, " — TMDB fallback lookup enabled for {0} movies without IDs", without)
                            : string.Empty);
                }

                // Review gate. Off by default; when on, a title that is neither reviewed nor
                // already on disk is held rather than written, so a provider's overnight
                // additions land in the review queue instead of the library.
                var reviewGateOn = config.RequireReviewBeforeSync;
                var reviewedVodSet = DeserializeIdSet(config.ReviewedVodStreamIdsJson);
                if (reviewGateOn && reviewedVodSet == null)
                {
                    // The store did not parse. Standing down is the only safe reading: treating
                    // an unreadable checkpoint as "nothing is reviewed" would withhold the whole
                    // catalogue on the strength of a field we failed to read.
                    _logger.Error(
                        "ReviewedVodStreamIdsJson could not be parsed, so \"require review before sync\" is disabled for this run. "
                        + "Every title would otherwise look un-reviewed. Check the plugin configuration file.");
                    reviewGateOn = false;
                }

                // What the user already keeps, keyed on identity rather than provider id — the
                // exemption that stops the gate withholding an established film whose id the
                // provider reassigned. Also the reason a held title never has files to protect
                // from orphan cleanup: anything on disk matches here and syncs normally.
                var libraryTmdbIds = new HashSet<int>();
                var libraryFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (reviewGateOn)
                {
                    var moviesOnDisk = BuildLibraryIdentityIndex(
                        config.StrmLibraryPath, "Movies", libraryTmdbIds, libraryFolderNames);
                    _logger.Info(
                        "Review gate on: {0} reviewed movie ids, {1} titles already on disk ({2} with a TMDB id in the folder name)",
                        reviewedVodSet.Count, moviesOnDisk, libraryTmdbIds.Count);
                }

                var heldForReview = 0;
                var autoReviewed = new List<Tuple<int, string>>();
                // A sample of what was held. The gate's whole effect is content NOT appearing,
                // so a bare count gives no way to tell "held the 5,000 new titles" from "held
                // your entire library because the identity index came up empty".
                var heldTitles = new List<string>();
                const int HeldSampleSize = 15;

                _logger.Info("Starting movie STRM sync for {0} streams", allStreams.Count);

                var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var semaphore = new SemaphoreSlim(ResolveSyncParallelism(config));

                // Shared Dispatcharr VOD client — only queried per-movie, after smart-skip
                Emby.Xtream.Plugin.Client.DispatcharrClient dispatcharrVodClient = null;
                if (config.EnableDispatcharr && !string.IsNullOrEmpty(config.DispatcharrUrl))
                {
                    dispatcharrVodClient = new Emby.Xtream.Plugin.Client.DispatcharrClient(_logger);
                    dispatcharrVodClient.Configure(config.DispatcharrUser, config.DispatcharrPass);
                }

                var tasks = allStreams.Select(async movie =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var cleanedName = config.EnableContentNameCleaning
                            ? ContentNameCleaner.CleanContentName(movie.Name, config.ContentRemoveTerms)
                            : movie.Name;
                        var movieName = SanitizeFileName(cleanedName);
                        if (string.IsNullOrWhiteSpace(movieName))
                        {
                            Interlocked.Increment(ref _movieProgress.Failed);
                            return;
                        }

                        // Review gate, before the TMDB resolve below so a held title never costs
                        // a fallback lookup. Exempt when the user already keeps this title:
                        // matched on the provider's TMDB id, else on the folder name the sync
                        // would write. A re-addition under a new StreamId is therefore restored
                        // rather than withheld, and its new id is recorded as reviewed so the
                        // checkpoint heals itself instead of drifting.
                        //
                        // Held is not excluded: nothing is added to a blocklist and no folder is
                        // removed. And a held title has no files to protect — anything on disk
                        // matched the index above and took the exempt path.
                        if (reviewGateOn && !reviewedVodSet.Contains(movie.StreamId))
                        {
                            int providerTmdb;
                            var hasTmdb = IsValidTmdbId(movie.TmdbId)
                                && int.TryParse(movie.TmdbId.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out providerTmdb)
                                && libraryTmdbIds.Contains(providerTmdb);

                            if (!hasTmdb && !libraryFolderNames.Contains(movieName))
                            {
                                Interlocked.Increment(ref heldForReview);
                                lock (heldTitles)
                                {
                                    if (heldTitles.Count < HeldSampleSize) heldTitles.Add(cleanedName);
                                }
                                Interlocked.Increment(ref _movieProgress.Skipped);
                                Interlocked.Increment(ref _movieProgress.Completed);
                                ReportTaskProgress(_movieProgress, taskProgress);
                                return;
                            }

                            lock (autoReviewed) { autoReviewed.Add(Tuple.Create(movie.StreamId, cleanedName)); }
                        }

                        // Two distinct skip probes, picked by the flag combination:
                        //   * Folder naming ON — folder path depends on the TMDB ID, so resolve
                        //     first and probe the suffixed path. Otherwise we'd skip on a path
                        //     that never gets written.
                        //   * Folder naming OFF — probe the plain (un-suffixed) path first. If
                        //     the STRM already exists, we skip without ever running the
                        //     network fallback. Only resolve the TMDB ID when we'll proceed
                        //     past the skip check (the NFO writer may still need it).
                        //
                        // See ADR-015 for the NFO/folder-naming decoupling context.
                        string tmdbId = null;
                        if (config.EnableTmdbFolderNaming)
                        {
                            tmdbId = await ResolveMovieTmdbIdAsync(
                                movie, cleanedName, config,
                                (n, y, ct) => _tmdbLookupService.LookupTmdbIdAsync(n, y, ct),
                                _logger,
                                cancellationToken).ConfigureAwait(false);
                        }

                        var folderName = BuildMovieFolderName(cleanedName, tmdbId);
                        if (string.IsNullOrWhiteSpace(folderName))
                        {
                            Interlocked.Increment(ref _movieProgress.Failed);
                            return;
                        }

                        var subFolder = BuildContentFolderPath(
                            config.MovieFolderMode, movie.CategoryId, categoryNames, folderMappings, "Movies");
                        if (subFolder == null)
                        {
                            Interlocked.Increment(ref _movieProgress.Skipped);
                            Interlocked.Increment(ref _movieProgress.Completed);
                            ReportTaskProgress(_movieProgress, taskProgress);
                            return;
                        }

                        var movieDir = Path.Combine(config.StrmLibraryPath, subFolder, folderName);
                        var strmPath = Path.Combine(movieDir, folderName + ".strm");

                        // Smart skip: if file already exists AND the movie is not new (delta), skip
                        var isNewMovie = lastMovieTs == 0 || movie.Added > lastMovieTs;
                        if (!isNewMovie && config.SmartSkipExisting && File.Exists(strmPath))
                        {
                            lock (writtenPaths)
                            {
                                writtenPaths.Add(strmPath);
                            }
                            Interlocked.Increment(ref _movieProgress.Skipped);
                            Interlocked.Increment(ref _movieProgress.Completed);
                            ReportTaskProgress(_movieProgress, taskProgress);
                            return;
                        }

                        // Folder naming off + NFO on: defer the TMDB fallback lookup until after
                        // the skip check. Movies that get skipped never trigger a network call.
                        if (!config.EnableTmdbFolderNaming && config.EnableNfoFiles && tmdbId == null)
                        {
                            tmdbId = await ResolveMovieTmdbIdAsync(
                                movie, cleanedName, config,
                                (n, y, ct) => _tmdbLookupService.LookupTmdbIdAsync(n, y, ct),
                                _logger,
                                cancellationToken).ConfigureAwait(false);
                        }

                        var ext = !string.IsNullOrEmpty(movie.ContainerExtension)
                            ? movie.ContainerExtension
                            : "mp4";

                        var streamUrl = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}/movie/{1}/{2}/{3}.{4}",
                            config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty), movie.StreamId, ext);

                        // Build list of STRM entries (multi-version via Dispatcharr, or single)
                        var strmEntries = new List<Tuple<string, string>>();

                        if (dispatcharrVodClient != null)
                        {
                            try
                            {
                                var vodDetail = await dispatcharrVodClient.GetVodMovieDetailAsync(
                                    config.DispatcharrUrl, movie.StreamId, cancellationToken).ConfigureAwait(false);
                                if (vodDetail != null && !string.IsNullOrEmpty(vodDetail.Uuid))
                                {
                                    var providers = await dispatcharrVodClient.GetVodMovieProvidersAsync(
                                        config.DispatcharrUrl, movie.StreamId, cancellationToken).ConfigureAwait(false);
                                    if (providers.Count > 1)
                                    {
                                        for (int vi = 0; vi < providers.Count; vi++)
                                        {
                                            var suffix = vi == 0 ? string.Empty
                                                : string.Format(CultureInfo.InvariantCulture, " - Version {0}", vi + 1);
                                            var providerUrl = string.Format(
                                                CultureInfo.InvariantCulture,
                                                "{0}/proxy/vod/movie/{1}?stream_id={2}",
                                                config.DispatcharrUrl, vodDetail.Uuid, providers[vi].StreamId);
                                            strmEntries.Add(Tuple.Create(
                                                Path.Combine(movieDir, folderName + suffix + ".strm"),
                                                providerUrl));
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Debug("Dispatcharr VOD lookup failed for '{0}': {1}", movie.Name, ex.Message);
                            }
                        }

                        if (strmEntries.Count == 0)
                            strmEntries.Add(Tuple.Create(strmPath, streamUrl));

                        Directory.CreateDirectory(movieDir);
                        var isAnyNewFile = false;
                        foreach (var entry in strmEntries)
                        {
                            var itemPath = entry.Item1;
                            var itemUrl = entry.Item2;
                            var fileExists = File.Exists(itemPath);

                            // Skip write if file content is already up to date (avoids Emby library re-scan)
                            if (!fileExists || File.ReadAllText(itemPath) != itemUrl)
                            {
                                File.WriteAllText(itemPath, itemUrl);
                                if (!fileExists)
                                {
                                    Interlocked.Increment(ref _movieProgress.Added);
                                    isAnyNewFile = true;
                                }
                            }
                            lock (writtenPaths) { writtenPaths.Add(itemPath); }
                        }
                        if (isAnyNewFile)
                        {
                            lock (addedMovieTitles)
                            {
                                if (addedMovieTitles.Count < 20) addedMovieTitles.Add(cleanedName);
                            }
                        }

                        if (config.EnableNfoFiles)
                        {
                            var nfoPath = Path.Combine(movieDir, folderName + ".nfo");
                            var yearMatch = YearInTitleRegex.Match(cleanedName);
                            int? nfoYear = null;
                            if (yearMatch.Success)
                            {
                                int y;
                                if (int.TryParse(yearMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out y))
                                    nfoYear = y;
                            }
                            try { NfoWriter.WriteMovieNfo(nfoPath, cleanedName, tmdbId, nfoYear); }
                            catch (Exception ex) { _logger.Debug("NFO write failed for '{0}': {1}", movie.Name, ex.Message); }
                        }

                        Interlocked.Increment(ref _movieProgress.Completed);
                        ReportTaskProgress(_movieProgress, taskProgress);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Failed to write STRM for movie '{0}': [{1}] {2}", movie.Name, ex.GetType().Name, ex.Message);
                        lock (_failedItemsLock)
                        {
                            _failedItems.Add(new FailedSyncItem
                            {
                                ItemType = "Movie",
                                StreamId = movie.StreamId,
                                Name = movie.Name,
                                CategoryId = movie.CategoryId,
                                TmdbId = movie.TmdbId,
                                ContainerExtension = movie.ContainerExtension,
                                ErrorMessage = ex.Message
                            });
                        }
                        Interlocked.Increment(ref _movieProgress.Failed);
                        Interlocked.Increment(ref _movieProgress.Completed);
                        ReportTaskProgress(_movieProgress, taskProgress);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);

                if (reviewGateOn)
                {
                    // Fold the exempted re-additions into the checkpoint, so a title the user
                    // already keeps stays recognised under its new id without them re-reviewing
                    // it. Only ever adds; the gate never marks anything un-reviewed.
                    if (autoReviewed.Count > 0)
                    {
                        foreach (var entry in autoReviewed)
                        {
                            reviewedVodSet.Add(entry.Item1);
                        }

                        config.ReviewedVodStreamIdsJson = SerializeIdSet(reviewedVodSet);
                        saveConfig?.Invoke();

                        var restored = autoReviewed
                            .Select(e => e.Item2)
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                            .Take(HeldSampleSize)
                            .ToList();
                        _logger.Info(
                            "Review gate: {0} title(s) you already keep came back under a new StreamId — synced and marked reviewed: {1}{2}",
                            autoReviewed.Count,
                            string.Join(", ", restored),
                            autoReviewed.Count > restored.Count ? ", ..." : string.Empty);
                    }

                    if (heldForReview > 0)
                    {
                        heldTitles.Sort(StringComparer.OrdinalIgnoreCase);
                        _logger.Info(
                            "Review gate: {0} un-reviewed title(s) held out of the library. They are NOT excluded — review them in the de-dup view and they sync on the next run. For example: {1}{2}",
                            heldForReview,
                            string.Join(", ", heldTitles),
                            heldForReview > heldTitles.Count ? ", ..." : string.Empty);
                    }
                }

                // Remove folders for explicitly excluded movies. Deliberately before orphan
                // cleanup and independent of it — see RemoveExcludedContent remarks.
                // Note both passes accumulate into Deleted, which therefore counts folders
                // (exclusions) and files (orphans) together. The dashboard shows one number.
                if (excludedMovies.Count > 0)
                {
                    _movieProgress.Phase = "Removing excluded movies";
                    _movieProgress.Deleted += RemoveExcludedContent(
                        config, excludedMovies, config.MovieFolderMode, categoryNames, folderMappings, "Movies");
                }

                // Cleanup orphans. Skipped when any category failed to answer: those titles are
                // missing from writtenPaths through no fault of their own, so deleting what is
                // "orphaned" would delete a working category's library.
                if (config.CleanupOrphans && _movieProgress.Failed == 0 && !vodFetch.HadFailures)
                {
                    _movieProgress.Phase = "Cleaning up orphaned files";
                    var moviesRoot = Path.Combine(config.StrmLibraryPath, "Movies");
                    _movieProgress.Deleted += CleanupOrphans(moviesRoot, writtenPaths, config.OrphanSafetyThreshold, config);
                }

                // Persist the highest Added timestamp seen so next sync can delta from here.
                // Computed over the UNFILTERED catalogue: if the newest movie happens to be
                // excluded, the watermark must still advance past it or every later sync
                // re-processes everything after it.
                //
                // A partial fetch still advances the watermark. fetchedStreams holds only the
                // categories that answered, so this moves past what was actually processed;
                // freezing it because some other category 502'd would make every later sync
                // re-process the categories that succeeded.
                if (fetchedStreams.Count > 0)
                {
                    var maxAdded = fetchedStreams.Max(m => m.Added);
                    if (maxAdded > config.LastMovieSyncTimestamp)
                    {
                        config.LastMovieSyncTimestamp = maxAdded;
                        saveConfig?.Invoke();
                    }
                }

                _logger.Info("Movie STRM sync completed: {0} written, {1} skipped, {2} failed",
                    _movieProgress.Completed - _movieProgress.Skipped, _movieProgress.Skipped, _movieProgress.Failed);

                // Logged after the write-back above, so the numbers are the post-sync state.
                LogDecisionStoreSizes(config);

                NotifyEmbyLibraryChanged(config, "Movies", _movieProgress.Added, _movieProgress.Deleted);
            }
            catch (Exception ex)
            {
                _logger.Error("Movie sync failed: {0}", ex.Message);
                _movieProgress.Phase = "Failed: " + ex.Message;
                movieSyncSuccess = false;
                throw;
            }
            finally
            {
                _movieProgress.IsRunning = false;
                if (string.IsNullOrEmpty(_movieProgress.AbortReason))
                {
                    _movieProgress.Phase = "Complete";
                }

                AddHistoryEntry(new SyncHistoryEntry
                {
                    StartTime = movieSyncStart,
                    EndTime = DateTime.UtcNow,
                    Success = movieSyncSuccess,
                    WasMovieSync = true,
                    MoviesTotal = _movieProgress.Total,
                    MoviesCompleted = _movieProgress.Completed,
                    MoviesAdded = _movieProgress.Added,
                    MoviesSkipped = _movieProgress.Skipped,
                    MoviesFailed = _movieProgress.Failed,
                    MoviesDeleted = _movieProgress.Deleted,
                    AddedMovieTitles = addedMovieTitles,
                });
            }
        }

        /// <summary>
        /// Syncs series STRM files. At most one series sync runs at a time.
        /// </summary>
        /// <returns>
        /// False when a series sync was already in progress and this request was ignored.
        /// True when this call ran.
        /// </returns>
        public async Task<bool> SyncSeriesAsync(PluginConfiguration config, CancellationToken cancellationToken, Action saveConfig = null, IProgress<double> taskProgress = null)
        {
            // See SyncMoviesAsync for why the caller's IsRunning check is not sufficient.
            if (!await _seriesSyncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _logger.Warn("Series sync requested while one is already running — ignoring the duplicate request");
                return false;
            }

            try
            {
                await SyncSeriesCoreAsync(config, cancellationToken, saveConfig, taskProgress).ConfigureAwait(false);
                return true;
            }
            finally
            {
                _seriesSyncGate.Release();
            }
        }

        private async Task SyncSeriesCoreAsync(PluginConfiguration config, CancellationToken cancellationToken, Action saveConfig, IProgress<double> taskProgress)
        {
            ApplyUserAgentToSharedClient();
            // Before anything that writes — see the matching call in SyncMoviesCoreAsync.
            SnapshotConfigurationForRollback(config);
            CheckAndUpgradeNamingVersion(config, saveConfig);
            _seriesProgress = new SyncProgress { IsRunning = true, Phase = "Starting series sync" };
            _episodeProgress = new SyncProgress { IsRunning = true };
            lock (_failedItemsLock) { _failedItems.RemoveAll(i => i.ItemType == "Series"); }
            var seriesSyncStart = DateTime.UtcNow;
            var seriesSyncSuccess = true;
            var addedSeriesTitles = new List<string>();

            try
            {
                EnsureStrmLibraryPath(config.StrmLibraryPath);

                // Rename existing episode files to the title-free form before anything reads
                // the tree, so the pre-fetch skip and orphan cleanup below both see the names
                // this run is about to write.
                MigrateEpisodeFilenames(config, saveConfig);

                var folderMappings = FolderMappingParser.Parse(config.SeriesFolderMappings);
                if (string.Equals(config.SeriesFolderMode, "custom", StringComparison.OrdinalIgnoreCase) &&
                    folderMappings.Count == 0)
                {
                    seriesSyncSuccess = false;
                    _seriesProgress.AbortReason =
                        "Multiple Folders mode is on but no categories are assigned to any folder. " +
                        "Click + Add Folder, name it, use Refresh Categories, tick the series categories for that folder, then save plugin settings. " +
                        "Or switch back to Single Folder to use the flat category list.";
                    _seriesProgress.Phase = "Configuration needed";
                    _logger.Warn("Series sync aborted: {0}", _seriesProgress.AbortReason);
                    return;
                }

                var categoryNames = new Dictionary<int, string>();

                if (!string.Equals(config.SeriesFolderMode, "single", StringComparison.OrdinalIgnoreCase))
                {
                    _seriesProgress.Phase = "Fetching series categories";
                    var categories = await FetchSeriesCategoriesWithFallbackAsync(config, cancellationToken).ConfigureAwait(false);
                    foreach (var cat in categories)
                    {
                        categoryNames[cat.CategoryId] = cat.CategoryName;
                    }
                }

                // Parse TVDb overrides once before the loop
                var tvdbOverrides = config.EnableSeriesIdFolderNaming
                    ? ParseTvdbOverrides(config.TvdbFolderIdOverrides)
                    : null;

                _seriesProgress.Phase = "Fetching series list";
                var seriesFetch = await FetchSeriesListAsync(config.SelectedSeriesCategoryIds, config, cancellationToken).ConfigureAwait(false);
                var fetchedSeries = seriesFetch.Items;

                if (seriesFetch.HadFailures)
                {
                    _logger.Warn(
                        "{0} of {1} series categories failed to answer — orphan cleanup will be skipped this run to avoid deleting files for the categories that did not report",
                        seriesFetch.FailedCategoryCount, seriesFetch.RequestedCategoryCount);
                }

                // Collapse key for every fetched series, built once up front: (target folder +
                // cleaned name). Both the exclusion propagation immediately below and the collapse
                // further down read it from here, so the two can never disagree about which copies
                // are "the same show" — which is the property that makes the propagation safe.
                // SeriesIds are unique within the fetched list, so the indexer is enough.
                var collapseKeyBySeriesId = new Dictionary<int, string>();
                foreach (var s in fetchedSeries)
                {
                    var keyCleaned = config.EnableContentNameCleaning
                        ? ContentNameCleaner.CleanContentName(s.Name, config.ContentRemoveTerms)
                        : s.Name;
                    var keyFolder = BuildContentFolderPath(
                        config.SeriesFolderMode, s.CategoryId, categoryNames, folderMappings, "Shows");
                    collapseKeyBySeriesId[s.SeriesId] = (keyFolder ?? "null") + " " + SanitizeFileName(keyCleaned);
                }

                // Per-item exclusions (issue #57) — see the matching block in SyncMoviesAsync.
                var excludedSeriesSet = ContentExclusionFilter.BuildSet(config.ExcludedSeriesIds);

                // Path-A (ADR-F001): exclusions are stored per SeriesId, but Dispatcharr issues a
                // distinct SeriesId per (provider, category) for the same show. A category enabled
                // after the exclusion was made therefore brings a fresh, un-blocklisted copy and the
                // show silently starts syncing again — repaired today only when the user opens the
                // de-dup view AND saves, which an unattended sync never does.
                //
                // Fix: propagate exclusion across the whole collapse group rather than matching bare
                // ids. Safe because the group is exactly the set of copies the collapse merges into
                // one folder, of which only the representative is ever written — so widening cannot
                // suppress anything that would have appeared separately. Keyed on (folder + cleaned
                // name), so Multiple/Custom folder mode keeps genuinely per-folder copies independent,
                // and it matches the de-dup view's own name grouping exactly.
                var excludedGroupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (excludedSeriesSet.Count > 0)
                {
                    foreach (var s in fetchedSeries)
                    {
                        if (ContentExclusionFilter.IsExcluded(excludedSeriesSet, s.SeriesId))
                        {
                            excludedGroupKeys.Add(collapseKeyBySeriesId[s.SeriesId]);
                        }
                    }
                }

                var excludedSeriesItems = new List<Tuple<string, int?>>();
                var excludedSeriesRaw = new List<SeriesInfo>();
                var allSeries = fetchedSeries;
                if (excludedSeriesSet.Count > 0)
                {
                    // Copies caught by the group rather than by their own id — the Path-A repair.
                    // Logged separately because it is the only visible sign the propagation did
                    // anything, and "skipping N of M" alone cannot show it.
                    var groupOnlyCount = 0;
                    allSeries = new List<SeriesInfo>();
                    foreach (var s in fetchedSeries)
                    {
                        var byId = ContentExclusionFilter.IsExcluded(excludedSeriesSet, s.SeriesId);
                        if (byId || excludedGroupKeys.Contains(collapseKeyBySeriesId[s.SeriesId]))
                        {
                            if (!byId)
                            {
                                groupOnlyCount++;
                            }

                            var excludedName = config.EnableContentNameCleaning
                                ? ContentNameCleaner.CleanContentName(s.Name, config.ContentRemoveTerms)
                                : s.Name;
                            excludedSeriesItems.Add(Tuple.Create(excludedName, s.CategoryId));
                            excludedSeriesRaw.Add(s);
                        }
                        else
                        {
                            allSeries.Add(s);
                        }
                    }

                    _logger.Info("Per-item exclusions: skipping {0} of {1} series",
                        excludedSeriesItems.Count, fetchedSeries.Count);
                    if (groupOnlyCount > 0)
                    {
                        _logger.Info(
                            "{0} of those are cross-listed copies of a show already on the blocklist that arrived under a fresh SeriesId",
                            groupOnlyCount);
                    }
                }

                // Collapse series that would land in the same folder under the same name.
                // Unlike movies (one shared StreamId), a provider/proxy can cross-list the
                // "same" series under a different SeriesId per category; those instances point
                // at the same episodes but can carry different episode titles, which would
                // otherwise write duplicate per-episode .strm files (same URL, different
                // filename). Keeping one representative also skips redundant get_series_info
                // calls. Keyed on (target folder + cleaned name), so Multiple/Custom-folder
                // mode still keeps genuinely per-category copies in their separate folders.
                // Excluded series are already filtered out above, so the representative pick
                // below only has to be deterministic — it never has to avoid an excluded id.
                //
                // Episode hash cache: loaded before the collapse because the representative
                // pick below prefers a candidate that already has a stored hash. Cleared
                // alongside config.SeriesEpisodeHashesJson in the naming-flags reset further
                // down, so the skip paths still see an empty cache on a forced re-sync.
                var storedHashes = DeserializeEpisodeHashes(config.SeriesEpisodeHashesJson);
                if (allSeries.Count > 1)
                {
                    // Order the candidates before the first-wins pick, so the representative is
                    // stable across runs. Unordered, the pick follows the provider's own ordering
                    // inside each get_series response, which is not guaranteed between runs — and
                    // a flipped representative strands the series: the episode hash is keyed on
                    // SeriesId, so the new id has no stored hash, the series is delta-unchanged,
                    // it pre-fetch-skips, carries nothing, and stays in the no-hash state until
                    // some later run happens to fetch broadly.
                    //
                    // Prefer a candidate that already has a stored hash so an established
                    // representative never loses its place (even to a lower id appearing later),
                    // then the lowest SeriesId as a deterministic tie-break for a fresh group.
                    var collapseCandidates = allSeries
                        .OrderBy(s => storedHashes.ContainsKey(s.SeriesId.ToString(CultureInfo.InvariantCulture)) ? 0 : 1)
                        .ThenBy(s => s.SeriesId)
                        .ToList();
                    var keptKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var collapsedSeries = new List<SeriesInfo>(allSeries.Count);
                    // Which id won each group, and which lost. Recorded only to be logged: the
                    // pick itself is unchanged.
                    var representativeByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    var discardedByKey = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var s in collapseCandidates)
                    {
                        var folderKey = collapseKeyBySeriesId[s.SeriesId];
                        if (keptKeys.Add(folderKey))
                        {
                            collapsedSeries.Add(s);
                            representativeByKey[folderKey] = s.SeriesId;
                        }
                        else
                        {
                            if (!discardedByKey.TryGetValue(folderKey, out var discarded))
                            {
                                discarded = new List<int>();
                                discardedByKey[folderKey] = discarded;
                            }

                            discarded.Add(s.SeriesId);
                        }
                    }

                    if (collapsedSeries.Count != allSeries.Count)
                    {
                        _logger.Info("Collapsed {0} cross-listed series entries into {1} unique titles before sync",
                            allSeries.Count, collapsedSeries.Count);

                        // The id the sync actually ACTS on is invisible from outside the plugin,
                        // and that cost an hour on a real missing-episode hunt. A catalogue-wide
                        // get_series returns roughly one id per show, but the plugin fetches
                        // per-category and a show carries several; only the representative is ever
                        // compared to the delta watermark, fetched, or written. So a non-
                        // representative id sitting above the watermark looks reassuring and means
                        // nothing, and a correctly-skipped show is indistinguishable from a
                        // wrongly-skipped one. This line is the mapping nothing else records.
                        // Debug, because it is one line per collapsed group.
                        foreach (var group in discardedByKey.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                        {
                            if (!representativeByKey.TryGetValue(group.Key, out var representative))
                            {
                                continue;
                            }

                            group.Value.Sort();
                            _logger.Debug(
                                "Collapse: '{0}' -> representative SeriesId {1} (discarded: {2})",
                                group.Key, representative, string.Join(", ", group.Value));
                        }

                        allSeries = collapsedSeries;
                    }
                }

                // Delta sync: split into changed and unchanged using LastModified timestamp
                var lastSeriesTs = config.LastSeriesSyncTimestamp;
                long maxSeriesTs = lastSeriesTs;

                // Auto-reset delta state when folder naming flags change (stale folder paths would break pre-fetch skip)
                if (config.EnableSeriesIdFolderNaming != config.LastKnownEnableSeriesIdFolderNaming
                    || config.EnableSeriesMetadataLookup != config.LastKnownEnableSeriesMetadataLookup)
                {
                    if (lastSeriesTs > 0)
                        _logger.Info("Series folder naming flags changed — forcing full re-sync");
                    lastSeriesTs = 0;
                    maxSeriesTs = 0;
                    config.SeriesEpisodeHashesJson = string.Empty;
                    // The cache is read above this point now (collapse representative pick), so
                    // clear the in-memory copy too — the skip paths below must see it empty.
                    storedHashes.Clear();
                }
                config.LastKnownEnableSeriesIdFolderNaming = config.EnableSeriesIdFolderNaming;
                config.LastKnownEnableSeriesMetadataLookup = config.EnableSeriesMetadataLookup;
                saveConfig?.Invoke();

                // Excluded series never enter the loop below, so fold their timestamps in here —
                // otherwise the watermark stalls behind an excluded-but-recent title.
                foreach (var s in excludedSeriesRaw)
                {
                    long excludedLm;
                    if (long.TryParse(s.LastModified, NumberStyles.None, CultureInfo.InvariantCulture, out excludedLm)
                        && excludedLm > maxSeriesTs)
                    {
                        maxSeriesTs = excludedLm;
                    }
                }

                _seriesProgress.Total = allSeries.Count;
                _seriesProgress.Phase = "Writing STRM files";

                int deltaNew = 0, deltaExisting = 0;
                if (lastSeriesTs > 0)
                {
                    foreach (var s in allSeries)
                    {
                        long lm;
                        if (long.TryParse(s.LastModified, NumberStyles.None, CultureInfo.InvariantCulture, out lm) && lm > lastSeriesTs)
                            deltaNew++;
                        else
                            deltaExisting++;
                    }
                    _logger.Info("Delta series sync: {0} changed, {1} unchanged (since timestamp {2})",
                        deltaNew, deltaExisting, lastSeriesTs);
                }
                else
                {
                    _logger.Info("Starting series STRM sync for {0} series", allSeries.Count);
                }

                // Review gate for series. Same flag as movies, same fail-open reading of an
                // unparseable checkpoint — see SyncMoviesAsync and ADR-F002.
                var reviewGateOn = config.RequireReviewBeforeSync;
                var reviewedSeriesSet = DeserializeIdSet(config.ReviewedSeriesIdsJson);
                if (reviewGateOn && reviewedSeriesSet == null)
                {
                    _logger.Error(
                        "ReviewedSeriesIdsJson could not be parsed, so \"only sync what you have reviewed\" is disabled for series this run. "
                        + "Every show would otherwise look un-reviewed. Check the plugin configuration file.");
                    reviewGateOn = false;
                }

                // Only the folder names matter here — the TMDB set the movie side leans on is
                // unusable for series, which have no TMDB id on the list payload to compare
                // against. Collected anyway; the call signature is shared.
                var libraryTmdbIds = new HashSet<int>();
                var libraryFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (reviewGateOn)
                {
                    var showsOnDisk = BuildLibraryIdentityIndex(
                        config.StrmLibraryPath, "Shows", libraryTmdbIds, libraryFolderNames);
                    _logger.Info(
                        "Review gate on: {0} reviewed series ids, {1} shows already on disk, {2} with a stored episode hash",
                        reviewedSeriesSet.Count, showsOnDisk, storedHashes.Count);
                }

                var heldForReview = 0;
                var autoReviewed = new List<Tuple<int, string>>();
                var heldTitles = new List<string>();
                // Recorded by the gate itself rather than recomputed afterwards, so the two can
                // never disagree about what was held. Used to exempt held series from the
                // no-episode-hash diagnostic below: they have no hash because they were
                // deliberately not fetched, which is the opposite of a silent gap.
                var heldIds = new HashSet<int>();
                const int HeldSampleSize = 15;

                var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var semaphore = new SemaphoreSlim(ResolveSyncParallelism(config));

                // Episode hash cache (storedHashes) is loaded above, before the collapse.
                var updatedHashes = new ConcurrentDictionary<string, string>();
                int hashSkippedCount = 0;
                // Split the skip total by reason. One number for "skipped" hides the
                // difference between "never fetched, delta said unchanged" and "fetched,
                // episodes identical" — which is exactly the distinction you need when a
                // series is not getting the episodes you expect.
                int preFetchSkippedCount = 0;
                int unmappedSkippedCount = 0;
                int writtenCount = 0;

                // Pre-fetch directory index: subFolder → {strippedSeriesName → fullDirPath}
                // Built once before the parallel loop (one readdir per unique subfolder, no per-task races).
                var subFolderDirIndex = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                if (config.SmartSkipExisting && lastSeriesTs > 0)
                {
                    var uniqueSubFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var s in allSeries)
                    {
                        var sf = BuildContentFolderPath(
                            config.SeriesFolderMode, s.CategoryId, categoryNames, folderMappings, "Shows");
                        if (sf != null) uniqueSubFolders.Add(sf);
                    }
                    foreach (var sf in uniqueSubFolders)
                    {
                        var fullPath = Path.Combine(config.StrmLibraryPath, sf);
                        var idx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (Directory.Exists(fullPath))
                        {
                            foreach (var dir in Directory.GetDirectories(fullPath))
                            {
                                var stripped = StripFolderIdSuffix(Path.GetFileName(dir));
                                if (!string.IsNullOrEmpty(stripped) && !idx.ContainsKey(stripped))
                                    idx[stripped] = dir;
                            }
                        }
                        subFolderDirIndex[sf] = idx;
                    }
                }

                var tasks = allSeries.Select(async series =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var cleanedName = config.EnableContentNameCleaning
                            ? ContentNameCleaner.CleanContentName(series.Name, config.ContentRemoveTerms)
                            : series.Name;
                        var seriesName = SanitizeFileName(cleanedName);
                        if (string.IsNullOrWhiteSpace(seriesName))
                        {
                            Interlocked.Increment(ref _seriesProgress.Failed);
                            return;
                        }

                        var subFolder = BuildContentFolderPath(
                            config.SeriesFolderMode, series.CategoryId, categoryNames, folderMappings, "Shows");

                        if (subFolder == null)
                        {
                            Interlocked.Increment(ref unmappedSkippedCount);
                            Interlocked.Increment(ref _seriesProgress.Skipped);
                            Interlocked.Increment(ref _seriesProgress.Completed);
                            ReportTaskProgress(_seriesProgress, taskProgress);
                            return;
                        }

                        // Track delta timestamp before the API call (series.LastModified comes from the list, no extra HTTP needed)
                        long seriesLm = 0;
                        long.TryParse(series.LastModified, NumberStyles.None, CultureInfo.InvariantCulture, out seriesLm);
                        if (seriesLm > 0)
                        {
                            lock (_historyLock) { if (seriesLm > maxSeriesTs) maxSeriesTs = seriesLm; }
                        }

                        // Review gate — see the matching block in SyncMoviesAsync and ADR-F002.
                        // Deliberately AFTER the watermark update above: a held series must still
                        // advance the delta high-water mark, or it stalls behind whatever is
                        // waiting for review. And before the detail fetch below, which is the
                        // expensive call and the one that trips Dispatcharr's episode refresh.
                        //
                        // Series carry no TMDB id on the get_series list payload (measured 0 of
                        // 9,979), so the movie side's TMDB match is unavailable here. Two markers
                        // stand in: the id-stripped folder name, and a stored episode hash, which
                        // is keyed on SeriesId and so survives the provider renaming a show.
                        // SeriesIds themselves measured 0.3% dead, which is what makes the hash a
                        // dependable second marker rather than a nicety.
                        if (reviewGateOn && !reviewedSeriesSet.Contains(series.SeriesId))
                        {
                            var onDisk = libraryFolderNames.Contains(seriesName)
                                || storedHashes.ContainsKey(series.SeriesId.ToString(CultureInfo.InvariantCulture));

                            if (!onDisk)
                            {
                                Interlocked.Increment(ref heldForReview);
                                lock (heldTitles)
                                {
                                    if (heldTitles.Count < HeldSampleSize) heldTitles.Add(cleanedName);
                                }
                                lock (heldIds) { heldIds.Add(series.SeriesId); }
                                Interlocked.Increment(ref _seriesProgress.Skipped);
                                Interlocked.Increment(ref _seriesProgress.Completed);
                                ReportTaskProgress(_seriesProgress, taskProgress);
                                return;
                            }

                            lock (autoReviewed) { autoReviewed.Add(Tuple.Create(series.SeriesId, cleanedName)); }
                        }

                        var isChangedSeries = lastSeriesTs == 0 || seriesLm > lastSeriesTs;

                        // Pre-fetch smart skip: for delta-unchanged series, locate folder on disk by name
                        // (avoids one get_series_info HTTP call per unchanged series)
                        if (!isChangedSeries && config.SmartSkipExisting)
                        {
                            Dictionary<string, string> dirIndex;
                            string existingDir;
                            if (subFolderDirIndex.TryGetValue(subFolder, out dirIndex)
                                && dirIndex.TryGetValue(seriesName, out existingDir))
                            {
                                var existingStrms = Directory.GetFiles(existingDir, "*.strm", SearchOption.AllDirectories);
                                if (existingStrms.Length > 0)
                                {
                                    foreach (var strm in existingStrms)
                                        lock (writtenPaths) writtenPaths.Add(strm);
                                    var seriesKey = series.SeriesId.ToString(CultureInfo.InvariantCulture);
                                    string carryHash;
                                    if (storedHashes.TryGetValue(seriesKey, out carryHash))
                                        updatedHashes[seriesKey] = carryHash;
                                    Interlocked.Increment(ref preFetchSkippedCount);
                                    Interlocked.Increment(ref _seriesProgress.Skipped);
                                    Interlocked.Increment(ref _seriesProgress.Completed);
                                    ReportTaskProgress(_seriesProgress, taskProgress);
                                    Interlocked.Add(ref _episodeProgress.Total, existingStrms.Length);
                                    Interlocked.Add(ref _episodeProgress.Skipped, existingStrms.Length);
                                    return;
                                }
                            }
                        }

                        // Fetch series detail (needed for episodes + TMDB ID)
                        SeriesDetailInfo detail;
                        try
                        {
                            detail = await FetchSeriesDetailAsync(series.SeriesId, config, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error("Failed to fetch detail for series '{0}' (id={1}): [{2}] {3}", series.Name, series.SeriesId, ex.GetType().Name, ex.Message);
                            lock (_failedItemsLock)
                            {
                                _failedItems.Add(new FailedSyncItem
                                {
                                    ItemType = "Series",
                                    StreamId = series.SeriesId,
                                    Name = series.Name,
                                    CategoryId = series.CategoryId,
                                    ErrorMessage = ex.Message
                                });
                            }
                            Interlocked.Increment(ref _seriesProgress.Failed);
                            Interlocked.Increment(ref _seriesProgress.Completed);
                            ReportTaskProgress(_seriesProgress, taskProgress);
                            return;
                        }

                        if (detail == null || detail.Episodes == null || detail.Episodes.Count == 0)
                        {
                            // An empty payload for a series that already has files on disk is far
                            // more likely to be a transient provider hiccup (or a tolerated
                            // malformed episode map, see ADR-010) than a show that genuinely lost
                            // every episode. Keep the existing files in the valid set and count the
                            // series as failed, which holds the orphan-cleanup guard below. A real
                            // removal is picked up by a later run that returns cleanly.
                            var strandedStrms = FindExistingSeriesStrms(config, subFolder, seriesName);
                            if (strandedStrms.Length > 0)
                            {
                                foreach (var strm in strandedStrms)
                                {
                                    lock (writtenPaths) { writtenPaths.Add(strm); }
                                }

                                _logger.Warn(
                                    "Series '{0}' (id={1}) returned no episodes but has {2} STRM file(s) on disk — keeping them and skipping orphan cleanup this run",
                                    series.Name, series.SeriesId, strandedStrms.Length);
                                Interlocked.Increment(ref _seriesProgress.Failed);
                            }
                            else
                            {
                                // No episodes and nothing on disk: nothing to write and nothing
                                // to protect, so this branch used to return in complete silence
                                // — the series simply vanished from the run. Most often a film
                                // sitting in the series catalogue, or a title the provider has
                                // not populated. Say so; excluding it stops the retry cost.
                                _logger.Warn(
                                    "Series '{0}' (id={1}) returned no episodes and has no files on disk — nothing to write. Exclude it to stop re-checking every sync.",
                                    series.Name, series.SeriesId);
                            }

                            Interlocked.Increment(ref _seriesProgress.Completed);
                            ReportTaskProgress(_seriesProgress, taskProgress);
                            return;
                        }

                        // Build series folder name with metadata ID
                        var folderName = seriesName;
                        if (config.EnableSeriesIdFolderNaming)
                        {
                            var providerTmdbId = detail.Info != null ? detail.Info.TmdbId : null;
                            int? autoTvdbId = null;

                            // Only do TVDb lookup if no override and no provider TMDB
                            if (config.EnableSeriesMetadataLookup &&
                                (tvdbOverrides == null || !tvdbOverrides.ContainsKey(seriesName)) &&
                                !IsValidTmdbId(providerTmdbId))
                            {
                                var yearMatch = YearInTitleRegex.Match(cleanedName);
                                int? yearForLookup = null;
                                if (yearMatch.Success)
                                {
                                    int y;
                                    if (int.TryParse(yearMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out y))
                                    {
                                        yearForLookup = y;
                                    }
                                }

                                try
                                {
                                    autoTvdbId = await _tmdbLookupService.LookupSeriesTvdbIdAsync(cleanedName, yearForLookup, cancellationToken).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    _logger.Debug("TVDb lookup error for '{0}': {1}", cleanedName, ex.Message);
                                }
                            }

                            folderName = BuildSeriesFolderName(seriesName, providerTmdbId, autoTvdbId, tvdbOverrides);
                        }

                        var seriesDir = Path.Combine(config.StrmLibraryPath, subFolder, folderName);
                        var isNewSeries = !Directory.Exists(seriesDir);

                        if (config.EnableNfoFiles)
                        {
                            var showNfoPath = Path.Combine(seriesDir, "tvshow.nfo");
                            var tvdbIdMatch = Regex.Match(folderName, @"\[tvdbid=(\d+)\]");
                            var tmdbIdMatch = Regex.Match(folderName, @"\[tmdbid=(\d+)\]");
                            var showTvdbId = tvdbIdMatch.Success ? tvdbIdMatch.Groups[1].Value : null;
                            var showTmdbId = tmdbIdMatch.Success ? tmdbIdMatch.Groups[1].Value : null;
                            if (showTmdbId == null && detail?.Info?.TmdbId != null)
                                showTmdbId = detail.Info.TmdbId.ToString();
                            Directory.CreateDirectory(seriesDir);
                            try { NfoWriter.WriteShowNfo(showNfoPath, seriesName, showTvdbId, showTmdbId); }
                            catch (Exception ex) { _logger.Debug("Show NFO write failed for '{0}': {1}", seriesName, ex.Message); }
                        }

                        // Episode hash skip: compare episode ID+ext hash to detect unchanged content
                        // even when the provider bumped last_modified globally.
                        var currentEpHash = ComputeSeriesEpisodeHash(detail.Episodes);
                        var epHashKey = series.SeriesId.ToString(CultureInfo.InvariantCulture);
                        updatedHashes[epHashKey] = currentEpHash;

                        string previousHash;
                        if (config.SmartSkipExisting
                            && storedHashes.TryGetValue(epHashKey, out previousHash)
                            && previousHash == currentEpHash
                            && Directory.Exists(seriesDir))
                        {
                            var existingStrms = Directory.GetFiles(seriesDir, "*.strm", SearchOption.AllDirectories);
                            if (existingStrms.Length > 0)
                            {
                                foreach (var existingStrm in existingStrms)
                                {
                                    lock (writtenPaths)
                                    {
                                        writtenPaths.Add(existingStrm);
                                    }
                                }
                                Interlocked.Increment(ref _seriesProgress.Skipped);
                                Interlocked.Increment(ref _seriesProgress.Completed);
                                ReportTaskProgress(_seriesProgress, taskProgress);
                                Interlocked.Add(ref _episodeProgress.Total, existingStrms.Length);
                                Interlocked.Add(ref _episodeProgress.Skipped, existingStrms.Length);
                                Interlocked.Increment(ref hashSkippedCount);
                                return;
                            }
                        }

                        foreach (var seasonEntry in detail.Episodes)
                        {
                            // The episodes map is keyed by season number. Use it as the fallback when
                            // the per-episode "season" field is absent (0) — some providers only carry
                            // the season on the key — rather than assuming season 1.
                            int keySeason;
                            var haveKeySeason = int.TryParse(
                                seasonEntry.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out keySeason)
                                && keySeason >= 0;

                            foreach (var episode in seasonEntry.Value)
                            {
                                // Season 0 and episode 0 are specials. Forcing them to 1 drops them onto
                                // the real Season 01 / E01 slot, where a differing episode title writes a
                                // second .strm beside the genuine one — a duplicate episode in Emby.
                                // Keep them at 00 so they land in the Specials folder instead.
                                var seasonNum = episode.Season > 0
                                    ? episode.Season
                                    : (haveKeySeason ? keySeason : 1);
                                var episodeNum = episode.EpisodeNum >= 0 ? episode.EpisodeNum : 1;
                                var seasonFolder = string.Format(CultureInfo.InvariantCulture, "Season {0:D2}", seasonNum);
                                var seasonDir = Path.Combine(seriesDir, seasonFolder);

                                // The episode title is deliberately NOT part of the filename.
                                // Providers hand back different titles for the same episode across
                                // refreshes (and omit them entirely on some passes), so including
                                // the title meant a re-fetch wrote a NEW file beside the old one
                                // instead of overwriting it — one duplicate episode in Emby per
                                // title change, and a re-sync could mint tens of thousands at once.
                                // Emby matches episodes on the SxxExx code and its metadata
                                // providers rather than on filename text, so keying the name on the
                                // episode code alone is both stable and lossless.
                                var fileName = string.Format(
                                    CultureInfo.InvariantCulture,
                                    "{0} - S{1:D2}E{2:D2}.strm",
                                    seriesName, seasonNum, episodeNum);

                                var strmPath = Path.Combine(seasonDir, fileName);

                                var ext = !string.IsNullOrEmpty(episode.ContainerExtension)
                                    ? episode.ContainerExtension
                                    : "mp4";

                                var streamUrl = string.Format(
                                    CultureInfo.InvariantCulture,
                                    "{0}/series/{1}/{2}/{3}.{4}",
                                    config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty), episode.Id, ext);

                                // Skip write if file content is already up to date (avoids Emby library re-scan)
                                var fileExists = File.Exists(strmPath);
                                if (!fileExists || File.ReadAllText(strmPath) != streamUrl)
                                {
                                    Directory.CreateDirectory(seasonDir);
                                    File.WriteAllText(strmPath, streamUrl);

                                    if (!fileExists)
                                    {
                                        Interlocked.Increment(ref _episodeProgress.Added);
                                    }
                                }
                                else
                                {
                                    Interlocked.Increment(ref _episodeProgress.Skipped);
                                }

                                Interlocked.Increment(ref _episodeProgress.Total);

                                lock (writtenPaths)
                                {
                                    writtenPaths.Add(strmPath);
                                }
                            }
                        }

                        if (isNewSeries)
                        {
                            Interlocked.Increment(ref _seriesProgress.Added);
                            lock (addedSeriesTitles)
                            {
                                if (addedSeriesTitles.Count < 20) addedSeriesTitles.Add(cleanedName);
                            }
                        }
                        // Counted here rather than derived as Completed-Skipped-Failed: not
                        // every path keeps those three in step, and a series that returned an
                        // empty payload reaches Completed without writing anything.
                        Interlocked.Increment(ref writtenCount);
                        Interlocked.Increment(ref _seriesProgress.Completed);
                        ReportTaskProgress(_seriesProgress, taskProgress);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Failed to write STRM for series '{0}' (id={1}): [{2}] {3}", series.Name, series.SeriesId, ex.GetType().Name, ex.Message);
                        lock (_failedItemsLock)
                        {
                            _failedItems.Add(new FailedSyncItem
                            {
                                ItemType = "Series",
                                StreamId = series.SeriesId,
                                Name = series.Name,
                                CategoryId = series.CategoryId,
                                ErrorMessage = ex.Message
                            });
                        }
                        Interlocked.Increment(ref _seriesProgress.Failed);
                        Interlocked.Increment(ref _seriesProgress.Completed);
                        ReportTaskProgress(_seriesProgress, taskProgress);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);

                // Keeping Dispatcharr's own episode data fresh is deliberately NOT this plugin's
                // job — see ADR-F003. A server-side sweep owns that, and a sync-time poke of
                // collapsed-away siblings turned out to be inert: get_series_info already returns
                // the union across the relations behind one series record, and a sibling that
                // Dispatcharr has NOT linked to that record is a separate series whose episodes
                // nothing in the library points at.
                if (reviewGateOn)
                {
                    // Additive only, as on the movie side: the gate never marks anything
                    // un-reviewed, so folding in the shows it recognised lets the checkpoint
                    // heal itself as the provider reshuffles ids.
                    if (autoReviewed.Count > 0)
                    {
                        foreach (var entry in autoReviewed)
                        {
                            reviewedSeriesSet.Add(entry.Item1);
                        }

                        config.ReviewedSeriesIdsJson = SerializeIdSet(reviewedSeriesSet);
                        saveConfig?.Invoke();

                        var restored = autoReviewed
                            .Select(e => e.Item2)
                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                            .Take(HeldSampleSize)
                            .ToList();
                        _logger.Info(
                            "Review gate: {0} show(s) you already keep came back under a new SeriesId — synced and marked reviewed: {1}{2}",
                            autoReviewed.Count,
                            string.Join(", ", restored),
                            autoReviewed.Count > restored.Count ? ", ..." : string.Empty);
                    }

                    if (heldForReview > 0)
                    {
                        heldTitles.Sort(StringComparer.OrdinalIgnoreCase);
                        _logger.Info(
                            "Review gate: {0} un-reviewed show(s) held out of the library. They are NOT excluded — review them in the de-dup view and they sync on the next run. For example: {1}{2}",
                            heldForReview,
                            string.Join(", ", heldTitles),
                            heldForReview > heldTitles.Count ? ", ..." : string.Empty);
                    }
                }

                // Remove folders for explicitly excluded series. Deliberately before orphan
                // cleanup and independent of it — see RemoveExcludedContent remarks.
                // Note both passes accumulate into Deleted, which therefore counts folders
                // (exclusions) and files (orphans) together. The dashboard shows one number.
                if (excludedSeriesItems.Count > 0)
                {
                    _seriesProgress.Phase = "Removing excluded series";
                    _seriesProgress.Deleted += RemoveExcludedContent(
                        config, excludedSeriesItems, config.SeriesFolderMode, categoryNames, folderMappings, "Shows");
                }

                // Cleanup orphans. Skipped when any category failed to answer — see the
                // matching block in SyncMoviesAsync.
                if (config.CleanupOrphans && _seriesProgress.Failed == 0 && !seriesFetch.HadFailures)
                {
                    _seriesProgress.Phase = "Cleaning up orphaned files";
                    var showsRoot = Path.Combine(config.StrmLibraryPath, "Shows");
                    var deletedEpisodes = CleanupOrphans(showsRoot, writtenPaths, config.OrphanSafetyThreshold, config);
                    _seriesProgress.Deleted += deletedEpisodes;
                    _episodeProgress.Deleted = deletedEpisodes;
                }

                // Persist the highest LastModified timestamp seen
                if (maxSeriesTs > config.LastSeriesSyncTimestamp)
                {
                    config.LastSeriesSyncTimestamp = maxSeriesTs;
                    saveConfig?.Invoke();
                }

                // Persist episode hashes for next run
                config.SeriesEpisodeHashesJson = SerializeEpisodeHashes(updatedHashes);
                saveConfig?.Invoke();

                if (hashSkippedCount > 0)
                    _logger.Info("Episode hash skip: {0} series unchanged (episode IDs identical to previous sync)", hashSkippedCount);

                // Every series should leave an episode hash behind: computed after a fetch,
                // or carried forward by the pre-fetch skip. One that leaves neither ended the
                // run with no record of what episodes it should hold — it was skipped without
                // a stored hash to carry, or it returned early (an empty payload with nothing
                // on disk). Either way the run still reports success, and the gap is otherwise
                // only findable by diffing the hash map against the catalogue by hand.
                var noHashSeries = new List<string>();
                foreach (var s in allSeries)
                {
                    if (!updatedHashes.ContainsKey(s.SeriesId.ToString(CultureInfo.InvariantCulture))
                        && !heldIds.Contains(s.SeriesId))
                    {
                        noHashSeries.Add(string.Format(
                            CultureInfo.InvariantCulture, "'{0}' (id={1})", s.Name, s.SeriesId));
                    }
                }

                if (noHashSeries.Count > 0)
                {
                    _logger.Warn(
                        "{0} series finished with no episode hash recorded, so their episodes were not verified this run: {1}{2}",
                        noHashSeries.Count,
                        string.Join(", ", noHashSeries.Take(20)),
                        noHashSeries.Count > 20 ? ", …" : string.Empty);
                }

                // Report what actually happened. The old line derived "written" as
                // Completed-Skipped, which counted failures as writes (the failure path
                // increments Completed too) — a run with 604 failures reported 877 written.
                // Writes are now counted at the write itself, and the skip total is split by
                // reason so "never fetched" and "fetched, episodes identical" are separable.
                _logger.Info(
                    "Series STRM sync completed: {0} series — {1} written, {2} skipped ({3} unchanged, {4} episode-hash{5}), {6} failed{7}",
                    _seriesProgress.Total,
                    writtenCount,
                    _seriesProgress.Skipped,
                    preFetchSkippedCount,
                    hashSkippedCount,
                    (unmappedSkippedCount > 0
                        ? string.Format(CultureInfo.InvariantCulture, ", {0} unmapped category", unmappedSkippedCount)
                        : string.Empty)
                    // Held series must appear here or the breakdown does not add up to the skip
                    // total, which is exactly the ambiguity this line was rewritten to remove.
                    + (heldForReview > 0
                        ? string.Format(CultureInfo.InvariantCulture, ", {0} awaiting review", heldForReview)
                        : string.Empty),
                    _seriesProgress.Failed,
                    noHashSeries.Count > 0
                        ? string.Format(CultureInfo.InvariantCulture, ", {0} with no episode hash", noHashSeries.Count)
                        : string.Empty);

                // Logged after the write-back above, so the numbers are the post-sync state.
                LogDecisionStoreSizes(config);

                // Episode counts, not series counts: a series can be "written" while every
                // episode file already matched, which changes nothing on disk for Emby to find.
                NotifyEmbyLibraryChanged(config, "Shows", _episodeProgress.Added, _episodeProgress.Deleted);
            }
            catch (Exception ex)
            {
                _logger.Error("Series sync failed: {0}", ex.Message);
                _seriesProgress.Phase = "Failed: " + ex.Message;
                seriesSyncSuccess = false;
                throw;
            }
            finally
            {
                _seriesProgress.IsRunning = false;
                if (string.IsNullOrEmpty(_seriesProgress.AbortReason))
                {
                    _seriesProgress.Phase = "Complete";
                }

                AddHistoryEntry(new SyncHistoryEntry
                {
                    StartTime = seriesSyncStart,
                    EndTime = DateTime.UtcNow,
                    Success = seriesSyncSuccess,
                    WasSeriesSync = true,
                    SeriesTotal = _seriesProgress.Total,
                    SeriesCompleted = _seriesProgress.Completed,
                    SeriesAdded = _seriesProgress.Added,
                    SeriesSkipped = _seriesProgress.Skipped,
                    SeriesFailed = _seriesProgress.Failed,
                    SeriesDeleted = _seriesProgress.Deleted,
                    EpisodeTotal = _episodeProgress.Total,
                    EpisodeAdded = _episodeProgress.Added,
                    EpisodeSkipped = _episodeProgress.Skipped,
                    EpisodeFailed = _episodeProgress.Failed,
                    EpisodeDeleted = _episodeProgress.Deleted,
                    AddedSeriesTitles = addedSeriesTitles,
                });
            }
        }

        private void EnsureStrmLibraryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("STRM Library Path is not configured. Set it in the plugin settings.");
            }

            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    string.Format("Cannot create STRM Library Path '{0}': {1}. Check the path is valid and Emby has write permission.", path, ex.Message), ex);
            }
        }

        /// <summary>
        /// Re-attempts the items that failed in the last sync.
        /// </summary>
        /// <returns>False when a movie sync or another retry was already running.</returns>
        public async Task<bool> RetryFailedAsync(CancellationToken cancellationToken)
        {
            List<FailedSyncItem> items;
            lock (_failedItemsLock) { items = _failedItems.ToList(); }
            if (items.Count == 0) return true;

            // Shares _movieProgress with SyncMoviesAsync and writes into the same movie tree, so it
            // takes the same gate rather than racing a sync that is already underway.
            if (!await _movieSyncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _logger.Warn("Retry requested while a movie sync is already running — ignoring the duplicate request");
                return false;
            }

            // Nothing between acquiring a gate and its finally may throw, or the gate is held for
            // good and every later movie sync is refused until Emby restarts. The series
            // WaitAsync below throws on a cancelled token, and Plugin.Instance.Configuration
            // throws when ApplicationPaths is not initialised yet, so both sit inside.
            var seriesGateHeld = false;
            var retryStarted = false;
            try
            {
                // A retry batch can contain series items, and those write episodes under Shows.
                // Without the series gate a concurrent series sync would not have the
                // retry-written paths in its valid set, so its orphan cleanup would delete them —
                // and they would pass the ownership check precisely because the retry wrote them.
                if (items.Any(i => i.ItemType == "Series"))
                {
                    seriesGateHeld = await _seriesSyncGate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
                    if (!seriesGateHeld)
                    {
                        _logger.Warn("Retry requested while a series sync is already running — ignoring the duplicate request");
                        return false;
                    }
                }

                var config = Plugin.Instance.Configuration;
                _movieProgress = new SyncProgress { IsRunning = true, Phase = "Retrying failed items", Total = items.Count };
                retryStarted = true;

                var semaphore = new SemaphoreSlim(ResolveSyncParallelism(config));
                var categoryNames = new Dictionary<int, string>();
                var folderMappings = FolderMappingParser.Parse(config.MovieFolderMappings);
                var writtenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var succeeded = new List<FailedSyncItem>();
                var succeededLock = new object();

                var tasks = items.Select(async item =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (item.ItemType == "Movie")
                            await RetryMovieItemAsync(item, config, categoryNames, folderMappings, writtenPaths, cancellationToken).ConfigureAwait(false);
                        else if (item.ItemType == "Series")
                            await RetrySeriesItemAsync(item, config, cancellationToken).ConfigureAwait(false);

                        lock (succeededLock) { succeeded.Add(item); }
                        Interlocked.Increment(ref _movieProgress.Completed);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error("Retry still failed for '{0}': {1}", item.Name, ex.Message);
                        Interlocked.Increment(ref _movieProgress.Failed);
                        Interlocked.Increment(ref _movieProgress.Completed);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);

                lock (_failedItemsLock)
                {
                    foreach (var s in succeeded)
                        _failedItems.Remove(s);
                }

                return true;
            }
            finally
            {
                // Only report a finished retry if one actually started. Bailing out because the
                // series gate was taken leaves the previous run's progress object in place, and
                // stamping "Retry complete" on it would describe a run that never happened.
                if (retryStarted)
                {
                    _movieProgress.IsRunning = false;
                    _movieProgress.Phase = "Retry complete";
                }

                if (seriesGateHeld)
                {
                    _seriesSyncGate.Release();
                }
                _movieSyncGate.Release();
            }
        }

        private async Task RetryMovieItemAsync(
            FailedSyncItem item,
            PluginConfiguration config,
            Dictionary<int, string> categoryNames,
            Dictionary<int, string> folderMappings,
            HashSet<string> writtenPaths,
            CancellationToken cancellationToken)
        {
            var cleanedName = config.EnableContentNameCleaning
                ? ContentNameCleaner.CleanContentName(item.Name, config.ContentRemoveTerms)
                : item.Name;

            // Folder naming on retry honours EnableTmdbFolderNaming like the main loop: the
            // [tmdbid=…] suffix only goes on the folder when the user asked for it. Without
            // this gate, an NFO-only retry would write "The Matrix [tmdbid=603]" while the
            // main sync wrote "The Matrix", splitting one movie across two folders and
            // emitting the retry NFO in a folder the main loop never wrote.
            // The NFO writer and the TMDB fallback lookup only run when EnableNfoFiles wants
            // them — see ResolveMovieTmdbIdForRetryAsync for the gate.
            //
            // When folder naming is on but the provider did not supply a TMDB ID, run the
            // resolver before building the folder name. The main sync loop does this for the
            // same reason: a retry that wrote "The Matrix" (no suffix) while the main loop
            // already wrote "The Matrix [tmdbid=603]" would split one movie across two
            // folders. The resolver returns the provider ID if valid, else the fallback
            // result, else null — so the suffix appears whenever the main loop would have
            // applied it. We only run it when folder naming is on; otherwise the folder name
            // never carries the suffix and an extra network lookup is wasted.
            // Folder-naming TMDB ID: provider-supplied when valid, otherwise a one-shot
            // fallback lookup. The same call's result is reused for the NFO writer below
            // so a config with both flags on does not pay for two TMDB lookups on the
            // same item. The helper handles the provider/fallback split, validation,
            // and trimming. When folder naming is off, this branch never runs and
            // folderName is built without a suffix.
            string tmdbId = null;
            string folderTmdbId = null;
            if (config.EnableTmdbFolderNaming)
            {
                folderTmdbId = await ResolveRetryFolderTmdbIdAsync(
                    item, cleanedName, config,
                    (n, y, ct) => _tmdbLookupService.LookupTmdbIdAsync(n, y, ct),
                    _logger,
                    cancellationToken).ConfigureAwait(false);
                tmdbId = folderTmdbId;
            }
            var folderName = BuildMovieFolderName(cleanedName, folderTmdbId);
            if (string.IsNullOrWhiteSpace(folderName)) return;

            // NFO writer needs the TMDB ID even when folder naming is off (issue #63).
            // The folder-naming branch above already populated tmdbId when it ran, so
            // only resolve when folder naming is off — the resolver does not run twice.
            if (config.EnableNfoFiles && string.IsNullOrEmpty(tmdbId))
            {
                tmdbId = await ResolveMovieTmdbIdForRetryAsync(
                    item, cleanedName, config,
                    (n, y, ct) => _tmdbLookupService.LookupTmdbIdAsync(n, y, ct),
                    _logger,
                    cancellationToken).ConfigureAwait(false);
            }

            var subFolder = BuildContentFolderPath(
                config.MovieFolderMode, item.CategoryId, categoryNames, folderMappings, "Movies");
            if (subFolder == null) return;

            var movieDir = Path.Combine(config.StrmLibraryPath, subFolder, folderName);
            var strmPath = Path.Combine(movieDir, folderName + ".strm");
            var ext = !string.IsNullOrEmpty(item.ContainerExtension) ? item.ContainerExtension : "mp4";
            var streamUrl = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/movie/{1}/{2}/{3}.{4}",
                config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty), item.StreamId, ext);

            var fileExists = File.Exists(strmPath);

            // Skip write if file content is already up to date (avoids Emby library re-scan)
            if (!fileExists || File.ReadAllText(strmPath) != streamUrl)
            {
                Directory.CreateDirectory(movieDir);
                File.WriteAllText(strmPath, streamUrl);

                if (!fileExists)
                {
                    Interlocked.Increment(ref _movieProgress.Added);
                }
            }

            lock (writtenPaths) { writtenPaths.Add(strmPath); }

            if (config.EnableNfoFiles && !string.IsNullOrEmpty(tmdbId))
            {
                var nfoPath = Path.Combine(movieDir, folderName + ".nfo");
                var yearMatch = YearInTitleRegex.Match(cleanedName);
                int? nfoYear = null;
                if (yearMatch.Success)
                {
                    int y;
                    if (int.TryParse(yearMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out y))
                        nfoYear = y;
                }
                try { NfoWriter.WriteMovieNfo(nfoPath, cleanedName, tmdbId, nfoYear); }
                catch (Exception ex) { _logger.Debug("NFO write failed on retry for '{0}': {1}", item.Name, ex.Message); }
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private async Task RetrySeriesItemAsync(
            FailedSyncItem item,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var detail = await FetchSeriesDetailAsync(item.StreamId, config, cancellationToken).ConfigureAwait(false);
            if (detail == null || detail.Episodes == null || detail.Episodes.Count == 0) return;

            var cleanedName = config.EnableContentNameCleaning
                ? ContentNameCleaner.CleanContentName(item.Name, config.ContentRemoveTerms)
                : item.Name;

            var seriesDir = Path.Combine(config.StrmLibraryPath, "Shows", SanitizeFileName(cleanedName));
            Directory.CreateDirectory(seriesDir);

            foreach (var kvp in detail.Episodes)
            {
                var seasonNum = kvp.Key;
                var episodes = kvp.Value;
                if (episodes == null) continue;

                var seasonDir = Path.Combine(seriesDir, string.Format(CultureInfo.InvariantCulture, "Season {0:D2}", seasonNum));
                Directory.CreateDirectory(seasonDir);

                foreach (var ep in episodes)
                {
                    if (ep == null) continue;
                    var epFile = string.Format(CultureInfo.InvariantCulture,
                        "S{0:D2}E{1:D2}.strm", seasonNum, ep.EpisodeNum);
                    var epPath = Path.Combine(seasonDir, epFile);

                    var ext = !string.IsNullOrEmpty(ep.ContainerExtension) ? ep.ContainerExtension : "mp4";
                    var epUrl = string.Format(CultureInfo.InvariantCulture,
                        "{0}/series/{1}/{2}/{3}.{4}",
                        config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty), ep.Id, ext);

                    var fileExists = File.Exists(epPath);

                    // Skip write if file content is already up to date (avoids Emby library re-scan)
                    if (!fileExists || File.ReadAllText(epPath) != epUrl)
                    {
                        Directory.CreateDirectory(seasonDir);
                        File.WriteAllText(epPath, epUrl);

                        if (!fileExists)
                        {
                            Interlocked.Increment(ref _episodeProgress.Added);
                        }
                    }
                }
            }
        }

        private void AddHistoryEntry(SyncHistoryEntry entry)
        {
            string historyJson;
            lock (_historyLock)
            {
                var history = GetOrLoadHistory();
                history.Insert(0, entry);
                while (history.Count > MaxHistoryEntries)
                {
                    history.RemoveAt(history.Count - 1);
                }
                historyJson = STJ.JsonSerializer.Serialize(_syncHistory, JsonOptions);
            }

            try
            {
                Plugin.Instance.Configuration.SyncHistoryJson = historyJson;
                Plugin.Instance.SaveConfiguration();
            }
            catch (Exception ex)
            {
                _logger.Debug("Failed to persist sync history: {0}", ex.Message);
            }
        }

        /// <summary>
        /// Resolves a movie's TMDB ID for any downstream consumer that needs one. The lookup runs
        /// when EITHER <see cref="PluginConfiguration.EnableTmdbFolderNaming"/> OR
        /// <see cref="PluginConfiguration.EnableNfoFiles"/> is set, so that an NFO-only config can
        /// still resolve IDs to populate sidecar files. Returns null if neither flag wants a lookup
        /// or both the provider-supplied ID and the fallback lookup produced nothing.
        ///
        /// Caller is responsible for deciding whether to use the returned ID for folder naming
        /// (gate on <see cref="PluginConfiguration.EnableTmdbFolderNaming"/>) or for NFO content
        /// (gate on <see cref="PluginConfiguration.EnableNfoFiles"/>) — this helper does not
        /// enforce either, since the two are independent.
        /// </summary>
        internal static async Task<string> ResolveMovieTmdbIdAsync(
            VodStreamInfo movie, string cleanedName, PluginConfiguration config,
            Func<string, int?, CancellationToken, Task<string>> lookupTmdbIdAsync,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            if (!config.EnableTmdbFolderNaming && !config.EnableNfoFiles)
            {
                return null;
            }

            if (IsValidTmdbId(movie.TmdbId))
            {
                return movie.TmdbId.Trim();
            }

            if (config.EnableTmdbFallbackLookup)
            {
                var yearMatch = YearInTitleRegex.Match(cleanedName);
                int? yearForLookup = null;
                if (yearMatch.Success)
                {
                    int y;
                    if (int.TryParse(yearMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out y))
                    {
                        yearForLookup = y;
                    }
                }

                try
                {
                    return await lookupTmdbIdAsync(cleanedName, yearForLookup, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.Debug("TMDB fallback error for '{0}': {1}", cleanedName, ex.Message);
                }
            }

            return null;
        }

        /// <summary>
        /// Retry-path fallback TMDB lookup. The retry path only carries <see cref="FailedSyncItem"/>
        /// (no full <see cref="VodStreamInfo"/>). Returns the provider-supplied ID when valid, or
        /// runs the TMDB fallback lookup when <see cref="PluginConfiguration.EnableTmdbFallbackLookup"/>
        /// is set. Returns null on lookup failure or when the lookup is disabled.
        ///
        /// The caller is responsible for gating this helper on <see cref="PluginConfiguration.EnableNfoFiles"/>
        /// (or whichever flag needs the ID). Folder naming on retry is handled by the caller
        /// separately because the retry path predates <see cref="PluginConfiguration.EnableTmdbFolderNaming"/>.
        /// </summary>
        internal static async Task<string> ResolveMovieTmdbIdForRetryAsync(
            FailedSyncItem item, string cleanedName, PluginConfiguration config,
            Func<string, int?, CancellationToken, Task<string>> lookupTmdbIdAsync,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            if (IsValidTmdbId(item.TmdbId))
            {
                return item.TmdbId.Trim();
            }

            if (config.EnableTmdbFallbackLookup)
            {
                var yearMatch = YearInTitleRegex.Match(cleanedName);
                int? yearForLookup = null;
                if (yearMatch.Success)
                {
                    int y;
                    if (int.TryParse(yearMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out y))
                    {
                        yearForLookup = y;
                    }
                }

                try
                {
                    return await lookupTmdbIdAsync(cleanedName, yearForLookup, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.Debug("TMDB fallback error for '{0}': {1}", item.Name, ex.Message);
                }
            }

            return null;
        }

        /// <summary>
        /// Retry-path folder-naming TMDB ID. Returns the provider-supplied ID when valid,
        /// otherwise runs the fallback lookup; the returned value is validated as a usable
        /// TMDB ID and trimmed. Returns null when folder naming should produce an unsuffixed
        /// folder (no provider ID, no fallback hit, fallback disabled, or lookup failed).
        ///
        /// Encapsulates the retry-path branch so tests exercise the same code as production
        /// — previously each test re-implemented the provider/fallback/validate/trim chain
        /// inline, which let the tests pass even when the production shape drifted.
        /// </summary>
        internal static async Task<string> ResolveRetryFolderTmdbIdAsync(
            FailedSyncItem item,
            string cleanedName,
            PluginConfiguration config,
            Func<string, int?, CancellationToken, Task<string>> lookupTmdbIdAsync,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var folderTmdbId = IsValidTmdbId(item.TmdbId)
                ? item.TmdbId.Trim()
                : null;
            if (folderTmdbId != null) return folderTmdbId;

            folderTmdbId = await ResolveMovieTmdbIdForRetryAsync(
                item, cleanedName, config,
                lookupTmdbIdAsync, logger, cancellationToken).ConfigureAwait(false);

            if (folderTmdbId != null && !IsValidTmdbId(folderTmdbId))
            {
                return null;
            }
            return folderTmdbId?.Trim();
        }

        internal static string BuildMovieFolderName(string cleanedName, string tmdbId)
        {
            var sanitized = SanitizeFileName(cleanedName);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return string.Empty;
            }

            if (IsValidTmdbId(tmdbId))
            {
                return sanitized + " [tmdbid=" + tmdbId.Trim() + "]";
            }

            return sanitized;
        }

        private static bool IsValidTmdbId(string tmdbId)
        {
            if (string.IsNullOrWhiteSpace(tmdbId))
            {
                return false;
            }

            int id;
            return int.TryParse(tmdbId, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
        }

        internal static Dictionary<string, int> ParseTvdbOverrides(string config)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(config))
            {
                return result;
            }

            var lines = config.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    continue;
                }

                var eqIdx = trimmed.IndexOf('=');
                if (eqIdx <= 0)
                {
                    continue;
                }

                var folderName = trimmed.Substring(0, eqIdx).Trim();
                var idStr = trimmed.Substring(eqIdx + 1).Trim();

                int tvdbId;
                if (!string.IsNullOrEmpty(folderName) &&
                    int.TryParse(idStr, NumberStyles.None, CultureInfo.InvariantCulture, out tvdbId) &&
                    tvdbId > 0)
                {
                    result[folderName] = tvdbId;
                }
            }

            return result;
        }

        internal static string BuildSeriesFolderName(
            string sanitizedName, string tmdbId,
            int? autoTvdbId, Dictionary<string, int> tvdbOverrides)
        {
            if (string.IsNullOrWhiteSpace(sanitizedName))
            {
                return string.Empty;
            }

            // Priority 1: manual TVDb override
            int overrideId;
            if (tvdbOverrides != null && tvdbOverrides.TryGetValue(sanitizedName, out overrideId))
            {
                return sanitizedName + " [tvdbid=" + overrideId.ToString(CultureInfo.InvariantCulture) + "]";
            }

            // Priority 2: Xtream provider TMDB ID
            if (IsValidTmdbId(tmdbId))
            {
                return sanitizedName + " [tmdbid=" + tmdbId.Trim() + "]";
            }

            // Priority 3: auto TVDb lookup
            if (autoTvdbId.HasValue && autoTvdbId.Value > 0)
            {
                return sanitizedName + " [tvdbid=" + autoTvdbId.Value.ToString(CultureInfo.InvariantCulture) + "]";
            }

            // Priority 4: no ID
            return sanitizedName;
        }

        private static string StripFolderIdSuffix(string folderName)
        {
            return FolderIdSuffixRegex.Replace(folderName, string.Empty);
        }

        /// <summary>
        /// Strips provider-embedded series name + episode code prefixes from an episode title.
        /// Handles two patterns:
        ///   1. "{CleanedSeriesName} - SxxExx" at start/anywhere (full name match)
        ///   2. "AnyPrefix - SxxExx - ..." where SxxExx matches the exact season/episode numbers
        /// Returns the human-readable remainder, or empty string if the title was only a prefix.
        /// </summary>
        internal static string StripEpisodeTitleDuplicate(string episodeTitle, string seriesName, int seasonNum, int episodeNum)
        {
            var result = episodeTitle?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(result))
                return result;

            // Pass 1: strip "{seriesName} - SxxExx" pattern (handles clean name match including year)
            if (!string.IsNullOrEmpty(seriesName))
            {
                result = Regex.Replace(
                    result,
                    @"[\s\-]*" + Regex.Escape(seriesName) + @"[\s\-]*S\d+E\d+[\s\-]*",
                    string.Empty,
                    RegexOptions.IgnoreCase).Trim('-', ' ');
            }

            // Pass 2: if the exact episode code still appears (e.g. provider used a short series
            // name without the year), strip everything up to and including that code.
            // "Yago - S01E33 - Episode 33" → "Episode 33"
            var episodeCode = string.Format(CultureInfo.InvariantCulture, "S{0:D2}E{1:D2}", seasonNum, episodeNum);
            var codeIdx = result.IndexOf(episodeCode, StringComparison.OrdinalIgnoreCase);
            if (codeIdx >= 0)
            {
                result = result.Substring(codeIdx + episodeCode.Length).Trim('-', ' ');
            }

            return result;
        }

        internal static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var result = InvalidFileCharsRegex.Replace(name, string.Empty);
            // Remove leading/trailing dots and spaces (invalid on Windows)
            result = result.Trim('.', ' ');
            // Collapse multiple spaces
            result = Regex.Replace(result, @"\s{2,}", " ");
            return result;
        }

        private static string BuildContentFolderPath(
            string folderMode,
            int? categoryId,
            Dictionary<int, string> categoryNames,
            Dictionary<int, string> folderMappings,
            string rootFolder)
        {
            if (string.Equals(folderMode, "single", StringComparison.OrdinalIgnoreCase))
            {
                return rootFolder;
            }

            if (string.Equals(folderMode, "custom", StringComparison.OrdinalIgnoreCase) && categoryId.HasValue)
            {
                string mappedFolder;
                if (folderMappings.TryGetValue(categoryId.Value, out mappedFolder))
                {
                    return Path.Combine(rootFolder, SanitizeFileName(mappedFolder));
                }
                return null;
            }

            if (string.Equals(folderMode, "multiple", StringComparison.OrdinalIgnoreCase) && categoryId.HasValue)
            {
                string categoryName;
                if (categoryNames.TryGetValue(categoryId.Value, out categoryName) &&
                    !string.IsNullOrWhiteSpace(categoryName))
                {
                    return Path.Combine(rootFolder, SanitizeFileName(categoryName));
                }
                return null;
            }

            return rootFolder;
        }

        private async Task<List<Category>> FetchCategoriesAsync(string action, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action={3}",
                config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty), action);

            var json = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
            return STJ.JsonSerializer.Deserialize<List<Category>>(json, JsonOptions)
                ?? new List<Category>();
        }

        private async Task<List<Category>> FetchSeriesCategoriesWithFallbackAsync(
            PluginConfiguration config, CancellationToken cancellationToken)
        {
            var categories = await FetchCategoriesAsync("get_series_categories", config, cancellationToken).ConfigureAwait(false);
            if (categories.Count > 0)
            {
                return categories;
            }

            // Fallback: derive categories from series list
            _logger.Info("get_series_categories returned empty, deriving from series list");
            var seriesList = await FetchSeriesListAsync(null, config, cancellationToken).ConfigureAwait(false);
            return seriesList.Items
                .Where(s => s.CategoryId.HasValue)
                .GroupBy(s => s.CategoryId.Value)
                .Select(g => new Category
                {
                    CategoryId = g.Key,
                    CategoryName = g.FirstOrDefault(s => !string.IsNullOrEmpty(s.CategoryName))?.CategoryName
                        ?? "Category " + g.Key,
                })
                .OrderBy(c => c.CategoryName)
                .ToList();
        }

        private async Task<CatalogueFetchResult<VodStreamInfo>> FetchVodStreamsAsync(
            int[] categoryIds, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var result = new CatalogueFetchResult<VodStreamInfo>();
            var allStreams = new List<VodStreamInfo>();

            if (categoryIds == null || categoryIds.Length == 0)
            {
                // Fetch all VOD streams. A throw here propagates: the caller's outer catch
                // aborts the sync before cleanup, which is the correct outcome for the
                // whole-catalogue request failing.
                var url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/player_api.php?username={1}&password={2}&action=get_vod_streams",
                    config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty));

                var json = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                allStreams = STJ.JsonSerializer.Deserialize<List<VodStreamInfo>>(json, JsonOptions)
                    ?? new List<VodStreamInfo>();
            }
            else
            {
                result.RequestedCategoryCount = categoryIds.Length;
                var semaphore = new SemaphoreSlim(ResolveSyncParallelism(config));
                var tasks = categoryIds.Select(async catId =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var url = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}/player_api.php?username={1}&password={2}&action=get_vod_streams&category_id={3}",
                            config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty), catId);

                        var json = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                        var streams = STJ.JsonSerializer.Deserialize<List<VodStreamInfo>>(json, JsonOptions)
                            ?? new List<VodStreamInfo>();

                        // Override category_id to match the requested category.
                        // The Xtream API can return cross-listed movies whose primary
                        // category_id differs from the category we queried. Without
                        // this, custom folder mapping skips them as unmapped.
                        foreach (var s in streams)
                        {
                            s.CategoryId = catId;
                        }

                        return Tuple.Create(streams, true);
                    }
                    catch (Exception ex)
                    {
                        // Report the failure rather than returning an empty list. An empty list
                        // is indistinguishable from an empty category and would let orphan
                        // cleanup delete this category's existing files.
                        _logger.Warn("Failed to fetch VOD streams for category {0}: {1}", catId, ex.Message);
                        return Tuple.Create(new List<VodStreamInfo>(), false);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var r in results)
                {
                    if (r.Item2)
                    {
                        allStreams.AddRange(r.Item1);
                    }
                    else
                    {
                        result.FailedCategoryCount++;
                    }
                }

                // Deduplicate by StreamId (first occurrence wins, keeping its assigned category)
                allStreams = allStreams.GroupBy(s => s.StreamId).Select(g => g.First()).ToList();
            }

            result.Items = allStreams;
            return result;
        }

        private async Task<CatalogueFetchResult<SeriesInfo>> FetchSeriesListAsync(
            int[] categoryIds, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var result = new CatalogueFetchResult<SeriesInfo>();
            var allSeries = new List<SeriesInfo>();

            if (categoryIds == null || categoryIds.Length == 0)
            {
                var url = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}/player_api.php?username={1}&password={2}&action=get_series",
                    config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty));

                var json = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                allSeries = STJ.JsonSerializer.Deserialize<List<SeriesInfo>>(json, JsonOptions)
                    ?? new List<SeriesInfo>();
            }
            else
            {
                result.RequestedCategoryCount = categoryIds.Length;
                var semaphore = new SemaphoreSlim(ResolveSyncParallelism(config));
                var tasks = categoryIds.Select(async catId =>
                {
                    await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var url = string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}/player_api.php?username={1}&password={2}&action=get_series&category_id={3}",
                            config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty), catId);

                        var json = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                        var series = STJ.JsonSerializer.Deserialize<List<SeriesInfo>>(json, JsonOptions)
                            ?? new List<SeriesInfo>();

                        // Override category_id to match the requested category (same
                        // cross-listing issue as VOD streams — see FetchVodStreamsAsync).
                        foreach (var s in series)
                        {
                            s.CategoryId = catId;
                        }

                        return Tuple.Create(series, true);
                    }
                    catch (Exception ex)
                    {
                        // See FetchVodStreamsAsync: a failure must not look like an empty category.
                        _logger.Warn("Failed to fetch series for category {0}: {1}", catId, ex.Message);
                        return Tuple.Create(new List<SeriesInfo>(), false);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var r in results)
                {
                    if (r.Item2)
                    {
                        allSeries.AddRange(r.Item1);
                    }
                    else
                    {
                        result.FailedCategoryCount++;
                    }
                }

                allSeries = allSeries.GroupBy(s => s.SeriesId).Select(g => g.First()).ToList();
            }

            result.Items = allSeries;
            return result;
        }

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

        private async Task<SeriesDetailInfo> FetchSeriesDetailAsync(
            int seriesId, PluginConfiguration config, CancellationToken cancellationToken)
        {
            var url = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/player_api.php?username={1}&password={2}&action=get_series_info&series_id={3}",
                config.BaseUrl, Uri.EscapeDataString(config.Username ?? string.Empty), Uri.EscapeDataString(config.Password ?? string.Empty), seriesId);

            // Some providers answer get_series_info with HTTP 200 but an empty episode
            // list when several detail requests arrive at once (SyncParallelism > 1). The
            // payload is fine on a lightly-loaded retry, so re-fetch a few times with a
            // short backoff before giving up. Without this, each empty response is silently
            // treated as "no episodes" and the series is skipped, so a batch of newly-added
            // (or just re-included) titles only trickles in a couple per sync. The backoff
            // also spaces concurrent detail calls apart, easing the contention that produces
            // the empties. A throw still propagates to the caller's catch unchanged.
            var maxAttempts = Math.Max(1, SeriesDetailMaxAttempts);
            SeriesDetailInfo detail = null;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var json = await _httpClient.GetStringAsync(url).ConfigureAwait(false);
                detail = STJ.JsonSerializer.Deserialize<SeriesDetailInfo>(json, JsonOptions);

                if (detail != null && detail.Episodes != null && detail.Episodes.Count > 0)
                    return detail;

                if (attempt < maxAttempts)
                {
                    _logger.Debug("Empty episode list for series {0} (attempt {1}/{2}) — retrying", seriesId, attempt, maxAttempts);
                    if (SeriesDetailRetryBaseDelayMs > 0)
                        await Task.Delay(SeriesDetailRetryBaseDelayMs * attempt, cancellationToken).ConfigureAwait(false);
                }
            }

            // Still empty after retries — return it and let the caller record the outcome.
            return detail;
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
        /// </remarks>
        /// <param name="config">Active plugin configuration (supplies the library root).</param>
        /// <param name="excludedItems">Cleaned display name + category ID for each excluded item.</param>
        /// <param name="folderMode">"single", "multiple" or "custom".</param>
        /// <param name="categoryNames">Category ID → name, used by "multiple" mode.</param>
        /// <param name="folderMappings">Category ID → folder, used by "custom" mode.</param>
        /// <param name="rootFolder">"Movies" or "Shows".</param>
        /// <returns>The number of folders deleted.</returns>
        private int RemoveExcludedContent(
            PluginConfiguration config,
            List<Tuple<string, int?>> excludedItems,
            string folderMode,
            Dictionary<int, string> categoryNames,
            Dictionary<int, string> folderMappings,
            string rootFolder)
        {
            if (excludedItems == null || excludedItems.Count == 0)
            {
                return 0;
            }

            var removed = 0;

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
            // Ordered so the logged sample below is the same 15 paths on every run rather than
            // whichever 15 the filesystem happened to enumerate first. Deletion order is
            // otherwise irrelevant — the files are independent.
            var orphans = existingStrms
                .Where(s => !validPaths.Contains(s))
                .Where(s => StrmOwnership.IsOwnedStrm(s, config.BaseUrl, config.DispatcharrUrl))
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
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

            // A successful deletion was previously logged nowhere, at any level — only the count
            // was. So "the sync removed 360 episodes" was unanswerable after the fact: the files
            // were gone and nothing recorded which ones. Worse, the class of damage this hides is
            // exactly the dangerous one — a title whose provider id churned leaves a .strm that is
            // not in writtenPaths, so it is deleted as an orphan even though the user still wants
            // it (see ADR-F004). Keep a sample so the question is answerable next time.
            const int DeletedSampleSize = 15;
            // Every deletion, not just the sample. Bounded by the orphan count, which the ratio
            // guard above already caps, so this cannot grow to the size of the library.
            var deleted = new List<string>();

            foreach (var strmFile in orphans)
            {
                try
                {
                    // delete-ok: `orphans` is already filtered through StrmOwnership.IsOwnedStrm
                    // above, so every path here is one this plugin wrote.
                    File.Delete(strmFile);
                    removed++;
                    deleted.Add(RelativeToRoot(strmFile, rootPath));

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
                // Already in order: `orphans` was sorted before the loop, so the sample is the
                // same 15 paths on every run.
                var sample = deleted.Count > DeletedSampleSize
                    ? deleted.GetRange(0, DeletedSampleSize)
                    : deleted;

                // Past the sample the log alone stops being able to answer "what went?" — and
                // that is exactly the size of event where the question gets asked. Two real
                // cases motivated this: 360 files under Shows and 126 under Movies, neither
                // explainable afterwards. The record is written whatever the log level, because
                // the question is always asked in hindsight and a diagnostic you had to enable
                // beforehand cannot answer it.
                var recordPath = deleted.Count > sample.Count
                    ? WriteDeletionRecord(rootPath, deleted)
                    : null;

                _logger.Info(
                    "Removed {0} orphaned STRM files from {1}: {2}{3}",
                    removed, rootPath,
                    string.Join(", ", sample),
                    deleted.Count > sample.Count
                        ? ", ... (full list: " + (recordPath ?? "could not be written") + ")"
                        : string.Empty);
            }

            return removed;
        }

        /// <summary>
        /// Takes a rollback copy of the configuration file if it has changed since the last one
        /// (ADR-F005 mechanism 3). Returns the path written, or null if nothing was.
        /// <para>
        /// Called at the START of a sync, before the run performs any writes of its own — the
        /// naming-version upgrade and the review gate's write-back both save the configuration,
        /// so a copy taken later would already be of the post-write state.
        /// </para>
        /// <para>
        /// This is a rollback, not a backup. It sits on the same volume as the file it protects
        /// and does nothing for a lost disk; what it covers is a bad write through the plugin's
        /// own save path, where the last good state is otherwise gone. Never throws — failing to
        /// take a safety copy must not stop a sync the user asked for.
        /// </para>
        /// </summary>
        private string SnapshotConfigurationForRollback(PluginConfiguration config)
        {
            var keep = config.ConfigRollbackCount;
            if (keep <= 0)
            {
                return null;
            }

            try
            {
                var source = ConfigRollbackSourcePath;
                if (string.IsNullOrEmpty(source))
                {
                    // Plugin.Instance throws before ApplicationPaths is initialised, so this is
                    // guarded rather than resolved at construction.
                    source = Plugin.InstanceOrNull?.ConfigPath;
                }

                if (string.IsNullOrEmpty(source) || !File.Exists(source))
                {
                    return null;
                }

                var directory = Path.Combine(Path.GetDirectoryName(source) ?? string.Empty, "rollback");
                Directory.CreateDirectory(directory);

                // Skip when nothing changed, or a user who syncs hourly and edits nothing would
                // churn several megabytes a day and push the real last-good state out of the
                // retention window — which would defeat the whole mechanism.
                var currentHash = HashFile(source);
                var existing = Directory.GetFiles(directory, "*.xml");
                Array.Sort(existing, StringComparer.OrdinalIgnoreCase);
                if (existing.Length > 0 && currentHash != null
                    && string.Equals(currentHash, HashFile(existing[existing.Length - 1]), StringComparison.Ordinal))
                {
                    return null;
                }

                // Millisecond resolution, not seconds: the movie sync saves the configuration and
                // the series sync runs straight afterwards, so two copies in the same second are
                // ordinary rather than exotic — and at second resolution the second one would
                // overwrite the first, silently losing the state it was taken to preserve.
                // Still sorts chronologically, which the prune depends on.
                var target = Path.Combine(
                    directory,
                    string.Format(CultureInfo.InvariantCulture, "{0:yyyyMMdd-HHmmss-fff}.xml", DateTime.Now));
                File.Copy(source, target, false);

                PruneRollbackCopies(directory, keep);
                _logger.Debug("Configuration rollback copy written to {0}", target);
                return target;
            }
            catch (Exception ex)
            {
                _logger.Warn("Could not take a configuration rollback copy: {0}", ex.Message);
                return null;
            }
        }

        /// <summary>SHA-256 of a file, or null if it cannot be read.</summary>
        private static string HashFile(string path)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var stream = File.OpenRead(path))
                {
                    return BitConverter.ToString(sha.ComputeHash(stream));
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Keeps the rollback copies bounded. Name-sorted, which is chronological because the
        /// filename is the timestamp.
        /// </summary>
        private void PruneRollbackCopies(string directory, int keep)
        {
            var existing = Directory.GetFiles(directory, "*.xml");
            if (existing.Length <= keep)
            {
                return;
            }

            Array.Sort(existing, StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < existing.Length - keep; i++)
            {
                try
                {
                    // delete-ok: removes this plugin's own rollback copies from the "rollback"
                    // folder it created beside its configuration file. These are copies of the
                    // plugin's XML settings, never library content, so the StrmOwnership check
                    // does not apply and nothing here can reach a .strm or a media folder.
                    File.Delete(existing[i]);
                }
                catch (Exception ex)
                {
                    _logger.Debug("Could not prune old rollback copy '{0}': {1}", existing[i], ex.Message);
                }
            }
        }

        /// <summary>
        /// Writes the complete list of paths a cleanup removed, and returns where it went
        /// (null if it could not be written). Called only when the deletion count exceeds the
        /// sample the log line carries.
        /// <para>
        /// Never throws: the files are already gone by the time this runs, so failing to record
        /// what happened must not also fail the sync.
        /// </para>
        /// </summary>
        private string WriteDeletionRecord(string rootPath, List<string> deletedPaths)
        {
            var directory = DeletionRecordDirectory;
            if (string.IsNullOrEmpty(directory))
            {
                try
                {
                    // Plugin.Instance throws when ApplicationPaths is not initialised yet, which
                    // is why this is guarded rather than read at construction.
                    directory = Plugin.Instance?.ApplicationPaths?.LogDirectoryPath;
                }
                catch (Exception ex)
                {
                    _logger.Debug("No log directory available for the deleted-path record: {0}", ex.Message);
                    return null;
                }
            }

            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(directory);

                // Timestamp FIRST so an ordinary name sort is a chronological sort — the prune
                // below depends on that, and putting the root name first would group Movies and
                // Shows into separate runs and prune the wrong files.
                var leaf = Path.GetFileName(rootPath.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var fileName = string.Format(
                    CultureInfo.InvariantCulture,
                    "xtream-deleted-{0:yyyyMMdd-HHmmss}-{1}.txt",
                    DateTime.Now,
                    string.IsNullOrEmpty(leaf) ? "library" : SanitizeFileName(leaf));
                var path = Path.Combine(directory, fileName);

                using (var writer = new StreamWriter(path, false))
                {
                    writer.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "# {0} file(s) removed from {1} at {2:yyyy-MM-dd HH:mm:ss}",
                        deletedPaths.Count, rootPath, DateTime.Now));
                    writer.WriteLine("# Paths are relative to the root above.");
                    foreach (var deletedPath in deletedPaths)
                    {
                        writer.WriteLine(deletedPath);
                    }
                }

                PruneDeletionRecords(directory);
                return path;
            }
            catch (Exception ex)
            {
                _logger.Warn("Could not write the deleted-path record: {0}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Keeps the deleted-path records bounded. Name-sorted, which is chronological because
        /// the timestamp leads the filename.
        /// </summary>
        private void PruneDeletionRecords(string directory)
        {
            const int KeepRecords = 10;

            var existing = Directory.GetFiles(directory, "xtream-deleted-*.txt");
            if (existing.Length <= KeepRecords)
            {
                return;
            }

            Array.Sort(existing, StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < existing.Length - KeepRecords; i++)
            {
                try
                {
                    // delete-ok: removes this plugin's own diagnostic records from Emby's log
                    // directory, matched on the "xtream-deleted-*.txt" name pattern it writes
                    // itself. These are text files about the library, never library content, so
                    // the StrmOwnership check does not apply and nothing here can reach a .strm.
                    File.Delete(existing[i]);
                }
                catch (Exception ex)
                {
                    _logger.Debug("Could not prune old deleted-path record '{0}': {1}",
                        existing[i], ex.Message);
                }
            }
        }

        /// <summary>
        /// Trims the library root off a path so the deleted-file sample reads as
        /// <c>Show Name [tmdbid=1]/Season 01/....strm</c> rather than repeating the root on
        /// every entry. Falls back to the full path if it does not sit under the root.
        /// </summary>
        private static string RelativeToRoot(string fullPath, string rootPath)
        {
            if (!string.IsNullOrEmpty(rootPath)
                && fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath
                    .Substring(rootPath.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return fullPath;
        }
    }
}
