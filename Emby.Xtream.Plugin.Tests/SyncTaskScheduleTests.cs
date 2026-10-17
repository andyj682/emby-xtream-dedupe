using System;
using System.Reflection;
using Emby.Xtream.Plugin.Service;
using Emby.Xtream.Plugin.Tests.Fakes;
using MediaBrowser.Model.Logging;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// The config page shows Auto-Sync off by default, so the sync tasks must not come with a
    /// schedule of their own. A daily default ran syncs while the page said "off".
    /// </summary>
    public class SyncTaskScheduleTests
    {
        [Fact]
        public void SyncTasks_HaveNoDefaultSchedule()
        {
            var logs = LogManager();

            Assert.Empty(new SyncMoviesTask(logs).GetDefaultTriggers());
            Assert.Empty(new SyncSeriesTask(logs).GetDefaultTriggers());
        }

        private static ILogManager LogManager()
        {
            var proxy = DispatchProxy.Create<ILogManager, LogManagerProxy>();
            return proxy;
        }

        public class LogManagerProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.ReturnType == typeof(ILogger)) return new RecordingLogger();
                return targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void)
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
            }
        }
    }
}
