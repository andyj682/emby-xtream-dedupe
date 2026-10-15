using System;
using System.IO;
using System.Linq;
using Emby.Xtream.Plugin.Service;
using Emby.Xtream.Plugin.Tests.Fakes;
using Xunit;

namespace Emby.Xtream.Plugin.Tests
{
    /// <summary>
    /// The sanitized log download came back empty for a user whose plugin was clearly logging
    /// (#86). The newest five files of any kind were read, and Emby writes an ffmpeg log per
    /// stream into the same folder.
    /// </summary>
    public class PluginLogCollectorTests
    {
        [Fact]
        public void ServerLog_IsRead_EvenWhenNewerFfmpegLogsOutnumberIt()
        {
            using (var dir = new TempDirectory())
            {
                Write(dir, "embyserver.txt", "Info XtreamTuner: refreshing channels", ageMinutes: 60);
                for (var i = 0; i < 8; i++)
                    Write(dir, $"ffmpeg-directstream-{i}.txt", "ffmpeg output", ageMinutes: i);

                var lines = PluginLogCollector.Collect(dir.Path);

                Assert.Contains("Info XtreamTuner: refreshing channels", lines);
            }
        }

        [Fact]
        public void Selection_PutsServerLogsFirst_ThenOtherLogs_ThenFfmpegLogs()
        {
            using (var dir = new TempDirectory())
            {
                Write(dir, "ffmpeg-transcode-1.txt", "x", ageMinutes: 1);
                Write(dir, "hardware_detection-1.txt", "x", ageMinutes: 2);
                Write(dir, "embyserver-1.txt", "x", ageMinutes: 30);
                Write(dir, "embyserver.txt", "x", ageMinutes: 3);

                var names = PluginLogCollector.SelectLogFiles(dir.Path).Select(Path.GetFileName).ToList();

                Assert.Equal(
                    new[] { "embyserver.txt", "embyserver-1.txt", "hardware_detection-1.txt", "ffmpeg-transcode-1.txt" },
                    names);
            }
        }

        [Fact]
        public void Selection_IsCappedAtFiveFiles()
        {
            using (var dir = new TempDirectory())
            {
                for (var i = 0; i < 8; i++)
                    Write(dir, $"embyserver-{i}.txt", "x", ageMinutes: i);

                Assert.Equal(PluginLogCollector.MaxFiles, PluginLogCollector.SelectLogFiles(dir.Path).Count);
            }
        }

        [Fact]
        public void OnlyMatchingLines_AreCollected()
        {
            using (var dir = new TempDirectory())
            {
                Write(dir, "embyserver.txt",
                    "Info App: unrelated\nInfo Dispatcharr: token ok\nInfo LiveTv: guide refresh\nInfo Other: nope",
                    ageMinutes: 1);

                var lines = PluginLogCollector.Collect(dir.Path);

                Assert.Equal(new[] { "Info Dispatcharr: token ok", "Info LiveTv: guide refresh" }, lines);
            }
        }

        private static void Write(TempDirectory dir, string name, string content, int ageMinutes)
        {
            var path = Path.Combine(dir.Path, name);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-ageMinutes));
        }
    }
}
