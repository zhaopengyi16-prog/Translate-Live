using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.services
{
    public static class LectureSessionTracker
    {
        private static readonly SemaphoreSlim sessionGate = new(1, 1);
        private static long currentSessionId;

        public static long? CurrentSessionId
        {
            get
            {
                long id = Interlocked.Read(ref currentSessionId);
                return id > 0 ? id : null;
            }
        }

        public static async Task<LectureSessionEntry> BeginAsync(
            string mode,
            string apiUsed,
            string targetLanguage,
            CancellationToken token = default)
        {
            await sessionGate.WaitAsync(token);
            try
            {
                long existingId = Interlocked.Exchange(ref currentSessionId, 0);
                if (existingId > 0)
                    await SQLiteHistoryLogger.EndSessionAsync(existingId, null, token);

                var session = await SQLiteHistoryLogger.BeginSessionAsync(
                    mode, apiUsed, targetLanguage, token);
                Interlocked.Exchange(ref currentSessionId, session.Id);
                return session;
            }
            finally
            {
                sessionGate.Release();
            }
        }

        public static async Task EndCurrentAsync(
            string? summaryText = null,
            CancellationToken token = default)
        {
            await sessionGate.WaitAsync(token);
            try
            {
                long id = Interlocked.Exchange(ref currentSessionId, 0);
                if (id > 0)
                    await SQLiteHistoryLogger.EndSessionAsync(id, summaryText, token);
            }
            finally
            {
                sessionGate.Release();
            }
        }

        public static Task SaveSummaryAsync(string summaryText, CancellationToken token = default)
        {
            long? id = CurrentSessionId;
            return id.HasValue
                ? SQLiteHistoryLogger.SaveSessionSummaryAsync(id.Value, summaryText, token)
                : Task.CompletedTask;
        }
    }
}
