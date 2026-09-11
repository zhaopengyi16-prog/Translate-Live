using LiveCaptionsTranslator.services;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class LiveCaptionsConnectionPolicyTests
    {
        [TestMethod]
        public void AutomaticConnectionCanOnlyBeClaimedOnce()
        {
            var policy = new LiveCaptionsConnectionPolicy();

            Assert.IsTrue(policy.TryClaimAutomaticConnection());
            Assert.IsFalse(policy.TryClaimAutomaticConnection());
            Assert.IsFalse(policy.TryClaimAutomaticConnection());
        }

        [TestMethod]
        public void SeparateInstancesHaveIndependentStartupClaims()
        {
            var firstRun = new LiveCaptionsConnectionPolicy();
            var nextRun = new LiveCaptionsConnectionPolicy();

            Assert.IsTrue(firstRun.TryClaimAutomaticConnection());
            Assert.IsFalse(firstRun.TryClaimAutomaticConnection());
            Assert.IsTrue(nextRun.TryClaimAutomaticConnection());
        }

        [TestMethod]
        public void ConcurrentAutomaticClaimsHaveOneWinner()
        {
            var policy = new LiveCaptionsConnectionPolicy();

            bool[] results = Enumerable.Range(0, 32)
                .AsParallel()
                .Select(_ => policy.TryClaimAutomaticConnection())
                .ToArray();

            Assert.AreEqual(1, results.Count(result => result));
        }
    }
}
