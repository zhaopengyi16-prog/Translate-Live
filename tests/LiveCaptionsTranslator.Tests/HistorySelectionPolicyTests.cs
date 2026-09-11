using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.services;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class HistorySelectionPolicyTests
    {
        [TestMethod]
        public void DeletingMiddleSessionSelectsTheFollowingSession()
        {
            var sessions = CreateSessions(3, 2, 1);

            long? selected = HistorySelectionPolicy.ChooseAfterDeletion(sessions, 2);

            Assert.AreEqual(1L, selected);
        }

        [TestMethod]
        public void DeletingLastVisibleSessionSelectsItsPreviousNeighbor()
        {
            var sessions = CreateSessions(3, 2, 1);

            long? selected = HistorySelectionPolicy.ChooseAfterDeletion(sessions, 1);

            Assert.AreEqual(2L, selected);
        }

        [TestMethod]
        public void DeletingOnlySessionClearsTheSelection()
        {
            long? selected = HistorySelectionPolicy.ChooseAfterDeletion(
                CreateSessions(7),
                7);

            Assert.IsNull(selected);
        }

        private static List<LectureSessionEntry> CreateSessions(params long[] ids)
        {
            return ids.Select(id => new LectureSessionEntry
            {
                Id = id,
                Mode = $"Session {id}"
            }).ToList();
        }
    }
}
