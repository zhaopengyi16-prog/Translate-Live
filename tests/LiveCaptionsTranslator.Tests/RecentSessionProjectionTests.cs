using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.services;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class RecentSessionProjectionTests
    {
        [TestMethod]
        public void TakePreservesRepositoryOrderAndCapsTheSidebar()
        {
            var sessions = Enumerable.Range(1, 6)
                .Select(id => CreateSession(id))
                .ToArray();

            IReadOnlyList<LectureSessionEntry> result =
                RecentSessionProjection.Take(sessions);

            CollectionAssert.AreEqual(
                new long[] { 1, 2, 3, 4 },
                result.Select(session => session.Id).ToArray());
        }

        [TestMethod]
        public void RemoveDropsTheDeletedSessionImmediately()
        {
            IReadOnlyList<LectureSessionEntry> sessions =
                [CreateSession(5), CreateSession(4), CreateSession(3)];

            IReadOnlyList<LectureSessionEntry> result =
                RecentSessionProjection.Remove(sessions, 4);

            CollectionAssert.AreEqual(
                new long[] { 5, 3 },
                result.Select(session => session.Id).ToArray());
        }

        private static LectureSessionEntry CreateSession(long id)
        {
            return new LectureSessionEntry
            {
                Id = id,
                StartedAt = DateTimeOffset.UnixEpoch.AddMinutes(id),
                Mode = $"Session {id}"
            };
        }
    }
}
