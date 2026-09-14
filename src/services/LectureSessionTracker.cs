using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.services
{
    internal enum EmptySessionAbortResult
    {
        NotCurrent,
        Deleted,
        PreservedWithRecords,
        EndedAfterDeleteFailure
    }

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
                long existingId = Interlocked.Read(ref currentSessionId);
                if (existingId > 0)
                {
                    await SQLiteHistoryLogger.EndSessionAsync(existingId, null, token);
                    Interlocked.Exchange(ref currentSessionId, 0);
                }

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
                long id = Interlocked.Read(ref currentSessionId);
                if (id > 0)
                {
                    await SQLiteHistoryLogger.EndSessionAsync(id, summaryText, token);
                    Interlocked.Exchange(ref currentSessionId, 0);
                }
            }
            finally
            {
                sessionGate.Release();
            }
        }

        internal static async Task<EmptySessionAbortResult> AbortCurrentIfEmptyAsync(
            long expectedSessionId,
            CancellationToken token = default)
        {
            await sessionGate.WaitAsync(token);
            try
            {
                long currentId = Interlocked.Read(ref currentSessionId);
                if (currentId != expectedSessionId)
                    return EmptySessionAbortResult.NotCurrent;

                var history = await SQLiteHistoryLogger.LoadSessionHistoryAsync(
                    expectedSessionId,
                    token: token);
                if (history.Count > 0)
                {
                    await SQLiteHistoryLogger.EndSessionAsync(
                        expectedSessionId,
                        null,
                        token);
                    Interlocked.Exchange(ref currentSessionId, 0);
                    return EmptySessionAbortResult.PreservedWithRecords;
                }

                bool deleted = await SQLiteHistoryLogger.DeleteSessionAsync(
                    expectedSessionId,
                    token);
                if (deleted)
                {
                    Interlocked.Exchange(ref currentSessionId, 0);
                    return EmptySessionAbortResult.Deleted;
                }

                await SQLiteHistoryLogger.EndSessionAsync(
                    expectedSessionId,
                    null,
                    token);
                Interlocked.Exchange(ref currentSessionId, 0);
                return EmptySessionAbortResult.EndedAfterDeleteFailure;
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
