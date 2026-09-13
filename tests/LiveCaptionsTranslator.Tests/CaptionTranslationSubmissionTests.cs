using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class CaptionTranslationSubmissionTests
    {
        [TestMethod]
        public void SubmissionRetainsTheSessionCapturedWithTheCaption()
        {
            long? currentSession = 41;
            var identity = new TranslationTaskIdentity(
                Guid.NewGuid(),
                1,
                0,
                true,
                DateTimeOffset.UtcNow);
            var submission = new CaptionTranslationSubmission(
                identity,
                "Captured before the classroom changed.",
                currentSession);

            currentSession = 42;

            Assert.AreEqual(41L, submission.SessionId);
            Assert.AreEqual(identity, submission.Identity);
        }
    }
}
