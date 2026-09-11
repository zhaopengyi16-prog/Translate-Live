using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.services
{
    internal static class HistorySelectionPolicy
    {
        public static long? ChooseAfterDeletion(
            IEnumerable<LectureSessionEntry>? currentSessions,
            long deletedSessionId)
        {
            var sessions = currentSessions?.ToList() ?? [];
            int deletedIndex = sessions.FindIndex(item => item.Id == deletedSessionId);
            if (deletedIndex < 0)
                return sessions.FirstOrDefault()?.Id;

            sessions.RemoveAt(deletedIndex);
            if (sessions.Count == 0)
                return null;

            int nextIndex = Math.Min(deletedIndex, sessions.Count - 1);
            return sessions[nextIndex].Id;
        }
    }
}
