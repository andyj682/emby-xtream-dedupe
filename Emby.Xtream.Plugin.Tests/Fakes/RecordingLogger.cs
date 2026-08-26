using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MediaBrowser.Model.Logging;

namespace Emby.Xtream.Plugin.Tests.Fakes
{
    /// <summary>
    /// Captures log output so tests can assert on the diagnostics the sync emits.
    ///
    /// Messages are stored formatted rather than as a template plus arguments, so an
    /// assertion matches the text a user reading the log would actually see — a
    /// diagnostic whose format string is wrong should fail the test, not pass it.
    /// </summary>
    public sealed class RecordingLogger : ILogger
    {
        private readonly object _sync = new object();

        public List<string> Warnings { get; } = new List<string>();

        public List<string> Infos { get; } = new List<string>();

        private static string Format(string message, object[] paramList)
        {
            if (paramList == null || paramList.Length == 0)
            {
                return message ?? string.Empty;
            }

            try
            {
                return string.Format(CultureInfo.InvariantCulture, message ?? string.Empty, paramList);
            }
            catch (FormatException)
            {
                // A malformed template is itself worth surfacing, but throwing here would
                // fail the sync rather than the assertion. Keep the raw template.
                return message ?? string.Empty;
            }
        }

        private void Record(List<string> sink, string message, object[] paramList)
        {
            // The sync logs from parallel tasks; List<T> is not thread-safe.
            lock (_sync)
            {
                sink.Add(Format(message, paramList));
            }
        }

        public void Info(string message, params object[] paramList) => Record(Infos, message, paramList);

        public void Warn(string message, params object[] paramList) => Record(Warnings, message, paramList);

        public void Error(string message, params object[] paramList) { }

        public void Debug(string message, params object[] paramList) { }

        public void Fatal(string message, params object[] paramList) { }

        public void FatalException(string message, Exception exception, params object[] paramList) { }

        public void ErrorException(string message, Exception exception, params object[] paramList) { }

        public void LogMultiline(string message, LogSeverity severity, StringBuilder additionalContent) { }

        public void Log(LogSeverity severity, string message, params object[] paramList) { }

        public void Info(ReadOnlyMemory<char> message) { }

        public void Error(ReadOnlyMemory<char> message) { }

        public void Warn(ReadOnlyMemory<char> message) { }

        public void Debug(ReadOnlyMemory<char> message) { }

        public void Log(LogSeverity severity, ReadOnlyMemory<char> message) { }
    }
}
