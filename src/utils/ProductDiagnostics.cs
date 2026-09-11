using System.IO;
using System.Text.Json;

namespace LiveCaptionsTranslator.utils
{
    public static class ProductDiagnostics
    {
        private static readonly object logLock = new();

        public static void Write(string eventName, Exception? exception = null)
        {
            WriteCore(eventName, exception, null);
        }

        public static void WriteDuration(string eventName, long durationMilliseconds)
        {
            WriteCore(eventName, null, Math.Max(0, durationMilliseconds));
        }

        private static void WriteCore(
            string eventName,
            Exception? exception,
            long? durationMilliseconds)
        {
            try
            {
                AppPaths.EnsureCreated();
                string logPath = Path.Combine(
                    AppPaths.Current.LogsDirectory,
                    $"app-{DateTime.UtcNow:yyyyMMdd}.jsonl");
                string line = JsonSerializer.Serialize(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    eventName,
                    exceptionType = exception?.GetType().FullName,
                    hResult = exception?.HResult,
                    durationMs = durationMilliseconds
                });

                lock (logLock)
                    File.AppendAllText(logPath, line + Environment.NewLine);
            }
            catch
            {
            }
        }
    }

    public static class RuntimeJournal
    {
        private const string MARKER_NAME = "running.marker";

        public static bool PreviousRunWasInterrupted { get; private set; }

        public static void Begin()
        {
            AppPaths.EnsureCreated();
            string markerPath = Path.Combine(AppPaths.Current.RecoveryDirectory, MARKER_NAME);
            PreviousRunWasInterrupted = File.Exists(markerPath);
            File.WriteAllText(markerPath, DateTimeOffset.UtcNow.ToString("O"));
        }

        public static void Complete()
        {
            string markerPath = Path.Combine(AppPaths.Current.RecoveryDirectory, MARKER_NAME);
            if (File.Exists(markerPath))
                File.Delete(markerPath);
        }
    }
}
