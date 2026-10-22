using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Model.Tasks;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Describes the sync schedule from the triggers Emby actually holds for the plugin's sync
    /// tasks, so the Dashboard says what will run. The plugin's own Auto-Sync settings only write
    /// those triggers; a schedule set in Emby's Scheduled Tasks is kept and must be shown too.
    /// </summary>
    internal static class SyncSchedule
    {
        internal sealed class TaskSchedule
        {
            public TaskTriggerInfo[] Triggers;
            public DateTimeOffset? LastRunEndUtc;
        }

        internal sealed class Summary
        {
            public bool On;
            public string Description;
            public DateTime? NextRunUtc;
        }

        /// <param name="nowLocal">Server local time; daily and weekly triggers are in server local time.</param>
        internal static Summary Describe(IList<TaskSchedule> tasks, DateTime nowLocal)
        {
            var all = tasks.SelectMany(t => t.Triggers ?? new TaskTriggerInfo[0]).ToList();
            if (all.Count == 0)
                return new Summary { On = false, Description = "Off" };

            DateTime? next = null;
            foreach (var task in tasks)
            {
                foreach (var trigger in task.Triggers ?? new TaskTriggerInfo[0])
                {
                    var candidate = NextRunUtc(trigger, task.LastRunEndUtc, nowLocal);
                    if (candidate.HasValue && (!next.HasValue || candidate.Value < next.Value))
                        next = candidate;
                }
            }

            return new Summary { On = true, Description = Describe(tasks), NextRunUtc = next };
        }

        private static string Describe(IList<TaskSchedule> tasks)
        {
            // One wording only when every task has the same single trigger, which is what the
            // plugin's Auto-Sync settings write. Anything else was set in Emby.
            var keys = tasks.Select(t => t.Triggers ?? new TaskTriggerInfo[0]).ToList();
            if (keys.All(k => k.Length == 1))
            {
                var first = keys[0][0];
                if (keys.All(k => SameTrigger(k[0], first)))
                {
                    var text = DescribeTrigger(first);
                    if (text != null) return text;
                }
            }

            return "Custom schedule (Emby's Scheduled Tasks)";
        }

        private static bool SameTrigger(TaskTriggerInfo a, TaskTriggerInfo b)
        {
            return string.Equals(a.Type, b.Type, StringComparison.Ordinal)
                && a.TimeOfDayTicks == b.TimeOfDayTicks
                && a.IntervalTicks == b.IntervalTicks
                && a.DayOfWeek == b.DayOfWeek;
        }

        private static string DescribeTrigger(TaskTriggerInfo t)
        {
            if (t.Type == TaskTriggerInfo.TriggerDaily && t.TimeOfDayTicks.HasValue)
                return "Daily at " + Clock(t.TimeOfDayTicks.Value);
            if (t.Type == TaskTriggerInfo.TriggerInterval && t.IntervalTicks.HasValue)
            {
                var hours = TimeSpan.FromTicks(t.IntervalTicks.Value).TotalHours;
                return "Every " + hours.ToString("0.##", CultureInfo.InvariantCulture) + "h";
            }
            if (t.Type == TaskTriggerInfo.TriggerWeekly && t.TimeOfDayTicks.HasValue && t.DayOfWeek.HasValue)
                return "Every " + t.DayOfWeek.Value + " at " + Clock(t.TimeOfDayTicks.Value);
            return null;
        }

        private static string Clock(long ticks)
        {
            var ts = TimeSpan.FromTicks(ticks);
            return ts.Hours.ToString("00", CultureInfo.InvariantCulture) + ":" + ts.Minutes.ToString("00", CultureInfo.InvariantCulture);
        }

        private static DateTime? NextRunUtc(TaskTriggerInfo t, DateTimeOffset? lastRunEndUtc, DateTime nowLocal)
        {
            if (t.Type == TaskTriggerInfo.TriggerDaily && t.TimeOfDayTicks.HasValue)
            {
                var next = nowLocal.Date + TimeSpan.FromTicks(t.TimeOfDayTicks.Value);
                if (next <= nowLocal) next = next.AddDays(1);
                return ToUtc(next);
            }

            if (t.Type == TaskTriggerInfo.TriggerWeekly && t.TimeOfDayTicks.HasValue && t.DayOfWeek.HasValue)
            {
                var days = ((int)t.DayOfWeek.Value - (int)nowLocal.DayOfWeek + 7) % 7;
                var next = nowLocal.Date.AddDays(days) + TimeSpan.FromTicks(t.TimeOfDayTicks.Value);
                if (next <= nowLocal) next = next.AddDays(7);
                return ToUtc(next);
            }

            if (t.Type == TaskTriggerInfo.TriggerInterval && t.IntervalTicks.HasValue && t.IntervalTicks.Value > 0)
            {
                var nowUtc = ToUtc(nowLocal);
                if (!lastRunEndUtc.HasValue) return null;
                var next = lastRunEndUtc.Value.UtcDateTime.AddTicks(t.IntervalTicks.Value);
                return next < nowUtc ? nowUtc : next;
            }

            // Startup and system-event triggers have no time.
            return null;
        }

        private static DateTime ToUtc(DateTime local)
        {
            return local.Kind == DateTimeKind.Utc ? local : DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
        }
    }
}
