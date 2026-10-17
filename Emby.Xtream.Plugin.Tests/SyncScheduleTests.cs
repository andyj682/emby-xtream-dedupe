using System;
using System.Collections.Generic;
using Emby.Xtream.Plugin.Service;
using MediaBrowser.Model.Tasks;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// The Dashboard shows the schedule Emby holds for the sync tasks, not the plugin's Auto-Sync
    /// settings, so a schedule set in Emby's Scheduled Tasks is reported as it is.
    /// </summary>
    public class SyncScheduleTests
    {
        // UTC so the expectations do not depend on the test machine's time zone.
        private static readonly DateTime Now = new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void NoTriggers_IsOff()
        {
            var s = SyncSchedule.Describe(new List<SyncSchedule.TaskSchedule> { Task(), Task() }, Now);

            Assert.False(s.On);
            Assert.Equal("Off", s.Description);
            Assert.Null(s.NextRunUtc);
        }

        [Fact]
        public void SameDailyTrigger_IsDescribed_AndRunsNextAtThatTime()
        {
            var s = SyncSchedule.Describe(new List<SyncSchedule.TaskSchedule> { Task(Daily(3)), Task(Daily(3)) }, Now);

            Assert.True(s.On);
            Assert.Equal("Daily at 03:00", s.Description);
            Assert.Equal(new DateTime(2026, 10, 18, 3, 0, 0, DateTimeKind.Utc), s.NextRunUtc);
        }

        [Fact]
        public void DailyLaterToday_RunsToday()
        {
            var s = SyncSchedule.Describe(new List<SyncSchedule.TaskSchedule> { Task(Daily(22)) }, Now);

            Assert.Equal(new DateTime(2026, 10, 17, 22, 0, 0, DateTimeKind.Utc), s.NextRunUtc);
        }

        [Fact]
        public void Interval_RunsIntervalAfterTheLastRun()
        {
            var lastRun = new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);
            var s = SyncSchedule.Describe(new List<SyncSchedule.TaskSchedule> { Task(lastRun, Interval(6)) }, Now);

            Assert.Equal("Every 6h", s.Description);
            Assert.Equal(new DateTime(2026, 10, 17, 14, 0, 0, DateTimeKind.Utc), s.NextRunUtc);
        }

        [Fact]
        public void DifferentTriggersPerTask_AreACustomSchedule_WithTheEarliestNextRun()
        {
            // The old built-in defaults: movies at 03:00, series at 04:00.
            var s = SyncSchedule.Describe(new List<SyncSchedule.TaskSchedule> { Task(Daily(4)), Task(Daily(3)) }, Now);

            Assert.True(s.On);
            Assert.StartsWith("Custom schedule", s.Description);
            Assert.Equal(new DateTime(2026, 10, 18, 3, 0, 0, DateTimeKind.Utc), s.NextRunUtc);
        }

        [Fact]
        public void OnlyOneTaskScheduled_IsACustomSchedule()
        {
            var s = SyncSchedule.Describe(new List<SyncSchedule.TaskSchedule> { Task(Daily(3)), Task() }, Now);

            Assert.True(s.On);
            Assert.StartsWith("Custom schedule", s.Description);
        }

        private static SyncSchedule.TaskSchedule Task(params TaskTriggerInfo[] triggers) =>
            new SyncSchedule.TaskSchedule { Triggers = triggers };

        private static SyncSchedule.TaskSchedule Task(DateTimeOffset lastRun, params TaskTriggerInfo[] triggers) =>
            new SyncSchedule.TaskSchedule { Triggers = triggers, LastRunEndUtc = lastRun };

        private static TaskTriggerInfo Daily(int hour) =>
            new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(hour).Ticks };

        private static TaskTriggerInfo Interval(int hours) =>
            new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerInterval, IntervalTicks = TimeSpan.FromHours(hours).Ticks };
    }
}
