using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Xtream.Plugin.Service
{
    public class SyncSeriesTask : IScheduledTask
    {
        private readonly ILogger _logger;

        public SyncSeriesTask(ILogManager logManager)
            => _logger = logManager.GetLogger("XtreamTuner.SyncSeriesTask");

        public string Name        => "Xtream Tuner \u2013 Sync Series";
        public string Description => "Sync series/TV show STRM files from the Xtream server.";
        public string Category    => "Xtream Tuner";
        public string Key         => "XtreamTunerSyncSeries";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // No schedule until the user turns Auto-Sync on, which is what the config page
            // shows by default. A daily default here ran syncs while the page said "off".
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var config = Plugin.Instance.Configuration;
            if (!config.SyncSeries)
            {
                _logger.Info("Series sync disabled — skipping.");
                return;
            }
            var svc = Plugin.Instance.StrmSyncService;
            if (svc.SeriesProgress.IsRunning)
            {
                _logger.Info("Series sync already running — skipping scheduled run.");
                return;
            }
            progress.Report(0);
            var ran = await svc.SyncSeriesAsync(
                config,
                cancellationToken,
                () => Plugin.Instance.SaveConfiguration(),
                progress).ConfigureAwait(false);
            if (!ran)
            {
                // The IsRunning check above is a fast path; the service holds the real gate.
                _logger.Info("Series sync already running — skipping scheduled run.");
                return;
            }
            progress.Report(100);
        }
    }
}
