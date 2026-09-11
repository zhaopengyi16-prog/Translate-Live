using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class CaptionRevisionPolicyTests
    {
        [TestMethod]
        public void GrowingDraftIsARevision()
        {
            Assert.IsTrue(CaptionRevisionPolicy.IsRevision(
                "The speaker explains",
                "The speaker explains the architecture"));
        }

        [TestMethod]
        public void ShiftedSuffixCanBeReconciledWithoutDuplicatingWords()
        {
            const string previous =
                "The speaker explains why the translation queue keeps its order";
            const string current =
                "translation queue keeps its order even when responses arrive late.";

            Assert.IsTrue(CaptionRevisionPolicy.TryReconcile(
                previous,
                current,
                out string reconciled));
            Assert.AreEqual(
                "The speaker explains why the translation queue keeps its order even when responses arrive late.",
                reconciled);
        }

        [TestMethod]
        public void CompletedSentenceFollowedByDraftStartsANewUtterance()
        {
            Assert.IsFalse(CaptionRevisionPolicy.IsRevision(
                "This sentence is complete.",
                "This sentence begins a new point"));
        }

        [TestMethod]
        public void DistinctFinalSentencesStayDistinct()
        {
            Assert.IsFalse(CaptionRevisionPolicy.IsRevision(
                "The first result is stable.",
                "The second result is accurate."));
        }

        [TestMethod]
        public void GrowingPunctuatedRecognitionIsOneFinalRevision()
        {
            Assert.IsTrue(CaptionRevisionPolicy.IsGrowingFinalRevision(
                "If.",
                "If your safety is threatened."));
            Assert.IsTrue(CaptionRevisionPolicy.IsRevision(
                "If.",
                "If your safety is threatened."));
        }
    }
}
