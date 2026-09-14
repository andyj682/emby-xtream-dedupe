using System;
using MediaBrowser.Model.Plugins;

namespace Emby.Xtream.Plugin
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        // Xtream connection
        public string BaseUrl { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string HttpUserAgent { get; set; } = string.Empty;

        // Live TV
        public bool EnableLiveTv { get; set; } = true;
        public string LiveTvOutputFormat { get; set; } = "ts";
        // Cap (in Mbps) applied to the MediaSource bitrate hint when no Streamflow
        // stats are available. 0 = no hint (Emby Web defaults to ~200 Mbps for "Auto"
        // bandwidth). Set to a value below your hardware encoder's cap (e.g. 8 for
        // most consumer GPUs) if transcoding falls back to software with logs like
        // "Bitrate (X Mbit/s) exceeds maximum supported rate".
        public int FallbackTranscodeBitrateMbps { get; set; }

        // EPG / Guide Data
        public EpgSourceMode EpgSource { get; set; } = EpgSourceMode.XtreamServer;
        public string CustomEpgUrl { get; set; } = string.Empty;
        public bool DeferEpgToGuideData { get; set; } = true;

        // Back-compat: migrate EnableEpg (bool) → EpgSource on first load
        [Obsolete("Use EpgSource instead")] public bool EnableEpg { get; set; } = true;
        public int EpgCacheMinutes { get; set; } = 30;
        public int EpgDaysToFetch { get; set; } = 2;
        public int M3UCacheMinutes { get; set; } = 15;

        // Category filtering
        public int[] SelectedLiveCategoryIds { get; set; } = new int[0];
        public bool IncludeAdultChannels { get; set; }

        // Channel name cleaning
        public string ChannelRemoveTerms { get; set; } = string.Empty;
        public bool EnableChannelNameCleaning { get; set; } = true;

        // Dispatcharr
        public bool EnableDispatcharr { get; set; }
        public string DispatcharrUrl { get; set; } = string.Empty;
        public string DispatcharrUser { get; set; } = string.Empty;
        public string DispatcharrPass { get; set; } = string.Empty;
        public bool DispatcharrFallbackToXtream { get; set; } = true;
        public bool ForceAudioTranscode { get; set; }
        public bool DeclareDvbSubtitles { get; set; }

        /// <summary>
        /// Where the video codec declared to Emby for Dispatcharr channels comes from.
        /// <para>
        /// <c>auto</c> (default) reads the channel's Dispatcharr stream profile and declares
        /// what that profile emits, falling back to <c>stream_stats.video_codec</c> when the
        /// profile passes video through or cannot be read. This is the fix for issue #66:
        /// <c>stream_stats.video_codec</c> is the codec Dispatcharr <em>ingested</em>, so on a
        /// transcoding profile (HEVC source, H.264 output) declaring it makes Emby force the
        /// wrong decoder and the channel fails to play.
        /// </para>
        /// <para>
        /// <c>stats</c> always declares the reported codec, the behaviour before #66, and is
        /// the escape hatch if profile detection ever reads a profile wrongly. <c>h264</c> and
        /// <c>hevc</c> declare that codec on every Dispatcharr channel, for installs where the
        /// profile endpoint is unreachable but the output is known.
        /// </para>
        /// <para>
        /// Exposed as <c>Video codec for Dispatcharr channels</c> in the Dispatcharr section of
        /// the plugin config page. An empty value (config XML written before this setting
        /// existed) is read as <c>auto</c>.
        /// </para>
        /// </summary>
        public string DispatcharrVideoCodecSource { get; set; } = "auto";
        public int[] SelectedDispatcharrProfileIds { get; set; } = new int[0];
        public string CachedDispatcharrProfiles { get; set; } = string.Empty;

        // VOD Movies
        public bool SyncMovies { get; set; }
        public string StrmLibraryPath { get; set; } = "/config/xtream";
        public int[] SelectedVodCategoryIds { get; set; } = new int[0];
        public string MovieFolderMode { get; set; } = "single";
        public string MovieFolderMappings { get; set; } = string.Empty;

        /// <summary>
        /// Xtream stream IDs the user has explicitly excluded from movie sync.
        /// Excluded items are never written, and their existing folder is deleted
        /// on the next sync (independent of <see cref="CleanupOrphans"/>).
        /// </summary>
        public int[] ExcludedVodStreamIds { get; set; } = new int[0];

        /// <summary>
        /// Hold un-reviewed titles out of the sync instead of writing them.
        ///
        /// Off by default, and deliberately so: the sync's normal contract is "anything not
        /// excluded is written", and the reviewed-checkpoint is only a triage bookmark. With
        /// this on, the contract inverts to opt-in — a title is written once it is reviewed
        /// or excluded, so a provider adding thousands of titles overnight lands them in the
        /// review queue rather than in the library.
        ///
        /// A title already on disk is exempt: the provider reassigning its id would otherwise
        /// make an established title look un-reviewed and quietly withhold it. Movies are
        /// recognised by TMDB id or folder name; series, which carry no TMDB id on the
        /// <c>get_series</c> list payload, by folder name or a stored episode hash. See the
        /// library-identity index in <c>SyncMoviesAsync</c> / <c>SyncSeriesCoreAsync</c>.
        ///
        /// Holding is NOT excluding. A held title is never added to a blocklist and its
        /// folder is never removed.
        /// </summary>
        public bool RequireReviewBeforeSync { get; set; }

        /// <summary>
        /// How many rollback copies of this configuration to keep. Zero disables them.
        /// <para>
        /// The exclusions and reviewed checkpoints below are tens of thousands of individual
        /// decisions that cannot be reconstructed, and the failure they are exposed to is a bad
        /// write through this plugin's own save path (ADR-F005). A copy is taken at the start of
        /// a sync when the file has changed since the last one, and lands beside the
        /// configuration in a <c>rollback</c> folder.
        /// </para>
        /// <para>
        /// It is a rollback, not a backup: it sits on the same volume as the file it protects,
        /// so it does nothing for a lost disk. Copy the configuration somewhere else for that.
        /// <b>These copies contain the provider username and password in plain text, exactly as
        /// the configuration itself does.</b>
        /// </para>
        /// </summary>
        public int ConfigRollbackCount { get; set; } = 5;

        /// <summary>
        /// Where the plugin keeps its durable records — configuration backups, catalog
        /// snapshots and the store-size history (ADR-F005 mechanisms 5–8). Empty means the
        /// default, an <c>xtream-backups</c> folder beside the configuration file.
        /// <para>
        /// <b>This relocates the root; it does not enable it.</b> A setting that started empty
        /// and had to be filled in would mean these records existed only for users who went
        /// looking for them — the same failure as shipping a script — and it is worst for the
        /// records that are worthless unless they have been accumulating all along.
        /// </para>
        /// <para>
        /// The point of setting it is to put the records on a <b>different volume</b> from the
        /// configuration. On the default they share a disk, so they cover a bad write and not a
        /// lost one. Note the copies contain the provider username and password in plaintext,
        /// exactly as the configuration and every written <c>.strm</c> already do.
        /// </para>
        /// </summary>
        public string RecordsPath { get; set; } = string.Empty;

        /// <summary>
        /// How many scheduled configuration backups to keep. Zero disables the backup task.
        /// <para>
        /// Separate from <see cref="ConfigRollbackCount"/> because they answer different
        /// failures: a rollback undoes the last bad write and only needs a few, while a backup
        /// covers losing the file and wants enough history to reach back past a problem nobody
        /// noticed at the time. Two weeks of daily copies at ~1.8 MB is the default.
        /// </para>
        /// </summary>
        public int ConfigBackupCount { get; set; } = 14;

        /// <summary>
        /// How many dated catalog snapshots to keep (ADR-F005 mechanism 6). Zero disables them.
        /// <para>
        /// One per day, roughly 3 MB each. The useful depth is "far enough back to predate an
        /// id-churn event nobody noticed immediately", which two weeks covers comfortably — the
        /// two real recoveries both used a snapshot under 48 hours old.
        /// </para>
        /// </summary>
        public int CatalogueSnapshotCount { get; set; } = 14;

        /// <summary>
        /// Reviewed-checkpoint: JSON array of VOD StreamIds the user has marked
        /// "reviewed" in the de-duplicated view. Stored as a JSON string (not int[])
        /// because the set grows toward the full library size; the client keeps it in
        /// a Set for O(1) lookups. A title counts as reviewed if it is in this set OR
        /// excluded (excluding implies reviewed), so existing exclusions need no
        /// migration and "unreviewed" doubles as "new since I last looked".
        /// </summary>
        public string ReviewedVodStreamIdsJson { get; set; } = string.Empty;

        /// <summary>
        /// Durable identity for movie decisions (ADR-F004 stage 3): a JSON dictionary
        /// mapping StreamId → TMDB id, covering every movie StreamId that appears in
        /// <see cref="ExcludedVodStreamIds"/> or <see cref="ReviewedVodStreamIdsJson"/>.
        /// <para>
        /// Every decision this plugin stores is keyed on the provider's StreamId, which
        /// the provider is free to re-issue — and has: one re-ingest replaced every id in
        /// a catalog, silently detaching ~9,700 decisions. Recording the TMDB id beside
        /// the decision is what lets the sync recognize the same title under a new id and
        /// carry the decision across, with no snapshot and no external repair.
        /// </para>
        /// <para>
        /// A map rather than two parallel <c>int[]</c>s deliberately: parallel arrays
        /// carry an index invariant that nothing enforces, and a mis-paired exclusion
        /// makes <c>RemoveExcludedContent</c> delete the wrong folder. A map rather than
        /// <c>tmdb:603</c> prefixes inside the existing stores, also deliberately —
        /// prefixes would cost <see cref="ExcludedVodStreamIds"/> its native
        /// <c>int[]</c> XML serialization and inflate it in the field whose size is
        /// already the concern (see ADR-F004).
        /// </para>
        /// <para>
        /// The pairing is what makes "the provider rotated this id" distinguishable from
        /// "the user withdrew this decision": an entry whose StreamId is no longer in
        /// either store means the latter, and is dropped. Series are excluded — they carry
        /// no TMDB id on the <c>get_series</c> list payload.
        /// </para>
        /// </summary>
        public string VodDecisionTmdbIdsJson { get; set; } = string.Empty;

        // Series / TV Shows
        public bool SyncSeries { get; set; }
        public int[] SelectedSeriesCategoryIds { get; set; } = new int[0];
        public string SeriesFolderMode { get; set; } = "single";
        public string SeriesFolderMappings { get; set; } = string.Empty;

        /// <summary>
        /// Xtream series IDs the user has explicitly excluded from series sync.
        /// Granularity is per-series, not per-episode.
        /// </summary>
        public int[] ExcludedSeriesIds { get; set; } = new int[0];

        /// <summary>
        /// Reviewed-checkpoint for series. Same semantics as ReviewedVodStreamIdsJson:
        /// JSON array of reviewed SeriesIds; reviewed = this set OR excluded.
        /// </summary>
        public string ReviewedSeriesIdsJson { get; set; } = string.Empty;

        // Content name cleaning
        public bool EnableContentNameCleaning { get; set; }
        public string ContentRemoveTerms { get; set; } = string.Empty;

        // TMDB folder naming
        public bool EnableTmdbFolderNaming { get; set; }
        public bool EnableTmdbFallbackLookup { get; set; }

        // Series metadata matching
        public bool EnableSeriesIdFolderNaming { get; set; }
        public bool EnableSeriesMetadataLookup { get; set; }
        public string TvdbFolderIdOverrides { get; set; } = string.Empty;

        // NFO sidecar files
        public bool EnableNfoFiles { get; set; }

        // Cached categories (JSON arrays, populated on refresh)
        public string CachedVodCategories { get; set; } = string.Empty;
        public string CachedSeriesCategories { get; set; } = string.Empty;
        public string CachedLiveCategories { get; set; } = string.Empty;

        // Update tracking
        public string LastInstalledVersion { get; set; } = string.Empty;
        public bool UseBetaChannel { get; set; }

        // Sync settings
        public bool SmartSkipExisting { get; set; } = true;
        public int SyncParallelism { get; set; } = 3;
        public bool CleanupOrphans { get; set; } = true;

        /// <summary>
        /// After a sync that added or removed files, tell Emby the corresponding library
        /// folder changed so new content appears without waiting for a scheduled scan.
        /// Only fires when something actually changed, so an unchanged sync stays silent.
        /// Mainly for libraries with real-time monitoring switched off — a common choice,
        /// since watching the folder stops the disk ever spinning down.
        /// </summary>
        public bool RefreshEmbyLibraryAfterSync { get; set; } = true;

        /// <summary>Max requests/second to the Xtream provider. 0 = disabled (no throttle).</summary>
        public int XtreamRequestsPerSecond { get; set; } = 0;

        /// <summary>Fraction of existing STRMs that can be deleted in one cleanup pass. 0 = disabled.</summary>
        public double OrphanSafetyThreshold { get; set; } = 0.20;

        // Auto-sync schedule
        public bool   AutoSyncEnabled       { get; set; } = false;
        public string AutoSyncMode          { get; set; } = "interval"; // "interval" | "daily"
        public int    AutoSyncIntervalHours { get; set; } = 24;
        public string AutoSyncDailyTime     { get; set; } = "03:00";    // HH:mm server local time

        // Sync state (persisted across restarts)
        public string LastChannelListHash { get; set; } = string.Empty;
        public long LastMovieSyncTimestamp { get; set; }
        public long LastSeriesSyncTimestamp { get; set; }
        public int StrmNamingVersion { get; set; }  // default 0; bumped when naming logic changes to force re-sync
        public string SyncHistoryJson { get; set; } = string.Empty;

        /// <summary>
        /// Tracks the one-time rename of episode STRM files to the title-free
        /// "{Show} - SxxExx.strm" form. Default 0; set to the current version once the
        /// migration has run. Deliberately separate from <see cref="StrmNamingVersion"/>,
        /// which resets the delta watermark and clears episode hashes to force a full
        /// re-sync — this migration renames files in place and needs no re-fetch.
        /// </summary>
        public int EpisodeFilenameMigrationVersion { get; set; }

        /// <summary>
        /// JSON dictionary mapping series_id → SHA256 hash of episode URLs.
        /// Used to skip per-episode file I/O when the episode list hasn't changed.
        /// </summary>
        public string SeriesEpisodeHashesJson { get; set; } = string.Empty;

        // Tracks which folder naming flags were active during the last series sync.
        // A change triggers automatic full re-sync so the pre-fetch skip can't match stale paths.
        public bool LastKnownEnableSeriesIdFolderNaming { get; set; }
        public bool LastKnownEnableSeriesMetadataLookup { get; set; }
    }

    public enum EpgSourceMode
    {
        XtreamServer = 0,
        CustomUrl    = 1,
        Disabled     = 2,
    }
}
