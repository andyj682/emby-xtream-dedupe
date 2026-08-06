define(['baseView', 'loading', 'emby-input', 'emby-select', 'emby-checkbox', 'emby-button'],
function (BaseView, loading) {
    'use strict';

    var pluginId = 'b7e3c4a1-9f2d-4e8b-a5c6-d1f0e2b3c4a5';

    // Element.closest is missing on some of the older browsers Emby still serves this
    // dashboard to. The per-title panels are re-rendered from innerHTML, so per-element
    // listeners aren't an option and the delegated handlers need a reliable walk-up.
    function closestByClass(el, className) {
        while (el && el.nodeType === 1) {
            if (el.classList
                    ? el.classList.contains(className)
                    : (' ' + el.className + ' ').indexOf(' ' + className + ' ') >= 0) {
                return el;
            }
            el = el.parentNode;
        }
        return null;
    }

    function View(view, params) {
        BaseView.apply(this, arguments);

        // Fix spacing between path validation result and Smart Skip checkbox.
        // Direct style assignment overrides any inline styles from cached HTML.
        (function () {
            var valDiv = view.querySelector('.strmPathValidationResult');
            if (valDiv) {
                valDiv.style.cssText = 'margin-top:0.25em; margin-bottom:1.2em; font-size:0.9em;';
                var next = valDiv.nextElementSibling;
                if (next) next.style.marginTop = '';
            }
        }());

        this.loadedCategories = [];
        this.selectedCategoryIds = [];
        this.loadedVodCategories = [];
        this.selectedVodCategoryIds = [];
        this.loadedSeriesCategories = [];
        this.selectedSeriesCategoryIds = [];
        this.loadedDispatcharrProfiles = [];
        this.selectedDispatcharrProfileIds = [];
        this.excludedVodStreamIds = [];
        this.excludedSeriesIds = [];
        this.contentItemsByCategory = { vod: {}, series: {} };
        this.expandedContentCategories = { vod: {}, series: {} };
        // De-duplicated review view: cached title lists + reviewed-checkpoint sets
        // (kept as id→true maps for O(1) membership; persisted as JSON id arrays).
        this.reviewedVodStreamIds = {};
        this.reviewedSeriesIds = {};
        this.dedupedVod = [];
        this.dedupedSeries = [];
        // De-dup view filters (session-only, reset each load). Show: 'all' | 'included' |
        // 'excluded'; Reviewed: 'all' | 'reviewed' | 'unreviewed'.
        this.vodDedupedShowFilter = 'all';
        this.seriesDedupedShowFilter = 'all';
        this.vodDedupedReviewedFilter = 'all';
        this.seriesDedupedReviewedFilter = 'all';

        var self = this;

        view.querySelector('.xtreamConfigForm').addEventListener('submit', function (e) {
            e.preventDefault();
            saveConfig(self);
        });

        view.querySelector('.chkEnableNameCleaning').addEventListener('change', function () {
            updateNameCleaningVisibility(view);
        });

        view.querySelector('.chkEnableTmdbFolderNaming').addEventListener('change', function () {
            updateTmdbVisibility(view);
        });

        view.querySelector('.chkEnableDispatcharr').addEventListener('change', function () {
            updateDispatcharrVisibility(view);
        });


        view.querySelector('.selectEpgSource').addEventListener('change', function () {
            updateEpgVisibility(view);
        });

        view.querySelector('.chkSyncMovies').addEventListener('change', function () {
            updateVodMovieVisibility(view);
        });

        view.querySelector('.chkSyncSeries').addEventListener('change', function () {
            updateSeriesVisibility(view);
        });

        view.querySelector('.selMovieFolderMode').addEventListener('change', function () {
            updateFoldersVisibility(view, 'movie');
        });

        view.querySelector('.selSeriesFolderMode').addEventListener('change', function () {
            updateFoldersVisibility(view, 'series');
        });

        view.querySelector('.chkAutoSyncEnabled').addEventListener('change', function () {
            updateAutoSyncVisibility(view);
        });

        view.querySelector('.selAutoSyncMode').addEventListener('change', function () {
            updateAutoSyncVisibility(view);
        });

        view.querySelector('.btnAddMovieFolder').addEventListener('click', function () {
            addFolderEntry(view, 'movie', '', '', self.loadedVodCategories);
        });

        view.querySelector('.btnAddSeriesFolder').addEventListener('click', function () {
            addFolderEntry(view, 'series', '', '', self.loadedSeriesCategories);
        });

        view.querySelector('.txtStrmLibraryPath').addEventListener('blur', function () {
            validateStrmPath(view);
        });

        view.querySelector('.btnBrowseStrmPath').addEventListener('click', function () {
            openBrowser(view);
        });

        view.querySelector('.btnCloseBrowser').addEventListener('click', function () {
            closeBrowser(view);
        });

        view.querySelector('.btnBrowserCancel').addEventListener('click', function () {
            closeBrowser(view);
        });

        view.querySelector('.btnBrowserOk').addEventListener('click', function () {
            var path = (view.querySelector('.txtBrowserCurrentPath').value || '').trim();
            if (path) {
                view.querySelector('.txtStrmLibraryPath').value = path;
                validateStrmPath(view);
            }
            closeBrowser(view);
        });

        view.querySelector('.txtBrowserCurrentPath').addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { e.preventDefault(); browserNavigate(view, this.value.trim() || null); }
        });

        view.querySelector('.strmBrowserModal').addEventListener('click', function (e) {
            if (e.target === this) closeBrowser(view);
        });

        view.querySelector('.btnTestConnection').addEventListener('click', function () {
            testXtreamConnection(self);
        });

        view.querySelector('.btnTestDispatcharr').addEventListener('click', function () {
            testDispatcharrConnection(self);
        });

        view.querySelector('.btnRefreshProfiles').addEventListener('click', function () {
            loadDispatcharrProfiles(self);
        });

        view.querySelector('.btnSelectAllProfiles').addEventListener('click', function () {
            toggleAllProfiles(view, true);
        });

        view.querySelector('.btnDeselectAllProfiles').addEventListener('click', function () {
            toggleAllProfiles(view, false);
        });

        view.querySelector('.btnLoadCategories').addEventListener('click', function () {
            loadCategories(self);
        });

        view.querySelector('.btnSelectAllCategories').addEventListener('click', function () {
            toggleAllCategories(view, true);
        });

        view.querySelector('.btnDeselectAllCategories').addEventListener('click', function () {
            toggleAllCategories(view, false);
        });

        view.querySelector('.btnRefreshCache').addEventListener('click', function () {
            refreshCache(view);
        });

        view.querySelector('.btnSyncGuideMappings').addEventListener('click', function () {
            syncGuideMappings(view);
        });

        // VOD category buttons (single mode)
        view.querySelector('.btnLoadVodCategories').addEventListener('click', function () {
            loadVodCategories(self);
        });

        view.querySelector('.btnSelectAllVodCategories').addEventListener('click', function () {
            toggleAllVodCategories(self, true);
            setDedupedCatNudge(self, 'vod', true);
        });

        view.querySelector('.btnDeselectAllVodCategories').addEventListener('click', function () {
            toggleAllVodCategories(self, false);
            setDedupedCatNudge(self, 'vod', true);
        });

        // VOD category buttons (multi mode)
        view.querySelector('.btnLoadVodCategoriesMulti').addEventListener('click', function () {
            loadVodCategoriesMulti(self);
        });

        // Series category buttons (single mode)
        view.querySelector('.btnLoadSeriesCategories').addEventListener('click', function () {
            loadSeriesCategories(self);
        });

        view.querySelector('.btnSelectAllSeriesCategories').addEventListener('click', function () {
            toggleAllSeriesCategories(self, true);
            setDedupedCatNudge(self, 'series', true);
        });

        view.querySelector('.btnDeselectAllSeriesCategories').addEventListener('click', function () {
            toggleAllSeriesCategories(self, false);
            setDedupedCatNudge(self, 'series', true);
        });

        // Per-title selection: delegated so re-rendered panels stay live
        ['.vodCategoriesList', '.seriesCategoriesList'].forEach(function (selector) {
            var listEl = view.querySelector(selector);
            if (!listEl) return;

            listEl.addEventListener('click', function (e) {
                var toggle = closestByClass(e.target, 'contentItemToggle');
                if (toggle) {
                    e.preventDefault();
                    toggleContentItemPanel(
                        self,
                        toggle.getAttribute('data-content-type'),
                        parseInt(toggle.getAttribute('data-cat-id'), 10),
                        toggle);
                    return;
                }

                var selectAll = closestByClass(e.target, 'contentItemSelectAll');
                if (selectAll) {
                    e.preventDefault();
                    toggleAllContentItems(
                        self,
                        selectAll.getAttribute('data-content-type'),
                        parseInt(selectAll.getAttribute('data-cat-id'), 10),
                        true);
                    return;
                }

                var deselectAll = closestByClass(e.target, 'contentItemDeselectAll');
                if (deselectAll) {
                    e.preventDefault();
                    toggleAllContentItems(
                        self,
                        deselectAll.getAttribute('data-content-type'),
                        parseInt(deselectAll.getAttribute('data-cat-id'), 10),
                        false);
                }
            });

            listEl.addEventListener('change', function (e) {
                var cb = closestByClass(e.target, 'contentItemCheckbox');
                if (!cb) return;
                setContentExclusion(
                    self,
                    cb.getAttribute('data-content-type'),
                    parseInt(cb.getAttribute('data-item-id'), 10),
                    !cb.checked);
            });
        });

        // Series category buttons (multi mode)
        view.querySelector('.btnLoadSeriesCategoriesMulti').addEventListener('click', function () {
            loadSeriesCategoriesMulti(self);
        });

        // Sync buttons
        view.querySelector('.btnSyncMovies').addEventListener('click', function () {
            syncMovies(view);
        });

        view.querySelector('.btnSyncSeries').addEventListener('click', function () {
            syncSeries(view);
        });

        // Delete content buttons
        view.querySelector('.btnDeleteMovies').addEventListener('click', function () {
            deleteContent(view, 'Movies');
        });

        view.querySelector('.btnDeleteSeries').addEventListener('click', function () {
            deleteContent(view, 'Series');
        });

        // De-duplicated review views (Movies + Series) — same blocklist as the tree above
        wireDedupedView(view, self, 'vod');
        wireDedupedView(view, self, 'series');
        setupDedupMode(view, self, 'vod');
        setupDedupMode(view, self, 'series');

        view.querySelector('.chkCleanupOrphans').addEventListener('change', function () {
            view.querySelector('.orphanThresholdContainer').style.display = this.checked ? '' : 'none';
        });

        // Dashboard sync all button
        view.querySelector('.btnDashboardSyncAll').addEventListener('click', function () {
            dashboardSyncAll(self);
        });

        // Retry failed items button
        view.querySelector('.btnRetryFailed').addEventListener('click', function () {
            retryFailed(view);
        });

        // Download sanitized log button
        view.querySelector('.btnDownloadLog').addEventListener('click', function () {
            window.open(ApiClient.getUrl('XtreamTuner/Logs') + '?api_key=' + ApiClient.accessToken(), '_blank');
        });

        // Dismiss update banner
        view.querySelector('.updateBannerDismiss').addEventListener('click', function () {
            view.querySelector('.updateBanner').style.display = 'none';
        });

        // Beta channel toggle — save immediately then re-check for updates
        view.querySelector('.chkUseBetaChannel').addEventListener('change', function () {
            saveConfig(self, function () { checkForUpdate(view); });
        });

        // Install update button
        view.querySelector('.btnInstallUpdate').addEventListener('click', function () {
            installUpdate(view);
        });

        // Restart Emby button
        view.querySelector('.btnRestartEmby').addEventListener('click', function () {
            restartEmby(view);
        });

        // Danger zone toggles (event delegation on form)
        view.querySelector('.xtreamConfigForm').addEventListener('click', function (e) {
            var header = closestByClass(e.target, 'danger-zone-header');
            if (!header) return;
            var zone = header.parentNode;
            zone.classList.toggle('open');
            var arrow = header.querySelector('.danger-zone-arrow');
            if (arrow) arrow.textContent = zone.classList.contains('open') ? '\u25BC' : '\u25B6';
        });

        // Category search filters
        setupCategorySearch(view, '.vodCategorySearch', '.vodCategoriesList');
        setupCategorySearch(view, '.seriesCategorySearch', '.seriesCategoriesList');
        setupCategorySearch(view, '.liveCategorySearch', '.categoriesList');

        // Category checkbox change — live count badge updates
        view.querySelector('.vodCategoriesContainer').addEventListener('change', function (e) {
            if (e.target.classList.contains('vodCategoryCheckbox')) {
                updateCategoryCountBadge(view, 'vod');
                setDedupedCatNudge(self, 'vod', true);
            }
        });
        view.querySelector('.seriesCategoriesContainer').addEventListener('change', function (e) {
            if (e.target.classList.contains('seriesCategoryCheckbox')) {
                updateCategoryCountBadge(view, 'series');
                setDedupedCatNudge(self, 'series', true);
            }
        });
        view.querySelector('.categoriesContainer').addEventListener('change', function (e) {
            if (e.target.classList.contains('categoryCheckbox')) {
                updateCategoryCountBadge(view, 'live');
            }
        });

        // Folder mode visual cards
        initFolderModeCards(view, 'movie');
        initFolderModeCards(view, 'series');

        // Empty-state "Go to Settings" button
        var btnGoToSettings = view.querySelector('.btnGoToSettings');
        if (btnGoToSettings) {
            btnGoToSettings.addEventListener('click', function () {
                switchTab(view, 'generic');
            });
        }

        // Tab buttons
        var tabBtns = view.querySelectorAll('.tabBtn');
        for (var i = 0; i < tabBtns.length; i++) {
            tabBtns[i].addEventListener('click', function () {
                var tab = this.getAttribute('data-tab');
                switchTab(view, tab);
                if (tab === 'dashboard') {
                    loadDashboard(view);
                }
                // Auto-load categories on tab switch if not already loaded
                if (tab === 'movies' && self.loadedVodCategories.length === 0) {
                    loadVodCategories(self);
                }
                if (tab === 'series' && self.loadedSeriesCategories.length === 0) {
                    loadSeriesCategories(self);
                }
                if (tab === 'liveTv' && self.loadedCategories.length === 0) {
                    loadCategories(self);
                }
            });
        }
        switchTab(view, 'dashboard');
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        loadConfig(this);
        loadDashboard(this.view);
        checkForUpdate(this.view);
    };

    View.prototype.onPause = function () {};

    function loadConfig(instance) {
        loading.show();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            var view = instance.view;

            view.querySelector('.txtBaseUrl').value = config.BaseUrl || '';
            view.querySelector('.txtUsername').value = config.Username || '';
            view.querySelector('.txtPassword').value = config.Password || '';
            view.querySelector('.txtHttpUserAgent').value = config.HttpUserAgent || '';

            view.querySelector('.chkEnableLiveTv').checked = config.EnableLiveTv !== false;
            view.querySelector('.selOutputFormat').value = config.LiveTvOutputFormat || 'ts';
            view.querySelector('.txtFallbackTranscodeBitrate').value = (config.FallbackTranscodeBitrateMbps | 0);
            view.querySelector('.chkIncludeAdult').checked = !!config.IncludeAdultChannels;

            var epgVal = config.EpgSource;
            var epgNameToInt = { 'XtreamServer': '0', 'CustomUrl': '1', 'Disabled': '2' };
            view.querySelector('.selectEpgSource').value = epgNameToInt[epgVal] || (epgVal || 0).toString();
            view.querySelector('.txtCustomEpgUrl').value = config.CustomEpgUrl || '';
            view.querySelector('.chkDeferEpgToGuideData').checked = config.DeferEpgToGuideData !== false;
            view.querySelector('.txtEpgCacheMinutes').value = config.EpgCacheMinutes || 30;
            view.querySelector('.txtEpgDaysToFetch').value = config.EpgDaysToFetch || 2;
            view.querySelector('.txtM3UCacheMinutes').value = config.M3UCacheMinutes || 15;

            instance.selectedCategoryIds = config.SelectedLiveCategoryIds || [];

            // Unified name cleaning (drives both content + channel cleaning)
            var nameCleaningEnabled = !!config.EnableContentNameCleaning || !!config.EnableChannelNameCleaning;
            view.querySelector('.chkEnableNameCleaning').checked = nameCleaningEnabled;
            var removeTerms = config.ContentRemoveTerms || '';
            if (!removeTerms && config.ChannelRemoveTerms) {
                removeTerms = config.ChannelRemoveTerms.split(',').map(function (t) { return t.trim(); }).filter(function (t) { return t; }).join('\n');
            }
            view.querySelector('.txtRemoveTerms').value = removeTerms;

            view.querySelector('.chkEnableDispatcharr').checked = !!config.EnableDispatcharr;
            view.querySelector('.txtDispatcharrUrl').value = config.DispatcharrUrl || '';
            view.querySelector('.txtDispatcharrUser').value = config.DispatcharrUser || '';
            view.querySelector('.txtDispatcharrPass').value = config.DispatcharrPass || '';
            view.querySelector('.chkDispatcharrFallback').checked = config.DispatcharrFallbackToXtream !== false;
            view.querySelector('.chkForceAudioTranscode').checked = !!config.ForceAudioTranscode;
            view.querySelector('.chkDeclareDvbSubtitles').checked = !!config.DeclareDvbSubtitles;

            instance.selectedDispatcharrProfileIds = config.SelectedDispatcharrProfileIds || [];
            loadCachedDispatcharrProfiles(instance, config);

            // Pre-parse cached categories so folder cards render correctly from the start
            var cachedVodCats = null;
            if (config.CachedVodCategories) {
                try { cachedVodCats = JSON.parse(config.CachedVodCategories); } catch (e) {}
            }
            var cachedSeriesCats = null;
            if (config.CachedSeriesCategories) {
                try { cachedSeriesCats = JSON.parse(config.CachedSeriesCategories); } catch (e) {}
            }

            // VOD Movies
            view.querySelector('.chkSyncMovies').checked = !!config.SyncMovies;
            var movieMode = config.MovieFolderMode || 'single';
            if (movieMode === 'multiple') movieMode = 'custom';
            view.querySelector('.selMovieFolderMode').value = movieMode;
            loadFolderEntries(view, 'movie', config.MovieFolderMappings || '', cachedVodCats);
            instance.selectedVodCategoryIds = config.SelectedVodCategoryIds || [];
            instance.excludedVodStreamIds = config.ExcludedVodStreamIds || [];
            instance.reviewedVodStreamIds = parseReviewedSet(config.ReviewedVodStreamIdsJson);

            // Series
            view.querySelector('.chkSyncSeries').checked = !!config.SyncSeries;
            var seriesMode = config.SeriesFolderMode || 'single';
            if (seriesMode === 'multiple') seriesMode = 'custom';
            view.querySelector('.selSeriesFolderMode').value = seriesMode;
            loadFolderEntries(view, 'series', config.SeriesFolderMappings || '', cachedSeriesCats);
            instance.selectedSeriesCategoryIds = config.SelectedSeriesCategoryIds || [];
            instance.excludedSeriesIds = config.ExcludedSeriesIds || [];
            instance.reviewedSeriesIds = parseReviewedSet(config.ReviewedSeriesIdsJson);

            // Update channel
            view.querySelector('.chkUseBetaChannel').checked = !!config.UseBetaChannel;

            // Sync settings
            view.querySelector('.txtStrmLibraryPath').value = config.StrmLibraryPath || '/config/xtream';
            validateStrmPath(view);
            view.querySelector('.chkSmartSkipExisting').checked = config.SmartSkipExisting !== false;
            view.querySelector('.chkRefreshDispatcharrEpisodes').checked = !!config.RefreshDispatcharrEpisodes;
            view.querySelector('.txtSyncParallelism').value = config.SyncParallelism || 3;
            view.querySelector('.txtXtreamRequestsPerSecond').value = config.XtreamRequestsPerSecond || 0;
            view.querySelector('.chkCleanupOrphans').checked = !!config.CleanupOrphans;
            view.querySelector('.txtOrphanSafetyThreshold').value = Math.round((config.OrphanSafetyThreshold != null ? config.OrphanSafetyThreshold : 0.20) * 100);
            view.querySelector('.orphanThresholdContainer').style.display = config.CleanupOrphans ? '' : 'none';
            view.querySelector('.chkEnableNfoFiles').checked = !!config.EnableNfoFiles;

            // Auto-sync schedule
            view.querySelector('.chkAutoSyncEnabled').checked = !!config.AutoSyncEnabled;
            view.querySelector('.selAutoSyncMode').value = config.AutoSyncMode || 'interval';
            view.querySelector('.txtAutoSyncIntervalHours').value = config.AutoSyncIntervalHours || 24;
            view.querySelector('.txtAutoSyncDailyTime').value = config.AutoSyncDailyTime || '03:00';
            updateAutoSyncVisibility(view);

            // Metadata ID naming (unified)
            var metadataIdEnabled = !!config.EnableTmdbFolderNaming || !!config.EnableSeriesIdFolderNaming;
            view.querySelector('.chkEnableTmdbFolderNaming').checked = metadataIdEnabled;
            var fallbackEnabled = !!config.EnableTmdbFallbackLookup || !!config.EnableSeriesMetadataLookup;
            view.querySelector('.chkEnableTmdbFallbackLookup').checked = fallbackEnabled;
            view.querySelector('.txtTvdbFolderIdOverrides').value = config.TvdbFolderIdOverrides || '';

            updateTmdbVisibility(view);
            updateNameCleaningVisibility(view);
            updateDispatcharrVisibility(view);
            updateEpgVisibility(view);
            updateVodMovieVisibility(view);
            updateSeriesVisibility(view);
            updateFoldersVisibility(view, 'movie');
            updateFoldersVisibility(view, 'series');

            // Sync folder mode card visuals to loaded select values
            syncFolderModeCards(view, 'movie');
            syncFolderModeCards(view, 'series');

            // Health bar, auto-sync line, empty-state
            renderHealthBar(view, config);
            renderAutoSyncDashboardLine(view, config);
            updateDashboardEmptyState(view, config);

            loading.hide();

            // Load cached categories from config (instant, no API call)
            loadCachedCategories(instance, config);
        }).catch(function (err) {
            loading.hide();
            console.error('Xtream: failed to load plugin configuration', err);
        });
    }

    function saveConfig(instance, callback) {
        loading.show();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            var view = instance.view;

            config.BaseUrl = view.querySelector('.txtBaseUrl').value.replace(/\/+$/, '');
            config.Username = view.querySelector('.txtUsername').value;
            var _pwdVal = view.querySelector('.txtPassword').value;
            if (_pwdVal) config.Password = _pwdVal;
            config.HttpUserAgent = view.querySelector('.txtHttpUserAgent').value;

            config.EnableLiveTv = view.querySelector('.chkEnableLiveTv').checked;
            config.LiveTvOutputFormat = view.querySelector('.selOutputFormat').value;
            config.FallbackTranscodeBitrateMbps = Math.max(0, parseInt(view.querySelector('.txtFallbackTranscodeBitrate').value, 10) || 0);
            config.IncludeAdultChannels = view.querySelector('.chkIncludeAdult').checked;

            config.EpgSource = parseInt(view.querySelector('.selectEpgSource').value, 10);
            config.CustomEpgUrl = view.querySelector('.txtCustomEpgUrl').value.trim();
            config.DeferEpgToGuideData = view.querySelector('.chkDeferEpgToGuideData').checked;
            config.EpgCacheMinutes = parseInt(view.querySelector('.txtEpgCacheMinutes').value, 10) || 30;
            config.EpgDaysToFetch = parseInt(view.querySelector('.txtEpgDaysToFetch').value, 10) || 2;
            config.M3UCacheMinutes = parseInt(view.querySelector('.txtM3UCacheMinutes').value, 10) || 15;

            config.SelectedLiveCategoryIds = getSelectedCategoryIds(instance);

            // Unified name cleaning → both backend properties
            var nameCleaningOn = view.querySelector('.chkEnableNameCleaning').checked;
            var removeTermsVal = view.querySelector('.txtRemoveTerms').value;
            config.EnableContentNameCleaning = nameCleaningOn;
            config.EnableChannelNameCleaning = nameCleaningOn;
            config.ContentRemoveTerms = removeTermsVal;
            config.ChannelRemoveTerms = removeTermsVal.split('\n').map(function (t) { return t.trim(); }).filter(function (t) { return t; }).join(',');

            config.EnableDispatcharr = view.querySelector('.chkEnableDispatcharr').checked;
            config.DispatcharrUrl = view.querySelector('.txtDispatcharrUrl').value.replace(/\/+$/, '');
            config.DispatcharrUser = view.querySelector('.txtDispatcharrUser').value;
            var _dPwdVal = view.querySelector('.txtDispatcharrPass').value;
            if (_dPwdVal) config.DispatcharrPass = _dPwdVal;
            config.DispatcharrFallbackToXtream = view.querySelector('.chkDispatcharrFallback').checked;
            config.ForceAudioTranscode = view.querySelector('.chkForceAudioTranscode').checked;
            config.DeclareDvbSubtitles = view.querySelector('.chkDeclareDvbSubtitles').checked;
            config.SelectedDispatcharrProfileIds = getSelectedDispatcharrProfileIds(instance);

            // VOD Movies
            config.SyncMovies = view.querySelector('.chkSyncMovies').checked;
            config.MovieFolderMode = view.querySelector('.selMovieFolderMode').value;
            config.MovieFolderMappings = serializeFolderEntries(view, 'movie');
            config.SelectedVodCategoryIds = getSelectedVodCategoryIds(instance);
            config.ExcludedVodStreamIds = instance.excludedVodStreamIds.slice();
            config.ReviewedVodStreamIdsJson = serializeReviewedSet(instance.reviewedVodStreamIds);

            // Series
            config.SyncSeries = view.querySelector('.chkSyncSeries').checked;
            config.SeriesFolderMode = view.querySelector('.selSeriesFolderMode').value;
            config.SeriesFolderMappings = serializeFolderEntries(view, 'series');
            config.SelectedSeriesCategoryIds = getSelectedSeriesCategoryIds(instance);
            config.ExcludedSeriesIds = instance.excludedSeriesIds.slice();
            config.ReviewedSeriesIdsJson = serializeReviewedSet(instance.reviewedSeriesIds);

            // Update channel
            config.UseBetaChannel = view.querySelector('.chkUseBetaChannel').checked;

            // Sync settings
            config.StrmLibraryPath = view.querySelector('.txtStrmLibraryPath').value.replace(/\/+$/, '') || '/config/xtream';
            config.SmartSkipExisting = view.querySelector('.chkSmartSkipExisting').checked;
            config.RefreshDispatcharrEpisodes = view.querySelector('.chkRefreshDispatcharrEpisodes').checked;
            config.SyncParallelism = parseInt(view.querySelector('.txtSyncParallelism').value, 10) || 3;
            config.XtreamRequestsPerSecond = parseInt(view.querySelector('.txtXtreamRequestsPerSecond').value, 10) || 0;
            config.CleanupOrphans = view.querySelector('.chkCleanupOrphans').checked;
            config.OrphanSafetyThreshold = (parseInt(view.querySelector('.txtOrphanSafetyThreshold').value, 10) || 0) / 100;
            config.EnableNfoFiles = view.querySelector('.chkEnableNfoFiles').checked;

            // Auto-sync schedule
            config.AutoSyncEnabled = view.querySelector('.chkAutoSyncEnabled').checked;
            config.AutoSyncMode = view.querySelector('.selAutoSyncMode').value;
            config.AutoSyncIntervalHours = parseInt(view.querySelector('.txtAutoSyncIntervalHours').value, 10) || 24;
            config.AutoSyncDailyTime = view.querySelector('.txtAutoSyncDailyTime').value || '03:00';

            // Metadata ID naming (unified → both backend properties)
            var metadataIdOn = view.querySelector('.chkEnableTmdbFolderNaming').checked;
            config.EnableTmdbFolderNaming = metadataIdOn;
            config.EnableSeriesIdFolderNaming = metadataIdOn;
            var fallbackOn = view.querySelector('.chkEnableTmdbFallbackLookup').checked;
            config.EnableTmdbFallbackLookup = fallbackOn;
            config.EnableSeriesMetadataLookup = fallbackOn;
            config.TvdbFolderIdOverrides = view.querySelector('.txtTvdbFolderIdOverrides').value;

            ApiClient.updatePluginConfiguration(pluginId, config).then(function () {
                Dashboard.processPluginConfigurationUpdateResult();
                applyScheduleToTasks(view, config, ApiClient);
                setDedupedCatNudge(instance, 'vod', false);
                setDedupedCatNudge(instance, 'series', false);
                ['vod', 'series'].forEach(function (t) {
                    var h = instance.view.querySelector('.' + t + 'DedupedHealNotice');
                    if (h) h.style.display = 'none';
                });
                if (typeof callback === 'function') callback();
            });
        }).catch(function () {
            loading.hide();
            Dashboard.alert('Failed to save configuration.');
        });
    }

    function switchTab(view, tabName) {
        var panels = view.querySelectorAll('.tabPanel');
        for (var i = 0; i < panels.length; i++) {
            panels[i].style.display = 'none';
        }

        var btns = view.querySelectorAll('.tabBtn');
        for (var i = 0; i < btns.length; i++) {
            btns[i].style.opacity = '0.7';
            btns[i].style.borderBottomColor = 'transparent';
        }

        var panelMap = { dashboard: '.tabDashboard', generic: '.tabGeneric', movies: '.tabMovies', series: '.tabSeries', liveTv: '.tabLiveTv' };
        var btnMap = { dashboard: '.tabBtnDashboard', generic: '.tabBtnGeneric', movies: '.tabBtnMovies', series: '.tabBtnSeries', liveTv: '.tabBtnLiveTv' };

        var panel = view.querySelector(panelMap[tabName]);
        if (panel) panel.style.display = 'block';

        var btn = view.querySelector(btnMap[tabName]);
        if (btn) {
            btn.style.opacity = '1';
            btn.style.borderBottomColor = '#52B54B';
        }

        // Hide Save button on Dashboard — nothing to save there
        var footer = view.querySelector('.stickyFooter');
        if (footer) footer.style.display = tabName === 'dashboard' ? 'none' : '';
    }

    function updateTmdbVisibility(view) {
        var enabled = view.querySelector('.chkEnableTmdbFolderNaming').checked;
        view.querySelector('.tmdbSettings').style.display = enabled ? '' : 'none';
    }

    function updateDispatcharrVisibility(view) {
        var enabled = view.querySelector('.chkEnableDispatcharr').checked;
        view.querySelector('.dispatcharrSettings').style.display = enabled ? '' : 'none';
    }

function updateEpgVisibility(view) {
        var source = parseInt(view.querySelector('.selectEpgSource').value, 10);
        view.querySelector('.epgSettings').style.display = source !== 2 ? '' : 'none';
        view.querySelector('.epgCustomUrlSettings').style.display = source === 1 ? '' : 'none';
    }

    function updateNameCleaningVisibility(view) {
        var enabled = view.querySelector('.chkEnableNameCleaning').checked;
        view.querySelector('.nameCleaningSettings').style.display = enabled ? '' : 'none';
    }

    function updateVodMovieVisibility(view) {
        var enabled = view.querySelector('.chkSyncMovies').checked;
        view.querySelector('.vodMovieSettings').style.display = enabled ? '' : 'none';
    }

    function updateSeriesVisibility(view) {
        var enabled = view.querySelector('.chkSyncSeries').checked;
        view.querySelector('.seriesSettings').style.display = enabled ? '' : 'none';
    }

    function updateFoldersVisibility(view, type) {
        var selClass    = type === 'movie' ? '.selMovieFolderMode'      : '.selSeriesFolderMode';
        var singleClass = type === 'movie' ? '.movieSingleContainer'    : '.seriesSingleContainer';
        var multiClass  = type === 'movie' ? '.movieFoldersContainer'   : '.seriesFoldersContainer';
        var listClass   = type === 'movie' ? '.movieFoldersList'        : '.seriesFoldersList';
        var addBtnClass = type === 'movie' ? '.btnAddMovieFolder'       : '.btnAddSeriesFolder';
        var mode = view.querySelector(selClass).value;
        var isMulti = mode === 'custom';
        view.querySelector(singleClass).style.display  = isMulti ? 'none'  : 'block';
        view.querySelector(multiClass).style.display   = isMulti ? 'block' : 'none';
        view.querySelector(listClass).style.display    = isMulti ? ''      : 'none';
        view.querySelector(addBtnClass).style.display  = isMulti ? ''      : 'none';
        updateMultiFolderEmptyHints(view);
    }

    function updateMultiFolderEmptyHints(view) {
        function one(type) {
            var selClass = type === 'movie' ? '.selMovieFolderMode' : '.selSeriesFolderMode';
            var listClass = type === 'movie' ? '.movieFoldersList' : '.seriesFoldersList';
            var hintClass = type === 'movie' ? '.movieMultiFolderEmptyHint' : '.seriesMultiFolderEmptyHint';
            var mode = view.querySelector(selClass).value;
            var list = view.querySelector(listClass);
            var hint = view.querySelector(hintClass);
            if (!hint || !list) return;
            var isMulti = mode === 'custom';
            var hasCards = list.querySelectorAll('.folderCard').length > 0;
            hint.style.display = (isMulti && !hasCards) ? 'block' : 'none';
        }
        one('movie');
        one('series');
    }

    function updateAutoSyncVisibility(v) {
        var enabled = v.querySelector('.chkAutoSyncEnabled').checked;
        v.querySelector('.autoSyncSettings').style.display = enabled ? '' : 'none';
        var mode = v.querySelector('.selAutoSyncMode').value;
        v.querySelector('.autoSyncIntervalContainer').style.display = mode === 'interval' ? '' : 'none';
        v.querySelector('.autoSyncDailyContainer').style.display    = mode === 'daily'    ? '' : 'none';
    }

    function buildTriggers(config) {
        if (!config.AutoSyncEnabled) return [];
        if (config.AutoSyncMode === 'daily') {
            var parts = (config.AutoSyncDailyTime || '03:00').split(':');
            var ticks = (parseInt(parts[0], 10) * 3600 + parseInt(parts[1] || '0', 10) * 60) * 10000000;
            return [{ Type: 'DailyTrigger', TimeOfDayTicks: ticks }];
        }
        // interval
        var hours = Math.max(1, config.AutoSyncIntervalHours || 24);
        return [{ Type: 'IntervalTrigger', IntervalTicks: hours * 36000000000 }];
    }

    function applyScheduleToTasks(view, config, apiClient) {
        apiClient.getJSON(apiClient.getUrl('ScheduledTasks'))
            .then(function (tasks) {
                if (!Array.isArray(tasks)) return;
                var xtreamTasks = tasks.filter(function (t) {
                    return t.Category === 'Xtream Tuner';
                });
                var triggers = buildTriggers(config);
                xtreamTasks.forEach(function (task) {
                    apiClient.ajax({
                        url: apiClient.getUrl('ScheduledTasks/' + task.Id + '/Triggers'),
                        type: 'POST',
                        contentType: 'application/json',
                        data: JSON.stringify(triggers)
                    });
                });
            });
    }

    // ---- Folder card management (for Multiple Folders mode) ----

    function addFolderEntry(view, type, name, checkedIdsStr, categories) {
        var listClass = type === 'movie' ? '.movieFoldersList' : '.seriesFoldersList';
        var list = view.querySelector(listClass);

        var card = document.createElement('div');
        card.className = 'folderCard';
        card.setAttribute('data-checked-ids', checkedIdsStr || '');
        card.style.cssText = 'background:rgba(128,128,128,0.04); border:1px solid rgba(128,128,128,0.15); border-radius:8px; padding:1.2em 1.4em; margin-bottom:1em;';

        // Header: name input + remove button
        var header = document.createElement('div');
        header.style.cssText = 'display:flex; gap:0.5em; align-items:center; margin-bottom:0.5em;';

        // Use a <textarea> instead of <input> to avoid Emby's HTMLBuiltIn
        // polyfill which intercepts all <input> elements and adds per-keystroke
        // overhead that causes severe input lag.
        var nameInput = document.createElement('textarea');
        nameInput.rows = 1;
        nameInput.className = 'folderCardName';
        nameInput.placeholder = 'e.g. Drama';
        nameInput.value = name;
        nameInput.style.cssText = 'flex:1; padding:0.5em 0.8em; background:transparent; border:1px solid rgba(128,128,128,0.25); border-radius:4px; color:inherit; font-size:1em; resize:none; overflow:hidden; line-height:1.4em; font-family:inherit; field-sizing:content;';
        nameInput.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') e.preventDefault();
        });

        var removeBtn = document.createElement('button');
        removeBtn.type = 'button';
        removeBtn.textContent = 'Remove';
        removeBtn.style.cssText = 'background:#c0392b; color:white; border:none; border-radius:4px; padding:0.5em 1em; cursor:pointer; font-size:0.9em;';
        removeBtn.addEventListener('click', function () {
            card.parentNode.removeChild(card);
            updateMultiFolderEmptyHints(view);
        });

        header.appendChild(nameInput);
        header.appendChild(removeBtn);
        card.appendChild(header);

        // Category checkboxes container
        var catContainer = document.createElement('div');
        catContainer.className = 'folderCardCategories';
        catContainer.style.cssText = 'max-height:300px; overflow-y:auto; border:1px solid rgba(128,128,128,0.15); border-radius:4px; padding:0.5em;';

        if (categories && categories.length > 0) {
            renderFolderCardCategories(catContainer, categories, checkedIdsStr);
        } else if (categories !== null && categories !== undefined) {
            catContainer.innerHTML = '<div style="opacity:0.5; padding:0.5em;">No categories available from server. Click Refresh Categories to try again.</div>';
        } else {
            catContainer.innerHTML = '<div style="opacity:0.5; padding:0.5em;">Loading categories...</div>';
        }

        card.appendChild(catContainer);
        list.appendChild(card);
        updateMultiFolderEmptyHints(view);
    }

    function renderFolderCardCategories(container, categories, checkedIdsStr) {
        var checkedIds = [];
        if (checkedIdsStr) {
            var parts = checkedIdsStr.split(',');
            for (var i = 0; i < parts.length; i++) {
                var n = parseInt(parts[i].trim(), 10);
                if (!isNaN(n)) checkedIds.push(n);
            }
        }

        var html = '';
        for (var i = 0; i < categories.length; i++) {
            var cat = categories[i];
            var checked = checkedIds.indexOf(cat.CategoryId) >= 0 ? ' checked' : '';
            html += '<div class="checkboxContainer" style="margin:0.3em 0; padding:0.2em 0.5em;">';
            html += '<label style="display:flex; align-items:center; cursor:pointer;">';
            html += '<input type="checkbox" class="folderCategoryCheckbox" data-category-id="' + cat.CategoryId + '"' + checked + ' style="margin-right:0.5em;" />';
            html += '<span>' + escapeHtml(cat.CategoryName) + ' <span style="opacity:0.5;">(ID: ' + cat.CategoryId + ')</span></span>';
            html += '</label>';
            html += '</div>';
        }
        container.innerHTML = html;
    }

    function clearFolderCardCategories(view, type) {
        var listClass = type === 'movie' ? '.movieFoldersList' : '.seriesFoldersList';
        var cards = view.querySelectorAll(listClass + ' .folderCard');
        for (var i = 0; i < cards.length; i++) {
            var catContainer = cards[i].querySelector('.folderCardCategories');
            catContainer.innerHTML = '<div style="opacity:0.5; padding:0.5em;">No categories available from server.</div>';
        }
    }

    function populateFolderCheckboxes(view, type, categories) {
        var listClass = type === 'movie' ? '.movieFoldersList' : '.seriesFoldersList';
        var cards = view.querySelectorAll(listClass + ' .folderCard');
        for (var i = 0; i < cards.length; i++) {
            var card = cards[i];
            var checkedIdsStr = card.getAttribute('data-checked-ids') || '';
            var catContainer = card.querySelector('.folderCardCategories');
            renderFolderCardCategories(catContainer, categories, checkedIdsStr);
        }
    }

    function loadFolderEntries(view, type, mappingsText, categories) {
        var listClass = type === 'movie' ? '.movieFoldersList' : '.seriesFoldersList';
        view.querySelector(listClass).innerHTML = '';

        if (!mappingsText) return;

        var lines = mappingsText.split('\n');
        for (var i = 0; i < lines.length; i++) {
            var line = lines[i].trim();
            if (!line) continue;
            var eqIdx = line.indexOf('=');
            if (eqIdx < 0) continue;
            var name = line.substring(0, eqIdx).trim();
            var ids = line.substring(eqIdx + 1).trim();
            addFolderEntry(view, type, name, ids, categories);
        }
    }

    function serializeFolderEntries(view, type) {
        var listClass = type === 'movie' ? '.movieFoldersList' : '.seriesFoldersList';
        var cards = view.querySelectorAll(listClass + ' .folderCard');
        var lines = [];
        for (var i = 0; i < cards.length; i++) {
            var name = cards[i].querySelector('.folderCardName').value.trim();
            if (!name) continue;

            // Check if checkboxes have been rendered (categories loaded)
            var allCheckboxes = cards[i].querySelectorAll('.folderCategoryCheckbox');
            var ids = [];
            if (allCheckboxes.length > 0) {
                // Categories loaded - read from checked checkboxes
                var checkedBoxes = cards[i].querySelectorAll('.folderCategoryCheckbox:checked');
                for (var j = 0; j < checkedBoxes.length; j++) {
                    ids.push(checkedBoxes[j].getAttribute('data-category-id'));
                }
            } else {
                // Categories not loaded yet - fall back to stored data attribute
                var storedIds = cards[i].getAttribute('data-checked-ids') || '';
                if (storedIds) {
                    var parts = storedIds.split(',');
                    for (var j = 0; j < parts.length; j++) {
                        var s = parts[j].trim();
                        if (s) ids.push(s);
                    }
                }
            }

            if (ids.length > 0) {
                lines.push(name + '=' + ids.join(','));
            }
        }
        return lines.join('\n');
    }

    function testXtreamConnection(instance) {
        var view = instance.view;
        var resultEl = view.querySelector('.connectionTestResult');
        resultEl.innerHTML = '<span style="opacity:0.5;">Testing connection...</span>';

        var url = view.querySelector('.txtBaseUrl').value.replace(/\/+$/, '');
        var user = view.querySelector('.txtUsername').value;
        var pass = view.querySelector('.txtPassword').value;

        if (!url || !user || !pass) {
            setPillResult(resultEl, false, 'Please enter server URL, username, and password.');
            return;
        }

        // Run the test server-side so it can reach hosts that the browser cannot
        // resolve (e.g. Docker container names on the Emby server's network).
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('XtreamTuner/TestConnection'),
            contentType: 'application/json',
            data: JSON.stringify({ BaseUrl: url, Username: user, Password: pass }),
            dataType: 'json'
        }).then(function (result) {
            setPillResult(resultEl, result.Success, result.Message);
            if (result.Success) {
                saveConfig(instance);
            }
        }).catch(function () {
            setPillResult(resultEl, false, 'Test request failed. Check server logs.');
        });
    }

    // ---- Folder browser ----

    function openBrowser(view) {
        var modal = view.querySelector('.strmBrowserModal');
        modal.style.display = 'flex';
        var startPath = (view.querySelector('.txtStrmLibraryPath').value || '').trim() || null;
        browserNavigate(view, startPath);
    }

    function closeBrowser(view) {
        view.querySelector('.strmBrowserModal').style.display = 'none';
    }

    function browserNavigate(view, path) {
        var modal = view.querySelector('.strmBrowserModal');
        var listEl = modal.querySelector('.browserList');
        listEl.innerHTML = '<div style="padding:1.2em 1.5em; opacity:0.5;">Loading...</div>';
        modal.querySelector('.txtBrowserCurrentPath').value = path || '';

        var url = ApiClient.getUrl('XtreamTuner/BrowsePath');
        if (path) url += '?path=' + encodeURIComponent(path);

        ApiClient.ajax({ type: 'GET', url: url, dataType: 'json' })
            .then(function (result) { browserRenderList(view, result); })
            .catch(function () {
                listEl.innerHTML = '<div style="padding:1.2em 1.5em; color:#cc0000;">Failed to load directory.</div>';
            });
    }

    function browserRenderList(view, result) {
        var modal = view.querySelector('.strmBrowserModal');
        var listEl = modal.querySelector('.browserList');
        listEl.innerHTML = '';

        modal.querySelector('.txtBrowserCurrentPath').value = result.CurrentPath || '';

        var isRoot = !result.CurrentPath;

        if (!isRoot) {
            var upRow = createBrowserRow('', '../  Parent directory', function () {
                browserNavigate(view, result.ParentPath || null);
            });
            listEl.appendChild(upRow);
        }

        if (result.Directories && result.Directories.length > 0) {
            result.Directories.forEach(function (dir) {
                var parts = dir.replace(/\\/g, '/').split('/').filter(function (p) { return p.length > 0; });
                var name = parts.length > 0 ? parts[parts.length - 1] : dir;
                var row = createBrowserRow('\u2192', name, function () {
                    browserNavigate(view, dir);
                });
                listEl.appendChild(row);
            });
        } else if (isRoot) {
            listEl.innerHTML = '<div style="padding:1.2em 1.5em; opacity:0.5;">No accessible folders found.</div>';
        } else {
            var emptyEl = document.createElement('div');
            emptyEl.style.cssText = 'padding:1.2em 1.5em; opacity:0.5;';
            emptyEl.textContent = 'No subdirectories.';
            listEl.appendChild(emptyEl);
        }
    }

    function createBrowserRow(icon, label, onClick) {
        var row = document.createElement('div');
        row.style.cssText = 'display:flex; align-items:center; gap:0.8em; padding:0.6em 1.5em; cursor:pointer; border-bottom:1px solid rgba(128,128,128,0.07); user-select:none;';
        var iconEl = document.createElement('span');
        iconEl.textContent = icon;
        iconEl.style.cssText = 'flex-shrink:0; width:1.2em; text-align:center; opacity:0.6;';
        var labelEl = document.createElement('span');
        labelEl.textContent = label;
        labelEl.style.cssText = 'font-size:0.9em; font-family:monospace;';
        row.appendChild(iconEl);
        row.appendChild(labelEl);
        row.addEventListener('mouseenter', function () { this.style.background = 'rgba(128,128,128,0.1)'; });
        row.addEventListener('mouseleave', function () { this.style.background = ''; });
        row.addEventListener('click', onClick);
        return row;
    }

    function validateStrmPath(view) {
        var path = (view.querySelector('.txtStrmLibraryPath').value || '').trim();
        var resultEl = view.querySelector('.strmPathValidationResult');
        if (!path) {
            resultEl.innerHTML = '';
            return;
        }
        resultEl.innerHTML = '<span style="opacity:0.5;">Checking path...</span>';
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('XtreamTuner/ValidateStrmPath'),
            contentType: 'application/json',
            data: JSON.stringify({ Path: path }),
            dataType: 'json'
        }).then(function (result) {
            setPillResult(resultEl, result.Success, result.Message);
        }).catch(function () {
            setPillResult(resultEl, false, 'Validation request failed.');
        });
    }

    function testDispatcharrConnection(instance) {
        var view = instance.view;
        var resultEl = view.querySelector('.dispatcharrTestResult');
        resultEl.innerHTML = '<span style="opacity:0.5;">Testing connection...</span>';

        var url = view.querySelector('.txtDispatcharrUrl').value.replace(/\/+$/, '');
        var user = view.querySelector('.txtDispatcharrUser').value;
        var pass = view.querySelector('.txtDispatcharrPass').value;

        if (!url) {
            setPillResult(resultEl, false, 'Please enter Dispatcharr URL.');
            return;
        }

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('XtreamTuner/TestDispatcharr'),
            contentType: 'application/json',
            data: JSON.stringify({ Url: url, Username: user, Password: pass }),
            dataType: 'json'
        }).then(function (result) {
            setPillResult(resultEl, result.Success, result.Message);
            if (result.Success) {
                saveConfig(instance);
            }
        }).catch(function () {
            setPillResult(resultEl, false, 'Test request failed. Check server logs.');
        });
    }

    // ---- Dispatcharr Channel Profiles ----

    function loadCachedDispatcharrProfiles(instance, config) {
        if (!config.CachedDispatcharrProfiles) return;
        try {
            var profiles = JSON.parse(config.CachedDispatcharrProfiles);
            if (profiles && profiles.length > 0) {
                instance.loadedDispatcharrProfiles = profiles;
                renderProfileList(instance.view, profiles, instance.selectedDispatcharrProfileIds);
            }
        } catch (e) {}
    }

    function loadDispatcharrProfiles(instance) {
        var view = instance.view;
        var listEl = view.querySelector('.dispatcharrProfilesList');
        var loadingEl = view.querySelector('.dispatcharrProfilesLoading');

        loadingEl.style.display = 'block';
        listEl.innerHTML = '';
        listEl.appendChild(loadingEl);

        var apiUrl = ApiClient.getUrl('XtreamTuner/DispatcharrProfiles');
        ApiClient.getJSON(apiUrl).then(function (profiles) {
            loadingEl.style.display = 'none';
            instance.loadedDispatcharrProfiles = profiles;

            if (!profiles || profiles.length === 0) {
                listEl.innerHTML = '<div style="opacity:0.5;">No profiles found. Check Dispatcharr connection settings.</div>';
                return;
            }

            renderProfileList(view, profiles, instance.selectedDispatcharrProfileIds);
            view.querySelector('.btnSelectAllProfiles').disabled = false;
            view.querySelector('.btnDeselectAllProfiles').disabled = false;
            updateProfileCountBadge(view);
        }).catch(function () {
            loadingEl.style.display = 'none';
            listEl.innerHTML = '<div style="color:#cc4444;">Failed to load profiles. Save Dispatcharr settings first.</div>';
        });
    }

    function renderProfileList(view, profiles, selectedIds) {
        var listEl = view.querySelector('.dispatcharrProfilesList');
        var html = '';
        for (var i = 0; i < profiles.length; i++) {
            var p = profiles[i];
            var checked = (selectedIds || []).indexOf(p.Id) >= 0 ? ' checked' : '';
            html += '<div class="checkboxContainer" style="margin:0.15em 0;">';
            html += '<label style="display:flex; align-items:center; cursor:pointer;">';
            html += '<input type="checkbox" class="dispatcharrProfileCheckbox" data-profile-id="' + p.Id + '"' + checked + ' style="margin-right:0.5em;" onchange="(function(el){var v=el.closest(\'.dispatcharrProfilesList\');v&&v.dispatchEvent(new Event(\'change\',{bubbles:true}));})(this)" />';
            html += '<span>' + escapeHtml(p.Name || ('Profile ' + p.Id)) + '</span>';
            html += '</label>';
            html += '</div>';
        }
        listEl.innerHTML = html;

        view.querySelector('.btnSelectAllProfiles').disabled = profiles.length === 0;
        view.querySelector('.btnDeselectAllProfiles').disabled = profiles.length === 0;

        // Update count badge whenever a checkbox changes
        listEl.addEventListener('change', function () {
            updateProfileCountBadge(view);
        });

        updateProfileCountBadge(view);
    }

    function toggleAllProfiles(view, checked) {
        var checkboxes = view.querySelectorAll('.dispatcharrProfileCheckbox');
        for (var i = 0; i < checkboxes.length; i++) {
            checkboxes[i].checked = checked;
        }
        updateProfileCountBadge(view);
    }

    function updateProfileCountBadge(view) {
        var checkboxes = view.querySelectorAll('.dispatcharrProfileCheckbox');
        var selected = 0;
        for (var i = 0; i < checkboxes.length; i++) {
            if (checkboxes[i].checked) selected++;
        }
        var badge = view.querySelector('.dispatcharrProfileCountBadge');
        if (badge) {
            badge.style.display = checkboxes.length > 0 ? '' : 'none';
            badge.querySelector('.profileCountSelected').textContent = selected;
            badge.querySelector('.profileCountTotal').textContent = checkboxes.length;
        }
    }

    function getSelectedDispatcharrProfileIds(instance) {
        var view = instance.view;
        var checkboxes = view.querySelectorAll('.dispatcharrProfileCheckbox');
        var ids = [];
        for (var i = 0; i < checkboxes.length; i++) {
            if (checkboxes[i].checked) {
                ids.push(parseInt(checkboxes[i].getAttribute('data-profile-id'), 10));
            }
        }
        // If no checkboxes rendered (page reloaded without refresh), preserve last known selection
        if (checkboxes.length === 0) {
            return instance.selectedDispatcharrProfileIds;
        }
        return ids;
    }

    // ---- Cached category loading (instant from config) ----

    function loadCachedCategories(instance, config) {
        var view = instance.view;

        // VOD categories
        var vodLoaded = false;
        if (config.CachedVodCategories) {
            try {
                var vodCats = JSON.parse(config.CachedVodCategories);
                if (vodCats && vodCats.length > 0) {
                    vodLoaded = true;
                    instance.loadedVodCategories = vodCats;
                    resetContentItemState(instance, 'vod');
                    renderCategoryList(view, '.vodCategoriesList', vodCats, 'vodCategoryCheckbox', instance.selectedVodCategoryIds, 'vod');
                    view.querySelector('.btnSelectAllVodCategories').disabled = false;
                    view.querySelector('.btnDeselectAllVodCategories').disabled = false;
                    var statusEl = view.querySelector('.vodCategoriesStatus');
                    if (statusEl) statusEl.textContent = '';
                    updateCategoryCountBadge(view, 'vod');

                    populateFolderCheckboxes(view, 'movie', vodCats);
                }
            } catch (e) { /* ignore parse errors */ }
        }
        if (!vodLoaded) {
            clearFolderCardCategories(view, 'movie');
            var vodListEl = view.querySelector('.vodCategoriesList');
            if (vodListEl && !vodListEl.innerHTML.trim()) {
                vodListEl.innerHTML = '<div style="opacity:0.5;">Click "Refresh Categories" to load.</div>';
            }
        }

        // Series categories
        var seriesLoaded = false;
        if (config.CachedSeriesCategories) {
            try {
                var seriesCats = JSON.parse(config.CachedSeriesCategories);
                if (seriesCats && seriesCats.length > 0) {
                    seriesLoaded = true;
                    instance.loadedSeriesCategories = seriesCats;
                    resetContentItemState(instance, 'series');
                    renderCategoryList(view, '.seriesCategoriesList', seriesCats, 'seriesCategoryCheckbox', instance.selectedSeriesCategoryIds, 'series');
                    view.querySelector('.btnSelectAllSeriesCategories').disabled = false;
                    view.querySelector('.btnDeselectAllSeriesCategories').disabled = false;
                    var statusEl = view.querySelector('.seriesCategoriesStatus');
                    if (statusEl) statusEl.textContent = '';
                    updateCategoryCountBadge(view, 'series');

                    populateFolderCheckboxes(view, 'series', seriesCats);
                }
            } catch (e) { /* ignore parse errors */ }
        }
        if (!seriesLoaded) {
            clearFolderCardCategories(view, 'series');
            var seriesListEl = view.querySelector('.seriesCategoriesList');
            if (seriesListEl && !seriesListEl.innerHTML.trim()) {
                seriesListEl.innerHTML = '<div style="opacity:0.5;">Click "Refresh Categories" to load.</div>';
            }
        }

        // Live TV categories
        if (config.CachedLiveCategories) {
            try {
                var liveCats = JSON.parse(config.CachedLiveCategories);
                if (liveCats && liveCats.length > 0) {
                    instance.loadedCategories = liveCats;
                    renderCategoryList(view, '.categoriesList', liveCats, 'categoryCheckbox', instance.selectedCategoryIds);
                    view.querySelector('.btnSelectAllCategories').disabled = false;
                    view.querySelector('.btnDeselectAllCategories').disabled = false;
                    updateCategoryCountBadge(view, 'live');

                }
            } catch (e) { /* ignore parse errors */ }
        }
    }

    // contentType: 'vod' | 'series' to draw a per-title expander, or null/undefined for
    // Live TV (per-channel selection is not implemented for Live TV in this plugin).
    function renderCategoryList(view, listSelector, categories, checkboxClass, selectedIds, contentType) {
        var listEl = view.querySelector(listSelector);
        if (!listEl) return;
        var html = '';
        for (var i = 0; i < categories.length; i++) {
            var cat = categories[i];
            var checked = selectedIds.indexOf(cat.CategoryId) >= 0 ? ' checked' : '';
            html += '<div class="checkboxContainer" style="margin:0.15em 0;">';
            html += '<div style="display:flex; align-items:center;">';
            if (contentType) {
                html += '<button type="button" class="contentItemToggle" data-content-type="' + contentType + '"';
                html += ' data-cat-id="' + cat.CategoryId + '" title="Show titles in this category"';
                html += ' style="background:none; border:none; color:inherit; cursor:pointer; opacity:0.6; padding:0 0.4em 0 0; font-size:0.95em;">&#9656;</button>';
            }
            html += '<label style="display:flex; align-items:center; cursor:pointer; flex:1;">';
            html += '<input type="checkbox" class="' + checkboxClass + '" data-category-id="' + cat.CategoryId + '"' + checked + ' style="margin-right:0.5em;" />';
            html += '<span>' + escapeHtml(cat.CategoryName) + '</span>';
            html += '</label>';
            html += '</div>';
            if (contentType) {
                html += '<div class="contentItemList" data-content-type="' + contentType + '" data-cat-id="' + cat.CategoryId + '"';
                html += ' style="display:none; margin:0.2em 0 0.5em 1.6em; padding-left:0.6em; border-left:2px solid rgba(128,128,128,0.25);"></div>';
            }
            html += '</div>';
        }
        listEl.innerHTML = html;
    }

    // ---- Per-title selection (issue #57) ----

    // Redrawing the category list replaces every panel with a fresh collapsed one, so the
    // expansion map must be dropped alongside it — otherwise the first click on a toggle
    // reads as "collapse" against an already-hidden panel and appears to do nothing.
    // The item cache goes too: a category refresh is an explicit ask for current data.
    function resetContentItemState(instance, contentType) {
        instance.expandedContentCategories[contentType] = {};
        instance.contentItemsByCategory[contentType] = {};
    }

    function toggleContentItemPanel(instance, contentType, categoryId, btn) {
        var view = instance.view;
        var panel = view.querySelector('.contentItemList[data-content-type="' + contentType + '"][data-cat-id="' + categoryId + '"]');
        if (!panel) return;

        var expanded = !!instance.expandedContentCategories[contentType][categoryId];
        if (expanded) {
            instance.expandedContentCategories[contentType][categoryId] = false;
            panel.style.display = 'none';
            if (btn) btn.innerHTML = '&#9656;';
            return;
        }

        instance.expandedContentCategories[contentType][categoryId] = true;
        panel.style.display = '';
        if (btn) btn.innerHTML = '&#9662;';

        if (instance.contentItemsByCategory[contentType][categoryId]) {
            renderContentItems(instance, contentType, categoryId);
        } else {
            fetchContentItems(instance, contentType, categoryId);
        }
    }

    function fetchContentItems(instance, contentType, categoryId) {
        var view = instance.view;
        var panel = view.querySelector('.contentItemList[data-content-type="' + contentType + '"][data-cat-id="' + categoryId + '"]');
        if (!panel) return;

        var label = contentType === 'vod' ? 'movies' : 'series';
        panel.innerHTML = '<div style="opacity:0.5; padding:0.25em 0;">Loading ' + label + '...</div>';

        // Query string appended by hand: every other call in this file uses the
        // single-argument form of getUrl, so don't introduce a second convention here.
        var endpoint = contentType === 'vod' ? 'XtreamTuner/Items/Vod' : 'XtreamTuner/Items/Series';
        var apiUrl = ApiClient.getUrl(endpoint) + '?CategoryId=' + encodeURIComponent(categoryId);

        ApiClient.getJSON(apiUrl).then(function (items) {
            instance.contentItemsByCategory[contentType][categoryId] = items || [];
            renderContentItems(instance, contentType, categoryId);
        }).catch(function () {
            panel.innerHTML = '<div style="color:#cc0000; padding:0.25em 0;">Failed to load ' + label +
                '. Save your connection settings first, then try again.</div>';
        });
    }

    function renderContentItems(instance, contentType, categoryId) {
        var view = instance.view;
        var panel = view.querySelector('.contentItemList[data-content-type="' + contentType + '"][data-cat-id="' + categoryId + '"]');
        if (!panel) return;

        var items = instance.contentItemsByCategory[contentType][categoryId] || [];
        var label = contentType === 'vod' ? 'movies' : 'series';
        if (items.length === 0) {
            panel.innerHTML = '<div style="opacity:0.5; padding:0.25em 0;">No ' + label + ' in this category.</div>';
            return;
        }

        var excluded = {};
        var list = contentType === 'vod' ? instance.excludedVodStreamIds : instance.excludedSeriesIds;
        for (var e = 0; e < list.length; e++) {
            excluded[list[e]] = true;
        }

        // Plain styled buttons, NOT is="emby-button": Emby's custom-element upgrade does not
        // run on markup injected via innerHTML, so an emby-button here renders unstyled.
        var btnStyle = 'background:none; border:1px solid rgba(128,128,128,0.35); color:inherit; ' +
            'cursor:pointer; padding:0.15em 0.6em; border-radius:3px; font-size:0.9em;';

        var html = '<div style="margin:0.25em 0 0.4em;">';
        html += '<button type="button" class="contentItemSelectAll"';
        html += ' data-content-type="' + contentType + '" data-cat-id="' + categoryId + '"';
        html += ' style="' + btnStyle + ' margin-right:0.4em;">Select All</button>';
        html += '<button type="button" class="contentItemDeselectAll"';
        html += ' data-content-type="' + contentType + '" data-cat-id="' + categoryId + '"';
        html += ' style="' + btnStyle + '">Deselect All</button>';
        html += '<span style="opacity:0.5; margin-left:0.6em;">' + items.length + ' ' + label + '</span>';
        html += '</div>';

        for (var i = 0; i < items.length; i++) {
            var item = items[i];
            var checked = excluded[item.Id] ? '' : ' checked';
            html += '<div style="margin:0.1em 0;">';
            html += '<label style="display:flex; align-items:center; cursor:pointer;">';
            html += '<input type="checkbox" class="contentItemCheckbox" data-content-type="' + contentType + '"';
            html += ' data-item-id="' + item.Id + '"' + checked + ' style="margin-right:0.5em;" />';
            html += '<span>' + escapeHtml(item.Name || '(unnamed)') + '</span>';
            html += '</label>';
            html += '</div>';
        }

        panel.innerHTML = html;
    }

    function setContentExclusion(instance, contentType, itemId, shouldBeExcluded) {
        var list = contentType === 'vod' ? instance.excludedVodStreamIds : instance.excludedSeriesIds;
        var idx = list.indexOf(itemId);
        if (shouldBeExcluded && idx === -1) {
            list.push(itemId);
        } else if (!shouldBeExcluded && idx !== -1) {
            list.splice(idx, 1);
        }
    }

    function toggleAllContentItems(instance, contentType, categoryId, checked) {
        var view = instance.view;
        var panel = view.querySelector('.contentItemList[data-content-type="' + contentType + '"][data-cat-id="' + categoryId + '"]');
        if (!panel) return;
        var boxes = panel.querySelectorAll('.contentItemCheckbox');
        for (var i = 0; i < boxes.length; i++) {
            boxes[i].checked = checked;
            setContentExclusion(instance, contentType, parseInt(boxes[i].getAttribute('data-item-id'), 10), !checked);
        }
    }

    // ---- De-duplicated title view (Movies + Series, type-parameterized) ----
    // One row per unique StreamId/SeriesId across the selected categories. Shares the
    // same exclusion blocklist as the per-category tree (excluded*StreamIds), so ticking
    // a title here is identical to unticking it in the tree. Rendering is capped so very
    // large provider lists stay responsive; search + category filter narrow the set.
    var DEDUPED_RENDER_CAP = 300;

    // The reviewed-checkpoint set is persisted as a JSON id array (it grows toward the
    // full library, so a JSON string beats a huge int[] round-tripping through config),
    // but the client keeps it as a Set (id->true) for O(1) membership tests.
    function parseReviewedSet(json) {
        var set = {};
        if (json) {
            try {
                var arr = JSON.parse(json);
                if (Array.isArray(arr)) {
                    for (var i = 0; i < arr.length; i++) {
                        var n = parseInt(arr[i], 10);
                        if (!isNaN(n)) { set[n] = true; }
                    }
                }
            } catch (e) {}
        }
        return set;
    }

    function serializeReviewedSet(set) {
        if (!set) return '[]';
        var ids = Object.keys(set).map(function (k) { return parseInt(k, 10); });
        return JSON.stringify(ids);
    }

    // A title is "reviewed" when every one of its provider ids is either in the reviewed
    // set or already excluded — excluding a title implicitly reviews it (the derived
    // union), which is why existing exclusions need no migration. Mirrors the "all ids
    // excluded" rule used for the keep/exclude checkbox.
    function isTitleReviewed(instance, cfg, ids) {
        if (!ids || ids.length === 0) return false;
        var reviewed = instance[cfg.reviewedKey] || {};
        var excluded = instance[cfg.excludeKey] || [];
        for (var i = 0; i < ids.length; i++) {
            if (reviewed[ids[i]]) continue;
            if (excluded.indexOf(ids[i]) !== -1) continue;
            return false;
        }
        return true;
    }

    function dedupedConfig(type) {
        if (type === 'series') {
            return {
                prefix: 'series',
                loadClass: '.btnLoadSeriesDeduped',
                endpoint: 'XtreamTuner/Items/SeriesDeduped',
                dataKey: 'dedupedSeries',
                excludeKey: 'excludedSeriesIds',
                reviewedKey: 'reviewedSeriesIds',
                catsKey: 'loadedSeriesCategories'
            };
        }
        return {
            prefix: 'vod',
            loadClass: '.btnLoadVodDeduped',
            endpoint: 'XtreamTuner/Items/VodDeduped',
            dataKey: 'dedupedVod',
            excludeKey: 'excludedVodStreamIds',
            reviewedKey: 'reviewedVodStreamIds',
            catsKey: 'loadedVodCategories'
        };
    }

    // Attaches all listeners for one de-duplicated view (vod or series).
    function wireDedupedView(view, self, type) {
        var cfg = dedupedConfig(type);
        var P = '.' + cfg.prefix + 'Deduped';
        view.querySelector(cfg.loadClass).addEventListener('click', function () {
            loadDeduped(self, type);
        });
        view.querySelector(P + 'Search').addEventListener('input', function () {
            self[cfg.prefix + 'DedupedShowAll'] = false;
            renderDedupedList(self, type);
        });
        view.querySelector(P + 'CatFilterList').addEventListener('change', function (e) {
            if (e.target.classList.contains(cfg.prefix + 'DedupedCatCheckbox')) {
                self[cfg.prefix + 'DedupedShowAll'] = false;
                renderDedupedList(self, type);
            }
        });
        view.querySelector(P + 'CatAll').addEventListener('click', function () {
            setDedupedCatFilterAll(self, type, true);
        });
        view.querySelector(P + 'CatNone').addEventListener('click', function () {
            setDedupedCatFilterAll(self, type, false);
        });
        view.querySelector(P + 'Count').addEventListener('click', function (e) {
            if (e.target.classList.contains(cfg.prefix + 'DedupedShowAll')) {
                self[cfg.prefix + 'DedupedShowAll'] = true;
                renderDedupedList(self, type);
            }
        });
        view.querySelector(P + 'List').addEventListener('change', function (e) {
            if (e.target.classList.contains('dedupedItemCheckbox')) {
                updateDedupedExclusion(self, type, e.target);
            }
        });
        // Per-title reviewed toggle (the right-side control). Delegated so it works for rows
        // rendered later into the list.
        view.querySelector(P + 'List').addEventListener('click', function (e) {
            var toggle = e.target.closest ? e.target.closest('.dedupedReviewToggle') : null;
            if (toggle) {
                toggleTitleReviewed(self, type, parseItemIds(toggle.getAttribute('data-item-ids')),
                    toggle.closest('.exclusionItemRow'));
            }
        });
        view.querySelector(P + 'SelectAll').addEventListener('click', function () {
            bulkDedupedExclusion(self, type, false);
        });
        view.querySelector(P + 'DeselectAll').addEventListener('click', function () {
            bulkDedupedExclusion(self, type, true);
        });
        view.querySelector(P + 'MarkReviewed').addEventListener('click', function () {
            bulkMarkReviewed(self, type);
        });
        view.querySelector(P + 'MarkUnreviewed').addEventListener('click', function () {
            bulkMarkUnreviewed(self, type);
        });
        view.querySelector(P + 'ShowFilter').addEventListener('click', function (e) {
            var btn = e.target.closest ? e.target.closest('.' + cfg.prefix + 'DedupedShowBtn') : null;
            if (btn) setDedupedFilter(self, type, 'Show', btn.getAttribute('data-filter'));
        });
        view.querySelector(P + 'ReviewedFilter').addEventListener('click', function (e) {
            var btn = e.target.closest ? e.target.closest('.' + cfg.prefix + 'DedupedReviewedBtn') : null;
            if (btn) setDedupedFilter(self, type, 'Reviewed', btn.getAttribute('data-filter'));
        });
    }

    // Series get a distinct provider id per category, so excluding a title in one category
    // leaves its copies in other categories un-excluded — and a category added LATER brings
    // a fresh un-excluded copy, quietly re-enabling a title you'd already excluded. On load,
    // extend any PARTIALLY-excluded title (some ids excluded, not all) to cover all its ids,
    // making series exclusion effectively title-level. Idempotent; a no-op for single-id
    // titles (all movies, which already share one id across categories). Returns the number
    // of titles healed so the caller can prompt a save. (Movies never hit the length<2 gate.)
    function healPartialExclusions(instance, type) {
        var cfg = dedupedConfig(type);
        if (!instance[cfg.excludeKey]) instance[cfg.excludeKey] = [];
        var list = instance[cfg.excludeKey];
        var excluded = {};
        list.forEach(function (id) { excluded[id] = true; });

        var healedTitles = 0;
        var data = instance[cfg.dataKey] || [];
        for (var i = 0; i < data.length; i++) {
            var ids = data[i].Ids || [];
            if (ids.length < 2) continue;
            var anyExcluded = false, allExcluded = true;
            for (var k = 0; k < ids.length; k++) {
                if (excluded[ids[k]]) anyExcluded = true; else allExcluded = false;
            }
            if (anyExcluded && !allExcluded) {
                for (var j = 0; j < ids.length; j++) {
                    if (!excluded[ids[j]]) { list.push(ids[j]); excluded[ids[j]] = true; }
                }
                healedTitles++;
            }
        }
        return healedTitles;
    }

    function loadDeduped(instance, type) {
        var cfg = dedupedConfig(type);
        var view = instance.view;
        var statusEl = view.querySelector('.' + cfg.prefix + 'DedupedStatus');
        var controlsEl = view.querySelector('.' + cfg.prefix + 'DedupedControls');

        statusEl.style.color = '';
        statusEl.textContent = 'Loading…';

        ApiClient.getJSON(ApiClient.getUrl(cfg.endpoint)).then(function (items) {
            instance[cfg.dataKey] = items || [];
            statusEl.style.color = '#52B54B';
            statusEl.textContent = 'Loaded ' + instance[cfg.dataKey].length + ' unique titles';

            // Extend any partial exclusions to whole titles (covers duplicate copies from
            // categories added since the last review), then tell the user to save.
            var healed = healPartialExclusions(instance, type);
            var healEl = view.querySelector('.' + cfg.prefix + 'DedupedHealNotice');
            if (healEl) {
                if (healed > 0) {
                    healEl.textContent = 'Re-applied your exclusions to ' + healed +
                        (healed === 1 ? ' title' : ' titles') + ' with new duplicate copies — Save to keep.';
                    healEl.style.display = '';
                } else {
                    healEl.style.display = 'none';
                }
            }

            controlsEl.style.display = '';
            styleDedupedFilterButtons(view, cfg.prefix, 'Show', instance[cfg.prefix + 'DedupedShowFilter'] || 'all');
            styleDedupedFilterButtons(view, cfg.prefix, 'Reviewed', instance[cfg.prefix + 'DedupedReviewedFilter'] || 'all');
            populateDedupedCategoryFilter(instance, type);
            renderDedupedList(instance, type);
        }).catch(function () {
            statusEl.style.color = '#cc0000';
            statusEl.textContent = 'Failed to load. Save your connection and select categories first.';
        });
    }

    function populateDedupedCategoryFilter(instance, type) {
        var cfg = dedupedConfig(type);
        var view = instance.view;
        var listEl = view.querySelector('.' + cfg.prefix + 'DedupedCatFilterList');
        var nameById = {};
        (instance[cfg.catsKey] || []).forEach(function (c) { nameById[c.CategoryId] = c.CategoryName; });

        var present = {};
        (instance[cfg.dataKey] || []).forEach(function (t) {
            (t.Categories || []).forEach(function (cid) { present[cid] = true; });
        });

        var ids = Object.keys(present).map(function (k) { return parseInt(k, 10); });
        ids.sort(function (a, b) {
            return (nameById[a] || ('Category ' + a)).localeCompare(nameById[b] || ('Category ' + b));
        });

        // Default every category ticked: with none=none semantics an empty filter shows
        // nothing, so a fresh load must start all-ticked to show the full list.
        var html = '';
        for (var i = 0; i < ids.length; i++) {
            html += '<label style="display:flex; align-items:flex-start; cursor:pointer; line-height:1.3;">';
            html += '<input type="checkbox" class="' + cfg.prefix + 'DedupedCatCheckbox" data-category-id="' + ids[i] + '" checked style="margin-right:0.35em; margin-top:0.15em; flex:0 0 auto;" />';
            html += '<span>' + escapeHtml(nameById[ids[i]] || ('Category ' + ids[i])) + '</span>';
            html += '</label>';
        }
        listEl.innerHTML = html || '<div style="opacity:0.5;">No categories.</div>';
    }

    // Reads the ticked category-filter checkboxes. none=none: an empty result shows nothing
    // (the filter defaults to all-ticked on populate, so the initial load shows everything).
    function getDedupedCatFilter(instance, type) {
        var cfg = dedupedConfig(type);
        var cbs = instance.view.querySelectorAll('.' + cfg.prefix + 'DedupedCatCheckbox:checked');
        var ids = [];
        for (var i = 0; i < cbs.length; i++) {
            ids.push(parseInt(cbs[i].getAttribute('data-category-id'), 10));
        }
        return ids;
    }

    function setDedupedCatFilterAll(instance, type, checked) {
        var cfg = dedupedConfig(type);
        var cbs = instance.view.querySelectorAll('.' + cfg.prefix + 'DedupedCatCheckbox');
        for (var i = 0; i < cbs.length; i++) { cbs[i].checked = checked; }
        instance[cfg.prefix + 'DedupedShowAll'] = false;
        renderDedupedList(instance, type);
    }

    function renderDedupedList(instance, type) {
        var cfg = dedupedConfig(type);
        var view = instance.view;
        var listEl = view.querySelector('.' + cfg.prefix + 'DedupedList');
        var countEl = view.querySelector('.' + cfg.prefix + 'DedupedCount');

        var search = (view.querySelector('.' + cfg.prefix + 'DedupedSearch').value || '').toLowerCase();
        var catFilter = getDedupedCatFilter(instance, type);

        var excluded = {};
        (instance[cfg.excludeKey] || []).forEach(function (id) { excluded[id] = true; });

        var showFilter = instance[cfg.prefix + 'DedupedShowFilter'] || 'all';
        var reviewedFilter = instance[cfg.prefix + 'DedupedReviewedFilter'] || 'all';

        var matches = [];
        var reviewedCount = 0;
        var excludedCount = 0;
        var data = instance[cfg.dataKey] || [];
        for (var i = 0; i < data.length; i++) {
            var t = data[i];
            if (search && (t.Name || '').toLowerCase().indexOf(search) < 0) continue;
            // Category filter is a union AND none=none: a title shows only if it's in ANY
            // ticked category, so an empty filter (all unticked) shows nothing. The filter
            // defaults to all-ticked on populate, so the initial load still shows everything.
            var cats = t.Categories || [];
            var inAny = false;
            for (var m = 0; m < catFilter.length; m++) {
                if (cats.indexOf(catFilter[m]) >= 0) { inAny = true; break; }
            }
            if (!inAny) continue;
            // Cache reviewed + excluded state on the title so rendering, the count line, and
            // the hide-worklist filters all agree and none recomputes it per row. A title is
            // excluded when every one of its ids is in the blocklist (mirrors the checkbox).
            t._reviewed = isTitleReviewed(instance, cfg, t.Ids);
            var tIds = t.Ids || [];
            var allExcluded = tIds.length > 0;
            for (var e2 = 0; e2 < tIds.length; e2++) {
                if (!excluded[tIds[e2]]) { allExcluded = false; break; }
            }
            t._excluded = allExcluded;
            if (t._reviewed) reviewedCount++;
            if (t._excluded) excludedCount++;
            // Tri-state view filters (default All, counted above so totals ignore them).
            // Show: Included = only titles that will sync; Excluded = only blocklisted
            // (audit / bulk un-exclude). Reviewed: Unreviewed = the "new since I last looked"
            // worklist; Reviewed = only already-checked.
            if (showFilter === 'included' && t._excluded) continue;
            if (showFilter === 'excluded' && !t._excluded) continue;
            if (reviewedFilter === 'reviewed' && !t._reviewed) continue;
            if (reviewedFilter === 'unreviewed' && t._reviewed) continue;
            matches.push(t);
        }
        // Remember the full filtered set so bulk actions apply to all matches, not just
        // the capped rows that are rendered.
        instance[cfg.prefix + 'DedupedMatches'] = matches;

        var showAll = instance[cfg.prefix + 'DedupedShowAll'];
        var renderCap = showAll ? matches.length : DEDUPED_RENDER_CAP;
        var shown = matches.slice(0, renderCap);
        var html = '';
        for (var j = 0; j < shown.length; j++) {
            var it = shown[j];
            var itIds = it.Ids || [];
            // A title is shown ticked (kept) unless every one of its ids is excluded
            // (cached as _excluded during filtering above).
            var isChecked = it._excluded ? '' : ' checked';
            // Dimmed name = excluded (won't sync); the right-side toggle carries the reviewed
            // bookmark. Excluded rows hide the toggle — the dim + unticked box already say
            // "dealt with", and "reviewed" is derived from exclusion there (not un-reviewable).
            var revClass = it._reviewed ? 'dedupedReviewToggle is-reviewed' : 'dedupedReviewToggle is-unreviewed';
            var revText = it._reviewed ? '✓ reviewed' : 'mark reviewed';
            var revTitle = it._reviewed ? 'Reviewed — click to mark unreviewed' : 'Click to mark reviewed';
            html += '<div class="exclusionItemRow" data-item-ids="' + itIds.join(',') + '" style="margin:0.1em 0; display:flex; align-items:center; justify-content:space-between;">';
            html += '<label style="display:flex; align-items:center; cursor:pointer; flex:1 1 auto; min-width:0;">';
            html += '<input type="checkbox" class="dedupedItemCheckbox" data-item-ids="' + itIds.join(',') + '"' + isChecked + ' style="margin-right:0.5em; flex:0 0 auto;" />';
            html += '<span class="dedupedItemName" style="' + (it._excluded ? 'opacity:0.5;' : '') + '">' + escapeHtml(it.Name || ('#' + (itIds[0] || '?'))) + '</span>';
            html += '</label>';
            html += '<span class="' + revClass + '" data-item-ids="' + itIds.join(',') + '" title="' + revTitle + '"' + (it._excluded ? ' style="display:none;"' : '') + '>' + revText + '</span>';
            html += '</div>';
        }
        if (html) {
            listEl.innerHTML = html;
        } else if (catFilter.length === 0) {
            // none=none: an empty category filter is why nothing shows — point the user at it
            // rather than implying there are no titles.
            listEl.innerHTML = '<div style="opacity:0.5;">No category selected in the filter above — tick a category (or click All) to show titles.</div>';
        } else {
            listEl.innerHTML = '<div style="opacity:0.5;">No matching titles.</div>';
        }

        // Count line: "M of N titles (R reviewed, X excluded)". When the match set exceeds
        // the cap, cap the render for responsiveness but offer a one-click "Show all".
        var notes = [];
        if (reviewedCount > 0) notes.push(reviewedCount + ' reviewed');
        if (excludedCount > 0) notes.push(excludedCount + ' excluded');
        var reviewedSuffix = notes.length ? ' (' + notes.join(', ') + ')' : '';
        if (matches.length > DEDUPED_RENDER_CAP && !showAll) {
            countEl.innerHTML = 'Showing ' + DEDUPED_RENDER_CAP + ' of ' + matches.length + reviewedSuffix +
                ' — <button type="button" class="' + cfg.prefix + 'DedupedShowAll" style="cursor:pointer;">Show all ' + matches.length + '</button> or refine your search';
        } else if (showAll && matches.length > DEDUPED_RENDER_CAP) {
            countEl.textContent = 'Showing all ' + matches.length + ' of ' + data.length + ' titles' + reviewedSuffix;
        } else {
            countEl.textContent = matches.length + ' of ' + data.length + ' titles' + reviewedSuffix;
        }
    }

    // Tri-state view filters for the de-dup list (session-only). kind = 'Show' | 'Reviewed'.
    function setDedupedFilter(instance, type, kind, value) {
        var cfg = dedupedConfig(type);
        instance[cfg.prefix + 'Deduped' + kind + 'Filter'] = value;
        instance[cfg.prefix + 'DedupedShowAll'] = false;
        styleDedupedFilterButtons(instance.view, cfg.prefix, kind, value);
        renderDedupedList(instance, type);
    }

    // Reflects the active option of one segmented filter control (reuses the mode-toggle look).
    function styleDedupedFilterButtons(view, prefix, kind, value) {
        var btns = view.querySelectorAll('.' + prefix + 'Deduped' + kind + 'Btn');
        for (var i = 0; i < btns.length; i++) {
            var active = btns[i].getAttribute('data-filter') === value;
            btns[i].style.background = active ? '#52B54B' : 'none';
            btns[i].style.color = active ? '#fff' : 'inherit';
        }
    }

    function parseItemIds(attr) {
        var ids = [];
        (attr || '').split(',').forEach(function (s) {
            var n = parseInt(s, 10);
            if (!isNaN(n)) { ids.push(n); }
        });
        return ids;
    }

    // A title maps to one or more provider ids (one for movies, several for a series
    // that spans categories). Excluding the title adds all its ids to the blocklist;
    // including removes them all.
    function updateDedupedExclusion(instance, type, cb) {
        var cfg = dedupedConfig(type);
        var ids = parseItemIds(cb.getAttribute('data-item-ids'));
        if (!instance[cfg.excludeKey]) instance[cfg.excludeKey] = [];
        if (!instance[cfg.reviewedKey]) instance[cfg.reviewedKey] = {};
        var list = instance[cfg.excludeKey];
        var reviewed = instance[cfg.reviewedKey];
        for (var i = 0; i < ids.length; i++) {
            var idx = list.indexOf(ids[i]);
            if (!cb.checked && idx === -1) {
                // Exclude: the derived union (excluded ⇒ reviewed) marks it reviewed,
                // no need to also touch the reviewed set here.
                list.push(ids[i]);
            } else if (cb.checked && idx !== -1) {
                // Re-include: drop from the blocklist but keep it reviewed — the user has
                // looked at this title and made a decision, so it should not resurface in
                // the unreviewed worklist.
                list.splice(idx, 1);
                reviewed[ids[i]] = true;
            }
        }
        // Restyle the row in place (excluded dimming / reviewed toggle) without a full
        // re-render, so stepping through a long list one checkbox at a time doesn't reset
        // scroll or yank rows out from under the cursor. Hide filters cull on next render.
        var row = cb.closest ? cb.closest('.exclusionItemRow') : null;
        if (row) restyleDedupedRow(instance, type, row, ids);
    }

    // Sets the right-side reviewed toggle's appearance for a row: hidden when excluded
    // (exclusion governs "dealt with" there), else "✓ reviewed" (click to un-review) or the
    // fainter "mark reviewed" (click to review).
    function applyReviewToggleState(el, excluded, reviewed) {
        if (!el) return;
        if (excluded) { el.style.display = 'none'; return; }
        el.style.display = '';
        el.className = reviewed ? 'dedupedReviewToggle is-reviewed' : 'dedupedReviewToggle is-unreviewed';
        el.textContent = reviewed ? '✓ reviewed' : 'mark reviewed';
        el.title = reviewed ? 'Reviewed — click to mark unreviewed' : 'Click to mark reviewed';
    }

    // Recomputes a row's excluded/reviewed styling from current instance state (name dim +
    // reviewed toggle) without a full re-render, so single-title edits keep scroll position.
    function restyleDedupedRow(instance, type, row, ids) {
        var cfg = dedupedConfig(type);
        var excludedSet = {};
        (instance[cfg.excludeKey] || []).forEach(function (id) { excludedSet[id] = true; });
        var allExcluded = ids.length > 0;
        for (var i = 0; i < ids.length; i++) {
            if (!excludedSet[ids[i]]) { allExcluded = false; break; }
        }
        var reviewed = isTitleReviewed(instance, cfg, ids);
        var nameEl = row.querySelector('.dedupedItemName');
        if (nameEl) nameEl.style.opacity = allExcluded ? '0.5' : '';
        applyReviewToggleState(row.querySelector('.dedupedReviewToggle'), allExcluded, reviewed);
    }

    // Toggles the reviewed bookmark for a single included title (the right-side control).
    // Excluded titles are reviewed-by-derivation and their toggle is hidden, so this only
    // ever flips reviewed-set membership for an included title's ids.
    function toggleTitleReviewed(instance, type, ids, row) {
        if (!ids || !ids.length) return;
        var cfg = dedupedConfig(type);
        if (!instance[cfg.reviewedKey]) instance[cfg.reviewedKey] = {};
        var reviewed = instance[cfg.reviewedKey];
        var isRev = isTitleReviewed(instance, cfg, ids);
        for (var i = 0; i < ids.length; i++) {
            if (isRev) delete reviewed[ids[i]];
            else reviewed[ids[i]] = true;
        }
        if (row) restyleDedupedRow(instance, type, row, ids);
    }

    // Applies to every title in the current filtered set (all matches, not just the
    // capped rows), then re-renders so the visible checkboxes reflect the change.
    function bulkDedupedExclusion(instance, type, exclude) {
        var cfg = dedupedConfig(type);
        var matches = instance[cfg.prefix + 'DedupedMatches'] || [];
        if (!instance[cfg.excludeKey]) instance[cfg.excludeKey] = [];
        if (!instance[cfg.reviewedKey]) instance[cfg.reviewedKey] = {};
        var list = instance[cfg.excludeKey];
        var reviewed = instance[cfg.reviewedKey];
        for (var i = 0; i < matches.length; i++) {
            var ids = matches[i].Ids || [];
            for (var k = 0; k < ids.length; k++) {
                var idx = list.indexOf(ids[k]);
                if (exclude && idx === -1) {
                    list.push(ids[k]);
                } else if (!exclude && idx !== -1) {
                    // Bulk re-include of a previously-excluded title keeps it reviewed,
                    // mirroring the single-checkbox path. Guarding on idx !== -1 means
                    // "Select all matching" on an untouched category is a genuine no-op and
                    // does NOT mass-mark reviewed.
                    list.splice(idx, 1);
                    reviewed[ids[k]] = true;
                }
            }
        }
        renderDedupedList(instance, type);
    }

    // "Mark all shown reviewed" — adds every id in the current filtered match set to the
    // reviewed set. With hide-reviewed on, the match set IS the unreviewed worklist, so
    // this clears it; with it off, it marks the whole filtered list reviewed. Does not
    // change exclusions — reviewing is orthogonal to keep/exclude.
    function bulkMarkReviewed(instance, type) {
        var cfg = dedupedConfig(type);
        var matches = instance[cfg.prefix + 'DedupedMatches'] || [];
        if (!instance[cfg.reviewedKey]) instance[cfg.reviewedKey] = {};
        var reviewed = instance[cfg.reviewedKey];
        for (var i = 0; i < matches.length; i++) {
            var ids = matches[i].Ids || [];
            for (var k = 0; k < ids.length; k++) { reviewed[ids[k]] = true; }
        }
        renderDedupedList(instance, type);
    }

    // "Mark all shown unreviewed" — the inverse: clears the reviewed set for every id in the
    // current filtered match set. Exclusions are untouched, so excluded titles stay reviewed
    // by derivation (re-include them to make them un-reviewable). Handy for undoing a bulk
    // mark or re-surfacing a batch in the unreviewed worklist.
    function bulkMarkUnreviewed(instance, type) {
        var cfg = dedupedConfig(type);
        var matches = instance[cfg.prefix + 'DedupedMatches'] || [];
        if (!instance[cfg.reviewedKey]) instance[cfg.reviewedKey] = {};
        var reviewed = instance[cfg.reviewedKey];
        for (var i = 0; i < matches.length; i++) {
            var ids = matches[i].Ids || [];
            for (var k = 0; k < ids.length; k++) { delete reviewed[ids[k]]; }
        }
        renderDedupedList(instance, type);
    }

    // ---- De-dup / Browse mode toggle ----
    // The single-folder VOD/Series UI shows either the category tree ("browse") or the
    // de-dup review list ("dedup"), never both. Both edit the same exclusion blocklist,
    // so switching is lossless. The choice is remembered per type in localStorage; the
    // fork defaults to de-dup.
    function setDedupMode(instance, type, mode) {
        var view = instance.view;
        var browseEl = view.querySelector('.' + type + 'BrowseSection');
        var dedupEl = view.querySelector('.' + type + 'DedupedSection');
        if (!browseEl || !dedupEl) return;
        browseEl.style.display = mode === 'browse' ? '' : 'none';
        dedupEl.style.display = mode === 'dedup' ? '' : 'none';
        var btns = view.querySelectorAll('.' + type + 'ModeBtn');
        for (var i = 0; i < btns.length; i++) {
            var active = btns[i].getAttribute('data-mode') === mode;
            btns[i].style.background = active ? '#52B54B' : 'none';
            btns[i].style.color = active ? '#fff' : 'inherit';
        }
        // Repaint the view we switch INTO so exclusions made in the other view show without
        // a page reload (both edit the same blocklist). The de-dup list's category SCOPE is
        // still server-side, so a category-selection change needs save+reload (the nudge).
        if (mode === 'dedup') {
            var cfg = dedupedConfig(type);
            if ((instance[cfg.dataKey] || []).length) renderDedupedList(instance, type);
        } else {
            var expanded = instance.expandedContentCategories[type] || {};
            for (var catId in expanded) {
                if (expanded[catId] && instance.contentItemsByCategory[type][catId]) {
                    renderContentItems(instance, type, catId);
                }
            }
        }
        try { window.localStorage.setItem('xtreamDedupMode_' + type, mode); } catch (e) {}
    }

    function setupDedupMode(view, self, type) {
        var toggle = view.querySelector('.' + type + 'ModeToggle');
        if (toggle) {
            toggle.addEventListener('click', function (e) {
                var btn = e.target.closest ? e.target.closest('.' + type + 'ModeBtn') : null;
                if (btn) setDedupMode(self, type, btn.getAttribute('data-mode'));
            });
        }
        var stored = null;
        try { stored = window.localStorage.getItem('xtreamDedupMode_' + type); } catch (e) {}
        // Default to browse for a fresh user (clearer cold-start — de-dup needs Load +
        // category selection); a remembered choice wins.
        setDedupMode(self, type, stored === 'dedup' ? 'dedup' : 'browse');
    }

    // Shows/hides the "category selection changed — save & reload" hint in the de-dup
    // section. The de-dup list is scoped server-side to the SAVED Selected*CategoryIds,
    // so a category change only reaches it after a save + re-load.
    function setDedupedCatNudge(instance, type, show) {
        var el = instance.view.querySelector('.' + type + 'DedupedCatNudge');
        if (el) el.style.display = show ? '' : 'none';
    }

    // ---- Live TV Categories ----

    function loadCategories(instance) {
        var view = instance.view;
        var listEl = view.querySelector('.categoriesList');
        var loadingEl = view.querySelector('.categoriesLoading');

        loadingEl.style.display = 'block';
        listEl.innerHTML = '';

        var apiUrl = ApiClient.getUrl('XtreamTuner/Categories/Live');

        ApiClient.getJSON(apiUrl).then(function (categories) {
            loadingEl.style.display = 'none';
            instance.loadedCategories = categories;

            if (!categories || categories.length === 0) {
                listEl.innerHTML = '<div style="opacity:0.5;">No categories found. Check your Xtream connection settings.</div>';
                return;
            }

            var html = '';
            for (var i = 0; i < categories.length; i++) {
                var cat = categories[i];
                var checked = instance.selectedCategoryIds.indexOf(cat.CategoryId) >= 0 ? ' checked' : '';
                html += '<div class="checkboxContainer" style="margin:0.15em 0;">';
                html += '<label style="display:flex; align-items:center; cursor:pointer;">';
                html += '<input type="checkbox" class="categoryCheckbox" data-category-id="' + cat.CategoryId + '"' + checked + ' style="margin-right:0.5em;" />';
                html += '<span>' + escapeHtml(cat.CategoryName) + '</span>';
                html += '</label>';
                html += '</div>';
            }
            listEl.innerHTML = html;

            view.querySelector('.btnSelectAllCategories').disabled = false;
            view.querySelector('.btnDeselectAllCategories').disabled = false;
            updateCategoryCountBadge(view, 'live');
        }).catch(function () {
            loadingEl.style.display = 'none';
            listEl.innerHTML = '<div style="color:#cc0000;">Failed to load categories. Save your connection settings first, then try again.</div>';
        });
    }

    function toggleAllCategories(view, checked) {
        var checkboxes = view.querySelectorAll('.categoryCheckbox');
        for (var i = 0; i < checkboxes.length; i++) {
            checkboxes[i].checked = checked;
        }
        updateCategoryCountBadge(view, 'live');
    }

    function getSelectedCategoryIds(instance) {
        var view = instance.view;
        var checkboxes = view.querySelectorAll('.categoryCheckbox');
        var ids = [];
        for (var i = 0; i < checkboxes.length; i++) {
            if (checkboxes[i].checked) {
                ids.push(parseInt(checkboxes[i].getAttribute('data-category-id'), 10));
            }
        }
        if (checkboxes.length === 0) {
            return instance.selectedCategoryIds;
        }
        return ids;
    }

    // ---- VOD Categories (single mode) ----

    function loadVodCategories(instance) {
        var view = instance.view;
        var listEl = view.querySelector('.vodCategoriesList');
        var loadingEl = view.querySelector('.vodCategoriesLoading');
        var statusEl = view.querySelector('.vodCategoriesStatus');

        loadingEl.style.display = 'block';
        listEl.innerHTML = '';

        var apiUrl = ApiClient.getUrl('XtreamTuner/Categories/Vod');

        ApiClient.getJSON(apiUrl).then(function (categories) {
            loadingEl.style.display = 'none';
            instance.loadedVodCategories = categories;

            if (!categories || categories.length === 0) {
                listEl.innerHTML = '<div style="opacity:0.5;">No VOD categories found. Your provider may not include VOD access on this account.</div>';
                return;
            }

            if (statusEl) statusEl.textContent = '';

            resetContentItemState(instance, 'vod');
            renderCategoryList(view, '.vodCategoriesList', categories, 'vodCategoryCheckbox', instance.selectedVodCategoryIds, 'vod');

            view.querySelector('.btnSelectAllVodCategories').disabled = false;
            view.querySelector('.btnDeselectAllVodCategories').disabled = false;
            updateCategoryCountBadge(view, 'vod');
        }).catch(function () {
            loadingEl.style.display = 'none';
            listEl.innerHTML = '<div style="color:#cc0000;">Failed to load VOD categories. Save your connection settings first, then try again.</div>';
        });
    }

    // Per-title exclusions deliberately survive this. They are keyed by stream ID and are
    // independent of category selection: an excluded title in a deselected category is simply
    // never fetched, and re-selecting the category re-applies the exclusion. Clearing them here
    // would be actively destructive — on Emby "no categories checked" means *sync everything*
    // (see FetchVodStreamsAsync), so Deselect All would hand back every title the user removed.
    function toggleAllVodCategories(instance, checked) {
        var view = instance.view;
        var checkboxes = view.querySelectorAll('.vodCategoryCheckbox');
        for (var i = 0; i < checkboxes.length; i++) {
            checkboxes[i].checked = checked;
        }
        updateCategoryCountBadge(view, 'vod');
    }

    // ---- VOD Categories (multi/folder mode) ----

    function loadVodCategoriesMulti(instance) {
        var view = instance.view;
        var statusEl = view.querySelector('.vodCategoriesMultiStatus');
        statusEl.textContent = 'Loading...';
        statusEl.style.opacity = '0.5';

        var apiUrl = ApiClient.getUrl('XtreamTuner/Categories/Vod');

        ApiClient.getJSON(apiUrl).then(function (categories) {
            instance.loadedVodCategories = categories || [];

            if (!categories || categories.length === 0) {
                statusEl.textContent = 'No VOD categories found. Your provider may not include VOD access on this account.';
                statusEl.style.color = '#cc0000'; statusEl.style.opacity = '1';
                clearFolderCardCategories(view, 'movie');
                return;
            }

            statusEl.textContent = 'Loaded ' + categories.length + ' categories';
            statusEl.style.color = '#52B54B'; statusEl.style.opacity = '1';
            populateFolderCheckboxes(view, 'movie', categories);
        }).catch(function () {
            statusEl.textContent = 'Failed to load categories. Save connection settings first.';
            statusEl.style.color = '#cc0000'; statusEl.style.opacity = '1';
        });
    }

    function getSelectedVodCategoryIds(instance) {
        var view = instance.view;
        var mode = view.querySelector('.selMovieFolderMode').value;

        if (mode === 'custom') {
            // Union of all checked IDs across all folder cards
            var allCheckboxes = view.querySelectorAll('.movieFoldersList .folderCategoryCheckbox');
            if (allCheckboxes.length === 0) {
                return instance.selectedVodCategoryIds;
            }
            var ids = [];
            var seen = {};
            for (var i = 0; i < allCheckboxes.length; i++) {
                if (allCheckboxes[i].checked) {
                    var id = parseInt(allCheckboxes[i].getAttribute('data-category-id'), 10);
                    if (!seen[id]) {
                        ids.push(id);
                        seen[id] = true;
                    }
                }
            }
            return ids;
        }

        // Single mode: flat checkboxes
        var checkboxes = view.querySelectorAll('.vodCategoryCheckbox');
        var ids = [];
        for (var i = 0; i < checkboxes.length; i++) {
            if (checkboxes[i].checked) {
                ids.push(parseInt(checkboxes[i].getAttribute('data-category-id'), 10));
            }
        }
        if (checkboxes.length === 0) {
            return instance.selectedVodCategoryIds;
        }
        return ids;
    }

    // ---- Series Categories (single mode) ----

    function loadSeriesCategories(instance) {
        var view = instance.view;
        var listEl = view.querySelector('.seriesCategoriesList');
        var loadingEl = view.querySelector('.seriesCategoriesLoading');
        var statusEl = view.querySelector('.seriesCategoriesStatus');

        loadingEl.style.display = 'block';
        listEl.innerHTML = '';

        var apiUrl = ApiClient.getUrl('XtreamTuner/Categories/Series');

        ApiClient.getJSON(apiUrl).then(function (categories) {
            loadingEl.style.display = 'none';
            instance.loadedSeriesCategories = categories;

            if (!categories || categories.length === 0) {
                listEl.innerHTML = '<div style="opacity:0.5;">No series categories found. Your provider may not include series access on this account.</div>';
                return;
            }

            if (statusEl) statusEl.textContent = '';

            resetContentItemState(instance, 'series');
            renderCategoryList(view, '.seriesCategoriesList', categories, 'seriesCategoryCheckbox', instance.selectedSeriesCategoryIds, 'series');

            view.querySelector('.btnSelectAllSeriesCategories').disabled = false;
            view.querySelector('.btnDeselectAllSeriesCategories').disabled = false;
            updateCategoryCountBadge(view, 'series');
        }).catch(function () {
            loadingEl.style.display = 'none';
            listEl.innerHTML = '<div style="color:#cc0000;">Failed to load series categories. Save your connection settings first, then try again.</div>';
        });
    }

    // Per-title exclusions survive this — see the note on toggleAllVodCategories.
    function toggleAllSeriesCategories(instance, checked) {
        var view = instance.view;
        var checkboxes = view.querySelectorAll('.seriesCategoryCheckbox');
        for (var i = 0; i < checkboxes.length; i++) {
            checkboxes[i].checked = checked;
        }
        updateCategoryCountBadge(view, 'series');
    }

    // ---- Series Categories (multi/folder mode) ----

    function loadSeriesCategoriesMulti(instance) {
        var view = instance.view;
        var statusEl = view.querySelector('.seriesCategoriesMultiStatus');
        statusEl.textContent = 'Loading...';
        statusEl.style.opacity = '0.5';

        var apiUrl = ApiClient.getUrl('XtreamTuner/Categories/Series');

        ApiClient.getJSON(apiUrl).then(function (categories) {
            instance.loadedSeriesCategories = categories || [];

            if (!categories || categories.length === 0) {
                statusEl.textContent = 'No series categories found. Your provider may not include series access on this account.';
                statusEl.style.color = '#cc0000'; statusEl.style.opacity = '1';
                clearFolderCardCategories(view, 'series');
                return;
            }

            statusEl.textContent = 'Loaded ' + categories.length + ' categories';
            statusEl.style.color = '#52B54B'; statusEl.style.opacity = '1';
            populateFolderCheckboxes(view, 'series', categories);
        }).catch(function () {
            statusEl.textContent = 'Failed to load categories. Save connection settings first.';
            statusEl.style.color = '#cc0000'; statusEl.style.opacity = '1';
        });
    }

    function getSelectedSeriesCategoryIds(instance) {
        var view = instance.view;
        var mode = view.querySelector('.selSeriesFolderMode').value;

        if (mode === 'custom') {
            var allCheckboxes = view.querySelectorAll('.seriesFoldersList .folderCategoryCheckbox');
            if (allCheckboxes.length === 0) {
                return instance.selectedSeriesCategoryIds;
            }
            var ids = [];
            var seen = {};
            for (var i = 0; i < allCheckboxes.length; i++) {
                if (allCheckboxes[i].checked) {
                    var id = parseInt(allCheckboxes[i].getAttribute('data-category-id'), 10);
                    if (!seen[id]) {
                        ids.push(id);
                        seen[id] = true;
                    }
                }
            }
            return ids;
        }

        var checkboxes = view.querySelectorAll('.seriesCategoryCheckbox');
        var ids = [];
        for (var i = 0; i < checkboxes.length; i++) {
            if (checkboxes[i].checked) {
                ids.push(parseInt(checkboxes[i].getAttribute('data-category-id'), 10));
            }
        }
        if (checkboxes.length === 0) {
            return instance.selectedSeriesCategoryIds;
        }
        return ids;
    }

    // ---- Sync operations ----

    function renderProgressBar(resultEl, progress) {
        var total = progress.Total || 0;
        var completed = progress.Completed || 0;
        var skipped = progress.Skipped || 0;
        var failed = progress.Failed || 0;
        var phase = progress.Phase || 'Working';
        var pct = total > 0 ? Math.round((completed / total) * 100) : 0;

        resultEl.innerHTML =
            '<div style="margin:0.5em 0;">' +
                '<div style="background:rgba(128,128,128,0.2); border-radius:4px; height:20px; overflow:hidden;">' +
                    '<div style="background:#52B54B; height:100%; width:' + pct + '%; transition:width 0.3s ease; border-radius:4px;"></div>' +
                '</div>' +
                '<div style="opacity:0.7; margin-top:0.4em; font-size:0.9em;">' +
                    escapeHtml(phase) + ' \u2014 ' + completed + ' / ' + total +
                    ' (' + skipped + ' skipped, ' + failed + ' failed) \u2014 ' + pct + '%' +
                '</div>' +
            '</div>';
    }

    function pollSyncProgress(view, type) {
        var resultClass = type === 'Movies' ? '.syncMoviesResult' : '.syncSeriesResult';
        var resultEl = view.querySelector(resultClass);
        var apiUrl = ApiClient.getUrl('XtreamTuner/Sync/Status');

        var intervalId = setInterval(function () {
            ApiClient.getJSON(apiUrl).then(function (status) {
                var progress = status[type];
                if (!progress) return;
                if (progress.IsRunning) {
                    renderProgressBar(resultEl, progress);
                }
            }).catch(function () {
                // Ignore poll errors; the POST completion will handle cleanup
            });
        }, 500);

        return intervalId;
    }

    function syncMovies(view) {
        var resultEl = view.querySelector('.syncMoviesResult');
        var btn = view.querySelector('.btnSyncMovies');
        btn.disabled = true;
        resultEl.innerHTML = '<span style="opacity:0.5;">Starting movie sync...</span>';

        var pollId = pollSyncProgress(view, 'Movies');
        var apiUrl = ApiClient.getUrl('XtreamTuner/Sync/Movies');

        ApiClient.ajax({
            type: 'POST',
            url: apiUrl,
            dataType: 'json'
        }).then(function (result) {
            clearInterval(pollId);
            btn.disabled = false;
            var msg = result.Success
                ? result.Message + ' (Total: ' + result.Total + ', Skipped: ' + result.Skipped + ', Failed: ' + result.Failed + ')'
                : result.Message;
            setPillResult(resultEl, result.Success, msg);
        }).catch(function () {
            clearInterval(pollId);
            btn.disabled = false;
            setPillResult(resultEl, false, 'Movie sync request failed. Check server logs for details.');
        });
    }

    function syncSeries(view) {
        var resultEl = view.querySelector('.syncSeriesResult');
        var btn = view.querySelector('.btnSyncSeries');
        btn.disabled = true;
        resultEl.innerHTML = '<span style="opacity:0.5;">Starting series sync...</span>';

        var pollId = pollSyncProgress(view, 'Series');
        var apiUrl = ApiClient.getUrl('XtreamTuner/Sync/Series');

        ApiClient.ajax({
            type: 'POST',
            url: apiUrl,
            dataType: 'json'
        }).then(function (result) {
            clearInterval(pollId);
            btn.disabled = false;
            var msg = result.Success
                ? result.Message + ' (Total: ' + result.Total + ', Skipped: ' + result.Skipped + ', Failed: ' + result.Failed + ')'
                : result.Message;
            setPillResult(resultEl, result.Success, msg);
        }).catch(function () {
            clearInterval(pollId);
            btn.disabled = false;
            setPillResult(resultEl, false, 'Series sync request failed. Check server logs for details.');
        });
    }

    function deleteContent(view, type) {
        var label = type === 'Movies' ? 'movies' : 'series';
        var resultClass = type === 'Movies' ? '.deleteMoviesResult' : '.deleteSeriesResult';
        var btnClass = type === 'Movies' ? '.btnDeleteMovies' : '.btnDeleteSeries';
        var resultEl = view.querySelector(resultClass);
        var btn = view.querySelector(btnClass);

        // Inline confirm instead of window.confirm
        resultEl.innerHTML =
            '<div style="display:flex; gap:0.5em; align-items:center; flex-wrap:wrap; margin-top:0.3em;">' +
            '<span style="font-size:0.9em; opacity:0.7;">Delete ALL ' + label + '? This cannot be undone.</span>' +
            '<button type="button" class="deleteConfirmYes" style="background:#c0392b; color:white; border:none; border-radius:4px; padding:0.3em 0.8em; font-size:0.85em; cursor:pointer; font-weight:600;">Yes, delete all</button>' +
            '<button type="button" class="deleteConfirmNo button-secondary" style="font-size:0.85em; padding:0.3em 0.8em; border:1px solid rgba(128,128,128,0.3); border-radius:4px; background:transparent; color:inherit; cursor:pointer;">Cancel</button>' +
            '</div>';

        resultEl.querySelector('.deleteConfirmNo').addEventListener('click', function () {
            resultEl.innerHTML = '';
        });

        resultEl.querySelector('.deleteConfirmYes').addEventListener('click', function () {
            btn.disabled = true;
            resultEl.innerHTML = '<span style="opacity:0.5;">Deleting ' + label + '...</span>';

            ApiClient.ajax({
                type: 'DELETE',
                url: ApiClient.getUrl('XtreamTuner/Content/' + type),
                dataType: 'json'
            }).then(function (result) {
                btn.disabled = false;
                setPillResult(resultEl, result.Success, result.Message);
            }).catch(function () {
                btn.disabled = false;
                setPillResult(resultEl, false, 'Delete request failed. Check server logs.');
            });
        });
    }

    function refreshCache(view) {
        var resultEl = view.querySelector('.refreshCacheResult');
        resultEl.innerHTML = '<span style="opacity:0.5;">Refreshing cache...</span>';

        var apiUrl = ApiClient.getUrl('XtreamTuner/RefreshCache');

        ApiClient.ajax({
            type: 'POST',
            url: apiUrl
        }).then(function () {
            setPillResult(resultEl, true, 'Cache refreshed successfully!');
        }).catch(function () {
            setPillResult(resultEl, false, 'Failed to refresh cache.');
        });
    }

    function syncGuideMappings(view) {
        var btn = view.querySelector('.btnSyncGuideMappings');
        var resultEl = view.querySelector('.guideMappingResult');
        btn.disabled = true;
        resultEl.style.display = '';
        resultEl.innerHTML = '<span style="opacity:0.5;">Syncing guide mappings...</span>';

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('XtreamTuner/SyncGuideMappings'),
            dataType: 'json'
        }).then(function (result) {
            btn.disabled = false;
            setPillResult(resultEl, result.Success, result.Message);
        }).catch(function () {
            btn.disabled = false;
            setPillResult(resultEl, false, 'Sync request failed. Check server logs.');
        });
    }

    // ---- Dashboard ----

    var dashboardPollId = null;

    function loadDashboard(view) {
        var apiUrl = ApiClient.getUrl('XtreamTuner/Dashboard');

        ApiClient.getJSON(apiUrl).then(function (data) {
            loadDashboard._retries = 0;
            renderDashboardStatus(view, data);

            // Auto-bust browser cache when plugin was updated.
            // localStorage remembers the last-seen version; if the server
            // reports a different one, pre-warm both resources and reload.
            var prevVer = localStorage.getItem('xtream-plugin-version');
            if (data.PluginVersion && prevVer && data.PluginVersion !== prevVer
                && !sessionStorage.getItem('xtream-cache-bust')) {
                sessionStorage.setItem('xtream-cache-bust', '1');
                var v = document.documentElement.getAttribute('data-appversion') || '';
                Promise.all([
                    fetch('configurationpage?name=xtreamconfig&v=' + v, { cache: 'reload' }),
                    fetch('configurationpage?name=xtreamconfigjs&v=' + v, { cache: 'reload' })
                ]).then(function () { location.reload(); });
                return;
            }
            if (data.PluginVersion) localStorage.setItem('xtream-plugin-version', data.PluginVersion);
            sessionStorage.removeItem('xtream-cache-bust');

            renderLibraryStats(view, data);
            renderDashboardHistory(view, data);

            if (data.IsRunning) {
                startDashboardProgressPolling(view);
            } else {
                stopDashboardProgressPolling();
                view.querySelector('.dashboardLiveProgress').style.display = 'none';
            }
        }).catch(function () {
            if ((loadDashboard._retries = (loadDashboard._retries || 0) + 1) <= 5) {
                setTimeout(function () { loadDashboard(view); }, 4000);
            }
        });

        loadFailedItems(view);
    }

    function loadFailedItems(view) {
        var card = view.querySelector('.dashboardFailedItemsCard');
        if (!card) return;

        ApiClient.getJSON(ApiClient.getUrl('XtreamTuner/Sync/FailedItems')).then(function (items) {
            if (!items || items.length === 0) {
                card.style.display = 'none';
                return;
            }
            card.style.display = '';
            var content = view.querySelector('.dashboardFailedItemsContent');
            var rows = items.map(function (item) {
                var time = item.FailedAt ? formatTimeAgo(new Date(item.FailedAt)) : '';
                return '<tr>' +
                    '<td style="padding:0.4em 0.6em; opacity:0.7;">' + (item.ItemType || '') + '</td>' +
                    '<td style="padding:0.4em 0.6em;">' + escHtml(item.Name || '') + '</td>' +
                    '<td style="padding:0.4em 0.6em; opacity:0.7; font-size:0.85em;">' + escHtml(item.ErrorMessage || '') + '</td>' +
                    '<td style="padding:0.4em 0.6em; opacity:0.6; font-size:0.85em; white-space:nowrap;">' + time + '</td>' +
                    '</tr>';
            }).join('');
            content.innerHTML = '<table style="width:100%; border-collapse:collapse; font-size:0.9em;">' +
                '<thead><tr>' +
                '<th style="text-align:left; padding:0.4em 0.6em; opacity:0.6; border-bottom:1px solid rgba(128,128,128,0.2);">Type</th>' +
                '<th style="text-align:left; padding:0.4em 0.6em; opacity:0.6; border-bottom:1px solid rgba(128,128,128,0.2);">Name</th>' +
                '<th style="text-align:left; padding:0.4em 0.6em; opacity:0.6; border-bottom:1px solid rgba(128,128,128,0.2);">Error</th>' +
                '<th style="text-align:left; padding:0.4em 0.6em; opacity:0.6; border-bottom:1px solid rgba(128,128,128,0.2);">When</th>' +
                '</tr></thead><tbody>' + rows + '</tbody></table>';
        }).catch(function () {
            card.style.display = 'none';
        });
    }

    function escHtml(str) {
        return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function retryFailed(view) {
        var btn = view.querySelector('.btnRetryFailed');
        var result = view.querySelector('.retryFailedResult');
        if (btn) btn.disabled = true;
        if (result) result.textContent = 'Retrying...';

        ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('XtreamTuner/Sync/RetryFailed') })
            .then(function (data) {
                if (result) result.textContent = data.Message || 'Done.';
                loadFailedItems(view);
                loadDashboard(view);
                if (btn) btn.disabled = false;
            })
            .catch(function () {
                if (result) result.textContent = 'Retry request failed.';
                if (btn) btn.disabled = false;
            });
    }

    function checkForUpdate(view) {
        // No beta param — server reads UseBetaChannel from its own config.
        // This lets checkForUpdate run independently of loadConfig.
        var apiUrl = ApiClient.getUrl('XtreamTuner/CheckUpdate');

        ApiClient.getJSON(apiUrl).then(function (data) {
            var banner = view.querySelector('.updateBanner');
            if (!banner) return;

            // Enhance version label with update status
            var versionEl = view.querySelector('.pluginVersion');
            if (versionEl && data.CurrentVersion) {
                if (data.UpdateInstalled) {
                    versionEl.innerHTML = 'v' + escapeHtml(data.CurrentVersion) +
                        ' <span style="color:#e67e22;">\u2192 v' + escapeHtml(data.LatestVersion) + ' (restart needed)</span>';
                } else if (data.UpdateAvailable) {
                    versionEl.innerHTML = 'v' + escapeHtml(data.CurrentVersion) +
                        ' <span style="color:#e67e22;">— update available</span>';
                } else {
                    versionEl.innerHTML = 'v' + escapeHtml(data.CurrentVersion) +
                        ' <span style="color:#52B54B;">— latest</span>';
                }
            }

            if (data.UpdateInstalled) {
                // Update already installed, show restart banner
                banner.style.background = 'rgba(230,126,34,0.15)';
                banner.style.borderColor = 'rgba(230,126,34,0.4)';
                view.querySelector('.updateBannerTitle').textContent = 'Update Installed:';
                view.querySelector('.updateBannerText').textContent =
                    'v' + data.LatestVersion + ' has been installed. Restart Emby to apply.';
                view.querySelector('.btnInstallUpdate').style.display = 'none';
                view.querySelector('.btnRestartEmby').style.display = '';
                var link = view.querySelector('.updateBannerLink');
                if (data.ReleaseUrl) {
                    link.href = data.ReleaseUrl;
                    link.style.display = '';
                } else {
                    link.style.display = 'none';
                }
                view.querySelector('.updateStatus').style.display = 'none';
                banner.style.display = 'block';
            } else if (data.UpdateAvailable) {
                var isBeta = !!data.IsPreRelease;
                banner.style.background = isBeta ? 'rgba(230,152,34,0.15)' : 'rgba(82,181,75,0.15)';
                banner.style.borderColor = isBeta ? 'rgba(230,152,34,0.4)' : 'rgba(82,181,75,0.4)';
                view.querySelector('.updateBannerTitle').textContent = 'Update Available:';
                var betaLabel = isBeta ? ' (beta)' : '';
                view.querySelector('.updateBannerText').textContent =
                    'v' + data.LatestVersion + betaLabel + ' is available (you have v' + data.CurrentVersion + ')';
                view.querySelector('.btnInstallUpdate').style.display = '';
                view.querySelector('.btnInstallUpdate').disabled = false;
                view.querySelector('.btnRestartEmby').style.display = 'none';
                var link = view.querySelector('.updateBannerLink');
                if (data.ReleaseUrl) {
                    link.href = data.ReleaseUrl;
                    link.style.display = '';
                } else {
                    link.style.display = 'none';
                }
                // Hide install button if no download URL
                if (!data.DownloadUrl) {
                    view.querySelector('.btnInstallUpdate').style.display = 'none';
                }
                view.querySelector('.updateStatus').style.display = 'none';
                banner.style.display = 'block';
            } else {
                banner.style.display = 'none';
            }
        }).catch(function (err) {
            console.error('Xtream: update check failed', err);
        });
    }

    function installUpdate(view) {
        var btn = view.querySelector('.btnInstallUpdate');
        var statusEl = view.querySelector('.updateStatus');
        btn.disabled = true;
        btn.textContent = 'Installing...';
        statusEl.style.display = 'block';
        statusEl.innerHTML = '<span style="opacity:0.5;">Downloading and installing update...</span>';

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('XtreamTuner/InstallUpdate'),
            dataType: 'json'
        }).then(function (result) {
            if (result.Success) {
                statusEl.innerHTML = '<span style="color:#52B54B;">' + escapeHtml(result.Message) + '</span>';
                // Switch banner to restart state
                var banner = view.querySelector('.updateBanner');
                banner.style.background = 'rgba(230,126,34,0.15)';
                banner.style.borderColor = 'rgba(230,126,34,0.4)';
                view.querySelector('.updateBannerTitle').textContent = 'Update Installed:';
                btn.style.display = 'none';
                view.querySelector('.btnRestartEmby').style.display = '';
            } else {
                statusEl.innerHTML = '<span style="color:#cc0000;">' + escapeHtml(result.Message) + '</span>';
                btn.disabled = false;
                btn.textContent = 'Update Now';
            }
        }).catch(function () {
            statusEl.innerHTML = '<span style="color:#cc0000;">Install request failed. Check server logs.</span>';
            btn.disabled = false;
            btn.textContent = 'Update Now';
        });
    }

    function restartEmby(view) {
        if (!confirm('Are you sure you want to restart Emby? All active streams will be interrupted.')) {
            return;
        }

        var btn = view.querySelector('.btnRestartEmby');
        var statusEl = view.querySelector('.updateStatus');
        btn.disabled = true;
        btn.textContent = 'Restarting...';
        statusEl.style.display = 'block';
        statusEl.innerHTML = '<span style="opacity:0.5;">Restarting Emby server...</span>';

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('XtreamTuner/RestartEmby')
        }).then(function () {
            statusEl.innerHTML = '<span style="opacity:0.5;">Waiting for server to come back...</span>';
            pollServerReady(view);
        }).catch(function () {
            // Server may have already restarted and dropped the connection
            statusEl.innerHTML = '<span style="opacity:0.5;">Waiting for server to come back...</span>';
            pollServerReady(view);
        });
    }

    function pollServerReady(view) {
        var statusEl = view.querySelector('.updateStatus');
        var attempts = 0;
        var maxAttempts = 60; // 60 * 2s = 2 minutes

        var pollId = setInterval(function () {
            attempts++;
            if (attempts > maxAttempts) {
                clearInterval(pollId);
                statusEl.innerHTML = '<span style="color:#cc0000;">Server did not come back within 2 minutes. Try reloading manually.</span>';
                return;
            }

            var xhr = new XMLHttpRequest();
            xhr.open('GET', ApiClient.getUrl('System/Info/Public'), true);
            xhr.timeout = 3000;
            xhr.onload = function () {
                if (xhr.status >= 200 && xhr.status < 300) {
                    clearInterval(pollId);
                    statusEl.innerHTML = '<span style="color:#52B54B;">Server is back! Reloading...</span>';
                    setTimeout(function () { window.location.reload(); }, 1000);
                }
            };
            xhr.onerror = function () { };
            xhr.ontimeout = function () { };
            xhr.send();
        }, 2000);
    }

    function renderDashboardStatus(view, data) {
        // Show plugin version from dashboard data (independent of update check)
        var versionEl = view.querySelector('.pluginVersion');
        if (versionEl && data.PluginVersion) {
            versionEl.textContent = 'v' + data.PluginVersion;
        }

        var container = view.querySelector('.dashboardStatusContent');
        var statsContainer = view.querySelector('.dashboardStatusStats');

        if (!data.LastSync) {
            container.innerHTML = '<span class="status-badge idle">No syncs yet</span>';
            statsContainer.style.display = 'none';
            return;
        }

        var last = data.LastSync;

        // Look for a companion scan (movies if last was series-only, or vice versa).
        // Movies always run before series in a combined sync, so it's in History[1+].
        var companion = null;
        if (data.History && data.History.length > 1) {
            for (var i = 1; i < data.History.length; i++) {
                var entry = data.History[i];
                if (last.WasSeriesSync && !last.WasMovieSync && entry.WasMovieSync) {
                    companion = entry;
                    break;
                }
                if (last.WasMovieSync && !last.WasSeriesSync && entry.WasSeriesSync) {
                    companion = entry;
                    break;
                }
            }
        }

        var overallSuccess = last.Success && (!companion || companion.Success);
        var badgeClass = overallSuccess ? 'success' : 'failed';
        var badgeText = overallSuccess ? 'Success' : 'Failed';

        var duration = Math.round((new Date(last.EndTime) - new Date(last.StartTime)) / 1000);
        var durationText = duration >= 60
            ? Math.floor(duration / 60) + 'm ' + (duration % 60) + 's'
            : duration + 's';

        var timeAgo = formatTimeAgo(new Date(last.EndTime));

        container.innerHTML =
            '<span class="status-badge ' + badgeClass + '">' + badgeText + '</span>' +
            '<span style="margin-left:0.8em; opacity:0.6; font-size:0.9em;">' + timeAgo + ' (' + durationText + ')</span>';

        var movieEntry = last.WasMovieSync ? last : (companion && companion.WasMovieSync ? companion : null);
        var seriesEntry = last.WasSeriesSync ? last : (companion && companion.WasSeriesSync ? companion : null);

        function statTile(value, label, color) {
            return '<div class="dashboard-stat"><div class="stat-value" style="color:' + (color || '#52B54B') + ';">' + value + '</div><div class="stat-label">' + label + '</div></div>';
        }
        function rowLabel(text) {
            return '<div style="font-size:0.75em; font-weight:600; opacity:0.45; text-transform:uppercase; letter-spacing:0.06em; margin-bottom:0.35em;">' + text + '</div>';
        }

        var mAdded = movieEntry ? (movieEntry.MoviesAdded || 0) : 0;
        var mDeleted = movieEntry ? (movieEntry.MoviesDeleted || 0) : 0;
        var sAdded = seriesEntry ? (seriesEntry.EpisodeAdded || 0) : 0;
        var sDeleted = seriesEntry ? (seriesEntry.EpisodeDeleted || 0) : 0;

        // Single shared 5-column grid so Movies and Episodes tiles are always the same width
        var statsHtml = '';
        if (movieEntry || seriesEntry) {
            statsHtml += '<div style="display:grid; grid-template-columns:repeat(5,1fr); gap:0.5em;">';

            if (movieEntry) {
                var movDiskTotal = (data.LibraryStats && data.LibraryStats.MovieCount) || 0;
                var movUpToDate = Math.max(0, movDiskTotal - mAdded);
                statsHtml += '<div style="grid-column:1/-1;">' + rowLabel('Movies') + '</div>';
                statsHtml +=
                    statTile(movDiskTotal, 'Total') +
                    statTile(movUpToDate, 'Up to date', '#aaa') +
                    statTile(mAdded > 0 ? '+' + mAdded : '0', 'Added', mAdded > 0 ? '#52B54B' : '#aaa') +
                    statTile(mDeleted > 0 ? mDeleted : '0', 'Deleted', mDeleted > 0 ? '#e74c3c' : '#aaa') +
                    statTile(movieEntry.MoviesFailed, 'Failed', movieEntry.MoviesFailed > 0 ? '#cc0000' : '#52B54B');
            }

            if (seriesEntry) {
                var epDiskTotal = (data.LibraryStats && data.LibraryStats.EpisodeCount) || 0;
                var epUpToDate = Math.max(0, epDiskTotal - sAdded);
                statsHtml += '<div style="grid-column:1/-1;">' + rowLabel('Episodes') + '</div>';
                statsHtml +=
                    statTile(epDiskTotal, 'Total') +
                    statTile(epUpToDate, 'Up to date', '#aaa') +
                    statTile(sAdded > 0 ? '+' + sAdded : '0', 'Added', sAdded > 0 ? '#52B54B' : '#aaa') +
                    statTile(sDeleted > 0 ? sDeleted : '0', 'Deleted', sDeleted > 0 ? '#e74c3c' : '#aaa') +
                    statTile(seriesEntry.EpisodeFailed, 'Failed', seriesEntry.EpisodeFailed > 0 ? '#cc0000' : '#52B54B');
            }

            statsHtml += '</div>';
        }

        // Expandable added-title lists (outside the shared grid)
        if (movieEntry && mAdded > 0 && movieEntry.AddedMovieTitles && movieEntry.AddedMovieTitles.length > 0) {
            statsHtml += '<details style="margin-top:0.3em; margin-bottom:0.4em; font-size:0.82em; opacity:0.65;">' +
                '<summary style="cursor:pointer; list-style:none;">Show added movie titles</summary>' +
                '<ul style="margin:0.3em 0 0 1em; padding:0;">' +
                movieEntry.AddedMovieTitles.map(function(t) { return '<li>' + escapeHtml(t) + '</li>'; }).join('') +
                (mAdded > movieEntry.AddedMovieTitles.length
                    ? '<li style="opacity:0.5;">\u2026and ' + (mAdded - movieEntry.AddedMovieTitles.length) + ' more</li>'
                    : '') +
                '</ul></details>';
        }
        if (seriesEntry && sAdded > 0 && seriesEntry.AddedSeriesTitles && seriesEntry.AddedSeriesTitles.length > 0) {
            statsHtml += '<details style="margin-top:0.3em; font-size:0.82em; opacity:0.65;">' +
                '<summary style="cursor:pointer; list-style:none;">Show added series titles</summary>' +
                '<ul style="margin:0.3em 0 0 1em; padding:0;">' +
                seriesEntry.AddedSeriesTitles.map(function(t) { return '<li>' + escapeHtml(t) + '</li>'; }).join('') +
                (sAdded > seriesEntry.AddedSeriesTitles.length
                    ? '<li style="opacity:0.5;">\u2026and ' + (sAdded - seriesEntry.AddedSeriesTitles.length) + ' more</li>'
                    : '') +
                '</ul></details>';
        }

        if (data.AutoSyncOn && data.NextSyncTime) {
            var delta = new Date(data.NextSyncTime) - new Date();
            if (delta > 0) {
                var hrs = Math.floor(delta / 3600000);
                var mins = Math.floor((delta % 3600000) / 60000);
                var nextText = hrs > 0 ? 'Next sync in ' + hrs + 'h ' + mins + 'm' : 'Next sync in ' + mins + 'm';
                statsHtml += '<div style="margin-top:0.6em; font-size:0.82em; opacity:0.5;">' + nextText + '</div>';
            }
        }

        if (statsHtml) {
            statsContainer.innerHTML = statsHtml;
            statsContainer.style.display = 'block';
        } else {
            statsContainer.style.display = 'none';
        }
    }

    function renderLibraryStats(view, data) {
        var container = view.querySelector('.dashboardLibraryContent');
        var stats = data.LibraryStats || {};

        function libTile(value, label, sub, extraStyle) {
            return '<div class="dashboard-stat"' + (extraStyle ? ' style="' + extraStyle + '"' : '') + '>' +
                '<div class="stat-value">' + value + '</div>' +
                '<div class="stat-label">' + label +
                    (sub ? '<br><span style="opacity:0.5; font-size:0.85em;">' + sub + '</span>' : '') +
                '</div>' +
            '</div>';
        }

        // 3-column grid: Movies spans all 3 columns (row 1), series tiles fill one each (row 2)
        var html = '<div style="display:grid; grid-template-columns: repeat(3, 1fr); gap: 0.5em;">';
        html += libTile(stats.MovieCount || 0, 'Movies', null, 'grid-column: 1 / -1;');
        html += libTile(stats.SeriesCount || 0, 'Shows');
        html += libTile(stats.SeasonCount || 0, 'Seasons');
        html += libTile(stats.EpisodeCount || 0, 'Episodes');
        html += '</div>';

        if (stats.LiveTvChannels > 0) {
            html += '<div style="margin-top:0.5em;">' +
                libTile(stats.LiveTvChannels, 'Live TV channels') +
            '</div>';
        }

        container.innerHTML = html;
    }

    function renderDashboardHistory(view, data) {
        var container = view.querySelector('.dashboardHistoryContent');
        var hasHistory = !!(data.History && data.History.length > 0);
        updateSyncCTAEmphasis(view, hasHistory);

        if (!hasHistory) {
            container.innerHTML = '<div style="opacity:0.5;">No sync history yet</div>';
            return;
        }

        var html = '<table class="dashboard-history-table">';
        html += '<thead><tr><th>Time</th><th>Status</th><th>Duration</th><th>Movies</th><th>Episodes</th></tr></thead>';
        html += '<tbody>';

        function historyMovieCol(e) {
            var finalTotal = (e.MoviesTotal || 0) - (e.MoviesDeleted || 0);
            return finalTotal +
                ' <span style="opacity:0.5;">(' +
                '<span style="color:#52B54B; opacity:1;">+' + (e.MoviesAdded || 0) + '</span> ' +
                '<span style="color:#e74c3c; opacity:1;">-' + (e.MoviesDeleted || 0) + '</span>, ' +
                e.MoviesFailed + ' fail' +
                ')</span>';
        }
        function historySeriesCol(e) {
            return (e.EpisodeTotal || 0) +
                ' <span style="opacity:0.5;">(' +
                '<span style="color:#52B54B; opacity:1;">+' + (e.EpisodeAdded || 0) + '</span> ' +
                '<span style="color:#e74c3c; opacity:1;">-' + (e.EpisodeDeleted || 0) + '</span>, ' +
                (e.EpisodeSkipped || 0) + ' skip, ' +
                (e.EpisodeFailed || 0) + ' fail' +
                ')</span>';
        }

        var dash = '<span style="opacity:0.3;">\u2014</span>';

        var i = 0;
        while (i < data.History.length) {
            var entry = data.History[i];
            var next = i + 1 < data.History.length ? data.History[i + 1] : null;

            // Pair a series-only entry with the following movie-only entry (or vice versa)
            var movieEntry = null, seriesEntry = null, consumed = 1;
            if (next && entry.WasSeriesSync && !entry.WasMovieSync && next.WasMovieSync && !next.WasSeriesSync) {
                seriesEntry = entry; movieEntry = next; consumed = 2;
            } else if (next && entry.WasMovieSync && !entry.WasSeriesSync && next.WasSeriesSync && !next.WasMovieSync) {
                movieEntry = entry; seriesEntry = next; consumed = 2;
            } else {
                movieEntry = entry.WasMovieSync ? entry : null;
                seriesEntry = entry.WasSeriesSync ? entry : null;
            }

            // Use the most recent entry for time/status; sum durations if paired
            var primary = entry;
            var success = entry.Success;
            if (consumed === 2) {
                success = (movieEntry ? movieEntry.Success : true) && (seriesEntry ? seriesEntry.Success : true);
            }
            var badgeClass = success ? 'success' : 'failed';
            var badgeText = success ? 'Success' : 'Failed';

            var dur = Math.round((new Date(primary.EndTime) - new Date(primary.StartTime)) / 1000);
            if (consumed === 2 && next) {
                dur += Math.round((new Date(next.EndTime) - new Date(next.StartTime)) / 1000);
            }
            var durationText = dur >= 60 ? Math.floor(dur / 60) + 'm ' + (dur % 60) + 's' : dur + 's';
            var timeStr = formatTimeAgo(new Date(primary.EndTime));

            var movieCol = movieEntry ? historyMovieCol(movieEntry) : dash;
            var seriesCol = seriesEntry ? historySeriesCol(seriesEntry) : dash;

            html += '<tr>';
            html += '<td>' + timeStr + '</td>';
            html += '<td><span class="status-badge ' + badgeClass + '">' + badgeText + '</span></td>';
            html += '<td>' + durationText + '</td>';
            html += '<td>' + movieCol + '</td>';
            html += '<td>' + seriesCol + '</td>';
            html += '</tr>';

            i += consumed;
        }

        html += '</tbody></table>';
        container.innerHTML = html;
    }

    function startDashboardProgressPolling(view) {
        stopDashboardProgressPolling();
        var progressCard = view.querySelector('.dashboardLiveProgress');
        progressCard.style.display = 'block';

        dashboardPollId = setInterval(function () {
            var apiUrl = ApiClient.getUrl('XtreamTuner/Sync/Status');
            ApiClient.getJSON(apiUrl).then(function (status) {
                var movieProg = status.Movies;
                var seriesProg = status.Series;
                var isRunning = (movieProg && movieProg.IsRunning) || (seriesProg && seriesProg.IsRunning);

                if (!isRunning) {
                    stopDashboardProgressPolling();
                    progressCard.style.display = 'none';
                    loadDashboard(view);
                    return;
                }

                var active = (movieProg && movieProg.IsRunning) ? movieProg : seriesProg;
                var total = active.Total || 0;
                var completed = active.Completed || 0;
                var pct = total > 0 ? Math.round((completed / total) * 100) : 0;

                view.querySelector('.dashboardProgressPhase').textContent = active.Phase || 'Working...';
                view.querySelector('.dashboardProgressBarFill').style.width = pct + '%';
                view.querySelector('.dashboardProgressDetail').textContent =
                    completed + ' / ' + total + ' (' + active.Skipped + ' skipped, ' + active.Failed + ' failed) \u2014 ' + pct + '%';
            }).catch(function () { });
        }, 500);
    }

    function stopDashboardProgressPolling() {
        if (dashboardPollId) {
            clearInterval(dashboardPollId);
            dashboardPollId = null;
        }
    }

    function dashboardSyncAll(instance) {
        var view = instance.view;
        var btn = view.querySelector('.btnDashboardSyncAll');
        var resultEl = view.querySelector('.dashboardSyncAllResult');
        btn.disabled = true;
        resultEl.innerHTML = '<span style="opacity:0.5;">Starting sync...</span>';

        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            var doMovies = config.SyncMovies;
            var doSeries = config.SyncSeries;

            if (!doMovies && !doSeries) {
                btn.disabled = false;
                setPillResult(resultEl, false, 'Nothing to sync. Enable Movies or Series sync in Settings first.');
                return;
            }

            startDashboardProgressPolling(view);

            var movieUrl = ApiClient.getUrl('XtreamTuner/Sync/Movies');
            var seriesUrl = ApiClient.getUrl('XtreamTuner/Sync/Series');
            var movieMsg = '';

            var moviePromise = doMovies
                ? ApiClient.ajax({ type: 'POST', url: movieUrl, dataType: 'json' })
                : Promise.resolve(null);

            moviePromise.then(function (movieResult) {
                if (movieResult) {
                    movieMsg = movieResult.Success
                        ? 'Movies: ' + movieResult.Total + ' total, ' + movieResult.Skipped + ' skipped, ' + movieResult.Failed + ' failed'
                        : 'Movies failed: ' + movieResult.Message;
                }

                if (doSeries) {
                    var prefix = movieMsg ? escapeHtml(movieMsg) + ' \u2014 ' : '';
                    resultEl.innerHTML = '<span style="opacity:0.5;">' + prefix + 'Starting series sync...</span>';

                    return ApiClient.ajax({ type: 'POST', url: seriesUrl, dataType: 'json' }).then(function (seriesResult) {
                        stopDashboardProgressPolling();
                        view.querySelector('.dashboardLiveProgress').style.display = 'none';
                        btn.disabled = false;

                        var seriesMsg = seriesResult.Success
                            ? 'Series: ' + seriesResult.Total + ' total, ' + seriesResult.Skipped + ' skipped, ' + seriesResult.Failed + ' failed'
                            : 'Series failed: ' + seriesResult.Message;

                        var parts = [];
                        if (movieMsg) parts.push(movieMsg);
                        parts.push(seriesMsg);
                        var overallSuccess = (!movieResult || movieResult.Success) && seriesResult.Success;
                        setPillResult(resultEl, overallSuccess, parts.join(' | '));
                        loadDashboard(view);
                    });
                } else {
                    stopDashboardProgressPolling();
                    view.querySelector('.dashboardLiveProgress').style.display = 'none';
                    btn.disabled = false;
                    setPillResult(resultEl, !!(movieResult && movieResult.Success), movieMsg);
                    loadDashboard(view);
                }
            }).catch(function () {
                stopDashboardProgressPolling();
                view.querySelector('.dashboardLiveProgress').style.display = 'none';
                btn.disabled = false;
                setPillResult(resultEl, false, 'Sync request failed. Check server logs.');
                loadDashboard(view);
            });
        }).catch(function () {
            btn.disabled = false;
            setPillResult(resultEl, false, 'Failed to load config.');
        });
    }

    function formatTimeAgo(date) {
        var now = new Date();
        var diff = Math.round((now - date) / 1000);
        if (diff < 60) return 'just now';
        if (diff < 3600) return Math.floor(diff / 60) + 'm ago';
        if (diff < 86400) return Math.floor(diff / 3600) + 'h ago';
        return date.toLocaleDateString() + ' ' + date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    }

    function setupCategorySearch(view, inputSelector, listSelector) {
        var input = view.querySelector(inputSelector);
        if (!input) return;
        input.addEventListener('input', function () {
            var filter = input.value.toLowerCase();
            var items = view.querySelectorAll(listSelector + ' .checkboxContainer');
            for (var i = 0; i < items.length; i++) {
                var text = items[i].textContent.toLowerCase();
                items[i].style.display = text.indexOf(filter) >= 0 ? '' : 'none';
            }
        });
    }

    function escapeHtml(text) {
        var div = document.createElement('div');
        div.appendChild(document.createTextNode(text));
        return div.innerHTML;
    }

    // ---- UX helpers ----

    function setPillResult(el, isSuccess, message) {
        var cls = isSuccess ? 'success' : 'error';
        var icon = isSuccess ? '\u2713' : '\u2717';
        el.innerHTML = '<span class="result-pill ' + cls + '">' + icon + '  ' + escapeHtml(message) + '</span>';
    }


    function updateCategoryCountBadge(view, type) {
        var map = {
            vod:    { badge: '.vodCategoryCountBadge',    checkbox: '.vodCategoryCheckbox' },
            series: { badge: '.seriesCategoryCountBadge', checkbox: '.seriesCategoryCheckbox' },
            live:   { badge: '.liveCategoryCountBadge',   checkbox: '.categoryCheckbox' }
        };
        var entry = map[type];
        if (!entry) return;
        var badge = view.querySelector(entry.badge);
        if (!badge) return;
        var total    = view.querySelectorAll(entry.checkbox).length;
        var selected = view.querySelectorAll(entry.checkbox + ':checked').length;
        if (total === 0) { badge.style.display = 'none'; return; }
        badge.querySelector('.countSelected').textContent = selected;
        badge.querySelector('.countTotal').textContent    = total;
        badge.style.display = '';
        badge.classList.toggle('zero-selected', selected === 0);
    }

    function renderHealthBar(view, config) {
        var xtreamItem      = view.querySelector('.healthItemXtream');
        var dispatcharrItem = view.querySelector('.healthItemDispatcharr');
        var syncItem        = view.querySelector('.healthItemLastSync');
        if (!xtreamItem) return;

        // Xtream dot
        var xtreamOk = !!(config.BaseUrl && config.Username);
        setHealthDot(xtreamItem, xtreamOk ? 'ok' : 'grey');
        xtreamItem.querySelector('.healthLabel').textContent = xtreamOk
            ? 'Xtream: Connected (' + config.Username + ')'
            : 'Xtream: Not configured';

        // Dispatcharr dot
        var dispatcharrOn = !!config.EnableDispatcharr;
        setHealthDot(dispatcharrItem, dispatcharrOn ? 'ok' : 'grey');
        dispatcharrItem.querySelector('.healthLabel').textContent = dispatcharrOn
            ? 'Dispatcharr: Active'
            : 'Dispatcharr: Disabled';

        // Last sync dot — prefer SyncHistoryJson[0].EndTime (updated on every sync),
        // fall back to LastMovieSyncTimestamp (may be Unix epoch int or ISO string).
        var syncLabel = 'Last Sync: Never';
        var syncOk = false;
        var syncDate = null;

        // Primary: SyncHistoryJson[0].EndTime (ISO string, always current)
        try {
            var hist = config.SyncHistoryJson ? JSON.parse(config.SyncHistoryJson) : null;
            if (hist && hist.length > 0) {
                var et = new Date(hist[0].EndTime);
                if (!isNaN(et.getTime())) syncDate = et;
            }
        } catch (e) { /* ignore parse errors */ }

        // Fallback: LastMovieSyncTimestamp (numeric Unix epoch seconds or ISO string)
        if (!syncDate) {
            var lastTs = config.LastMovieSyncTimestamp;
            if (lastTs && /^\d+$/.test(String(lastTs))) {
                var epoch = parseInt(lastTs, 10);
                if (epoch > 0) syncDate = new Date(epoch * 1000);
            } else if (lastTs && String(lastTs).indexOf('0001') !== 0) {
                var parsed = new Date(lastTs);
                if (!isNaN(parsed.getTime())) syncDate = parsed;
            }
        }

        if (syncDate) {
            syncOk = true;
            syncLabel = 'Last Sync: ' + formatTimeAgo(syncDate);
        }
        setHealthDot(syncItem, syncOk ? 'ok' : 'grey');
        syncItem.querySelector('.healthLabel').textContent = syncLabel;
    }

    function setHealthDot(itemEl, status) {
        var dot = itemEl.querySelector('.healthDot');
        if (!dot) return;
        var colours = { ok: '#52B54B', error: '#cc0000', grey: '#888' };
        dot.style.background = colours[status] || colours.grey;
    }

    function updateDashboardEmptyState(view, config) {
        var unconfigured = view.querySelector('.dashboardEmptyStateUnconfigured');
        var noCategories = view.querySelector('.dashboardEmptyStateNoCategories');
        var grid         = view.querySelector('.dashboard-grid');
        if (!unconfigured || !grid) return;

        var isConfigured = !!(config.BaseUrl && config.Username);
        var hasContent   = !!(config.SyncMovies || config.SyncSeries || config.EnableLiveTv);

        if (!isConfigured) {
            unconfigured.style.display = '';
            noCategories.style.display = 'none';
            grid.style.display = 'none';
        } else if (!hasContent) {
            unconfigured.style.display = 'none';
            noCategories.style.display = '';
            grid.style.display = '';
        } else {
            unconfigured.style.display = 'none';
            noCategories.style.display = 'none';
            grid.style.display = '';
        }
    }

    function renderAutoSyncDashboardLine(view, config) {
        var el = view.querySelector('.dashboardAutoSyncLine');
        if (!el) return;
        if (!config.AutoSyncEnabled) {
            el.innerHTML = 'Auto-sync: Off \u00a0\u2014\u00a0<a class="lnkGoToAutoSync" href="#" style="color:inherit; text-decoration:underline;">Enable in Settings</a>';
        } else {
            var interval = config.AutoSyncIntervalHours || 24;
            var nextText = '';
            var lastTs = config.LastMovieSyncTimestamp;
            if (lastTs) {
                var lastDate = typeof lastTs === 'number' ? new Date(lastTs * 1000) : new Date(lastTs);
                if (!isNaN(lastDate.getTime())) {
                    var diff = new Date(lastDate.getTime() + interval * 3600000) - new Date();
                    if (diff > 0) {
                        var h = Math.floor(diff / 3600000);
                        var m = Math.floor((diff % 3600000) / 60000);
                        nextText = ' \u2014 next run in ' + (h > 0 ? h + 'h ' : '') + m + 'm';
                    } else {
                        nextText = ' \u2014 overdue';
                    }
                }
            }
            el.textContent = 'Auto-sync: Every ' + interval + 'h' + nextText;
        }
        var link = el.querySelector('.lnkGoToAutoSync');
        if (link) {
            link.addEventListener('click', function (e) {
                e.preventDefault();
                switchTab(view, 'generic');
            });
        }
    }

    function updateSyncCTAEmphasis(view, hasHistory) {
        var btn  = view.querySelector('.btnDashboardSyncAll');
        var hint = view.querySelector('.syncNoCTAHint');
        if (!btn) return;
        if (!hasHistory) {
            btn.classList.add('block');
            if (hint) hint.style.display = '';
        } else {
            btn.classList.remove('block');
            if (hint) hint.style.display = 'none';
        }
    }

    function initFolderModeCards(view, type) {
        var containerClass = type === 'movie' ? '.movieFolderModeCards' : '.seriesFolderModeCards';
        var selectClass    = type === 'movie' ? '.selMovieFolderMode'   : '.selSeriesFolderMode';
        var container = view.querySelector(containerClass);
        var select    = view.querySelector(selectClass);
        if (!container || !select) return;

        var cards = container.querySelectorAll('.folder-mode-card');

        function activateCard(val) {
            for (var i = 0; i < cards.length; i++) {
                cards[i].classList.toggle('active', cards[i].getAttribute('data-mode') === val);
            }
        }

        for (var i = 0; i < cards.length; i++) {
            (function (card) {
                card.addEventListener('click', function () {
                    select.value = card.getAttribute('data-mode');
                    activateCard(select.value);
                    var evt;
                    if (typeof Event === 'function') {
                        evt = new Event('change', { bubbles: true });
                    } else {
                        evt = document.createEvent('Event');
                        evt.initEvent('change', true, true);
                    }
                    select.dispatchEvent(evt);
                });
            })(cards[i]);
        }

        activateCard(select.value);
    }

    function syncFolderModeCards(view, type) {
        var containerClass = type === 'movie' ? '.movieFolderModeCards' : '.seriesFolderModeCards';
        var selectClass    = type === 'movie' ? '.selMovieFolderMode'   : '.selSeriesFolderMode';
        var container = view.querySelector(containerClass);
        var select    = view.querySelector(selectClass);
        if (!container || !select) return;
        var val   = select.value;
        var cards = container.querySelectorAll('.folder-mode-card');
        for (var i = 0; i < cards.length; i++) {
            cards[i].classList.toggle('active', cards[i].getAttribute('data-mode') === val);
        }
    }

    return View;
});
