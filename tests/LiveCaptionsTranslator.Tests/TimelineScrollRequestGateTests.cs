using LiveCaptionsTranslator.services;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class TimelineScrollRequestGateTests
    {
        [TestMethod]
        public void UserNavigationInvalidatesAnAlreadyQueuedFollowRequest()
        {
            var gate = new TimelineScrollRequestGate();
            long request = gate.ScheduleFollow(isFollowingLive: true);

            gate.Invalidate();

            Assert.IsFalse(gate.CanExecute(request, isFollowingLive: false));
            Assert.IsFalse(gate.CanExecute(request, isFollowingLive: true));
        }

        [TestMethod]
        public void NewerFollowRequestSupersedesTheOlderRequest()
        {
            var gate = new TimelineScrollRequestGate();
            long first = gate.ScheduleFollow(isFollowingLive: true);
            long second = gate.ScheduleFollow(isFollowingLive: true);

            Assert.IsFalse(gate.CanExecute(first, isFollowingLive: true));
            Assert.IsTrue(gate.CanExecute(second, isFollowingLive: true));
        }

        [TestMethod]
        public void AnchorCompensationPreservesViewportOffset()
        {
            double offset = TimelineScrollRequestGate.CompensateOffset(
                currentOffset: 320,
                expectedAnchorTop: 24,
                actualAnchorTop: 76);

            Assert.AreEqual(372, offset);
        }
    }
}
