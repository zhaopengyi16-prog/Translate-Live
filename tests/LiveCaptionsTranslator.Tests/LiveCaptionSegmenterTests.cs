using LiveCaptionsTranslator.services;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class LiveCaptionSegmenterTests
    {
        [TestMethod]
        public void VisualLineWrappingDoesNotInventPunctuation()
        {
            Assert.AreEqual(
                "Today we learn state machines.",
                TextUtil.NormalizeCaptionWhitespace("Today we learn\r\nstate machines."));
            Assert.AreEqual(
                "今天学习状态机。",
                TextUtil.NormalizeCaptionWhitespace("今天学习\n状态机。"));
        }

        [TestMethod]
        public void ShortFinalSentenceIsNotJoinedToPreviousSentence()
        {
            var segmenter = new LiveCaptionSegmenter();

            LiveCaptionUpdate update = segmenter.Process("We reviewed the plan. So.");

            CollectionAssert.AreEqual(
                new[] { "We reviewed the plan.", "So." },
                update.FinalizedSentences.ToArray());
        }

        [TestMethod]
        public void DecimalAndInitialismArePreservedInsideOneSentence()
        {
            var segmenter = new LiveCaptionSegmenter();

            LiveCaptionUpdate update = segmenter.Process(
                "The U.S. example uses version 3.14 today.");

            CollectionAssert.AreEqual(
                new[] { "The U.S. example uses version 3.14 today." },
                update.FinalizedSentences.ToArray());
        }

        [TestMethod]
        public void ShiftedRollingWindowCompletesTheExistingDraft()
        {
            var segmenter = new LiveCaptionSegmenter();
            segmenter.Process("The timeline stays ordered even when results");

            LiveCaptionUpdate update = segmenter.Process(
                "timeline stays ordered even when results arrive out of order.");

            CollectionAssert.AreEqual(
                new[] { "The timeline stays ordered even when results arrive out of order." },
                update.FinalizedSentences.ToArray());
            Assert.AreEqual(string.Empty, update.DraftText);
        }

        [TestMethod]
        public void RepeatedAccessibilityFrameDoesNotEmitDuplicateFinals()
        {
            var segmenter = new LiveCaptionSegmenter();
            segmenter.Process("First sentence.");

            LiveCaptionUpdate repeated = segmenter.Process("First sentence.");

            Assert.IsEmpty(repeated.FinalizedSentences);
        }

        [TestMethod]
        public void ResetSeedsTheExistingWindowWithoutReplayingOldText()
        {
            var segmenter = new LiveCaptionSegmenter();
            segmenter.Process("Old sentence.");
            segmenter.Reset();

            LiveCaptionUpdate seeded = segmenter.Process("Old sentence.");
            LiveCaptionUpdate next = segmenter.Process("Old sentence. New sentence.");

            Assert.IsEmpty(seeded.FinalizedSentences);
            Assert.IsFalse(seeded.DraftIsEligible);
            CollectionAssert.AreEqual(
                new[] { "New sentence." },
                next.FinalizedSentences.ToArray());
        }

        [TestMethod]
        public void SeededDraftBecomesEligibleOnlyAfterRecognitionChanges()
        {
            var segmenter = new LiveCaptionSegmenter();
            segmenter.Reset();

            LiveCaptionUpdate seeded = segmenter.Process("Existing unfinished text");
            LiveCaptionUpdate repeated = segmenter.Process("Existing unfinished text");
            LiveCaptionUpdate revised = segmenter.Process("Existing unfinished text continues");

            Assert.IsFalse(seeded.DraftIsEligible);
            Assert.IsFalse(repeated.DraftIsEligible);
            Assert.IsTrue(revised.DraftIsEligible);
        }

        [TestMethod]
        public void EmptyConnectionSnapshotDoesNotSuppressTheFirstSpokenSentence()
        {
            var segmenter = new LiveCaptionSegmenter();
            segmenter.StartFromCurrentSnapshot(string.Empty);

            LiveCaptionUpdate firstSpeech = segmenter.Process("First live sentence.");

            CollectionAssert.AreEqual(
                new[] { "First live sentence." },
                firstSpeech.FinalizedSentences.ToArray());
        }

        [TestMethod]
        public void ExistingConnectionSnapshotIsNotReplayed()
        {
            var segmenter = new LiveCaptionSegmenter();
            segmenter.StartFromCurrentSnapshot("Old visible sentence.");

            LiveCaptionUpdate repeated = segmenter.Process("Old visible sentence.");
            LiveCaptionUpdate next = segmenter.Process(
                "Old visible sentence. New live sentence.");

            Assert.IsEmpty(repeated.FinalizedSentences);
            CollectionAssert.AreEqual(
                new[] { "New live sentence." },
                next.FinalizedSentences.ToArray());
        }

        [TestMethod]
        public void RollingWindowEmitsOnlyTheNewCompletedSentence()
        {
            var segmenter = new LiveCaptionSegmenter();
            segmenter.Process("First sentence.");

            LiveCaptionUpdate update = segmenter.Process("First sentence. Second sentence.");

            CollectionAssert.AreEqual(
                new[] { "Second sentence." },
                update.FinalizedSentences.ToArray());
        }

        [TestMethod]
        public void TruncatedCompletedPrefixIsNotSavedAsADuplicateSentence()
        {
            var segmenter = new LiveCaptionSegmenter();
            segmenter.Process("The API returns exactly one stable result.");

            LiveCaptionUpdate update = segmenter.Process(
                "one stable result. The next topic begins.");

            CollectionAssert.AreEqual(
                new[] { "The next topic begins." },
                update.FinalizedSentences.ToArray());
        }

        [TestMethod]
        public void MostRecentFinalCorrectionIsEmittedAsOneRevision()
        {
            var segmenter = new LiveCaptionSegmenter();
            segmenter.Process("The model is fast.");

            LiveCaptionUpdate update = segmenter.Process("The model is faster.");

            CollectionAssert.AreEqual(
                new[] { "The model is faster." },
                update.FinalizedSentences.ToArray());
        }

        [TestMethod]
        public void FinalCorrectionBeforeANewSentenceIsNotLost()
        {
            var segmenter = new LiveCaptionSegmenter();
            segmenter.Process("First sentence. The model is fast.");

            LiveCaptionUpdate update = segmenter.Process(
                "The model is faster. A new sentence follows.");

            CollectionAssert.AreEqual(
                new[] { "The model is faster.", "A new sentence follows." },
                update.FinalizedSentences.ToArray());
        }

        [TestMethod]
        public void DistinctCompletedSentencesAreNotCollapsed()
        {
            var segmenter = new LiveCaptionSegmenter();
            segmenter.Process("The model is fast.");

            LiveCaptionUpdate update = segmenter.Process("The model is accurate.");

            CollectionAssert.AreEqual(
                new[] { "The model is accurate." },
                update.FinalizedSentences.ToArray());
        }
    }
}
