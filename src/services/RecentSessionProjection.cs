using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.services
{
    internal static class RecentSessionProjection
    {
        internal static IReadOnlyList<LectureSessionEntry> Take(
            IEnumerable<LectureSessionEntry>? sessions,
            int maxCount = 4)
        {
            if (sessions == null || maxCount <= 0)
                return [];

            return sessions.Take(maxCount).ToArray();
        }

        internal static IReadOnlyList<LectureSessionEntry> Remove(
            IReadOnlyList<LectureSessionEntry>? sessions,
            long sessionId)
        {
            if (sessions == null || sessions.Count == 0)
                return [];

            return sessions
                .Where(session => session.Id != sessionId)
                .ToArray();
        }
    }
}
