using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Copies the plugin configuration into the records root on a schedule (ADR-F005
    /// mechanism 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A scheduled task rather than something the sync does, deliberately. A user whose sync is
    /// disabled, failing, or simply never scheduled still needs backups — and hanging this off
    /// the sync would rebuild, inside the plugin, the very property ADR-F005 exists to remove:
    /// protection that only reaches people who already set something up.
    /// </para>
    /// <para>
    /// Being a task also gives the on-demand run for free. Emby's Scheduled Tasks page can run
    /// any task immediately, so a backup can be forced before something risky without adding a
    /// button anywhere.
    /// </para>
    /// </remarks>
    public class BackupConfigurationTask : IScheduledTask
    {
        private readonly ILogger _logger;

        public BackupConfigurationTask(ILogManager logManager)
            => _logger = logManager.GetLogger("XtreamTuner.BackupConfigurationTask");

        public string Name        => "Xtream Tuner – Back Up Configuration";
        public string Description =>
            "Copy the plugin's exclusions, reviewed marks and settings into the backup folder. "
            + "These decisions cannot be rebuilt by a library scan.";
        public string Category    => "Xtream Tuner";
        public string Key         => "XtreamTunerBackupConfiguration";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Daily, an hour before the default sync trigger. Order matters slightly: a backup
            // taken before the sync captures the state the user last approved, rather than
            // whatever the sync has just written.
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
            };
        }

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            progress.Report(0);

            var config = Plugin.Instance.Configuration;
            if (config.ConfigBackupCount <= 0)
            {
                _logger.Info("Configuration backups are disabled (retention is 0) — skipping.");
                progress.Report(100);
                return Task.FromResult(0);
            }

            // Returns null when the configuration is unchanged since the last copy, which is the
            // ordinary case on a daily timer and is not worth a log line at Info.
            var written = Plugin.Instance.StrmSyncService.BackupConfiguration(config);
            if (written == null)
            {
                _logger.Debug("No configuration backup written (unchanged, or it could not be taken).");
            }

            progress.Report(100);
            return Task.FromResult(0);
        }
    }
}
