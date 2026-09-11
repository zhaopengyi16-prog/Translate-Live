using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.viewmodels;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class TranscriptSessionViewModelTests
    {
        [TestMethod]
        public void AThousandSegmentsRemainOrdered()
        {
            var viewModel = new TranscriptSessionViewModel();

            for (int sequence = 1000; sequence >= 1; sequence--)
                viewModel.ApplySegment(Create(sequence));

            Assert.HasCount(1000, viewModel.Segments);
            CollectionAssert.AreEqual(
                Enumerable.Range(1, 1000).Select(value => (long)value).ToArray(),
                viewModel.Segments.Select(segment => segment.Sequence).ToArray());
        }

        [TestMethod]
        public void OlderRevisionCannotOverwriteCurrentText()
        {
            var viewModel = new TranscriptSessionViewModel();
            var id = Guid.NewGuid();
            viewModel.ApplySegment(Create(1, id, revision: 2, source: "current"));
            viewModel.ApplySegment(Create(1, id, revision: 1, source: "stale"));

            Assert.AreEqual("current", viewModel.Segments[0].SourceText);
            Assert.AreEqual(2, viewModel.Segments[0].Revision);
        }

        [TestMethod]
        public void ReplacedHistoryEntryUpdatesTheExistingCard()
        {
            var viewModel = new TranscriptSessionViewModel();
            var originalId = Guid.NewGuid();
            viewModel.ApplySegment(Create(7, originalId, source: "partial sentence"));
            viewModel.ApplySegment(Create(9, source: "next sentence"));
            viewModel.BeginBrowsingHistory();

            bool replaced = viewModel.ReplaceSegmentBySequence(
                7,
                Create(10, source: "complete sentence"));

            Assert.IsTrue(replaced);
            Assert.HasCount(2, viewModel.Segments);
            CollectionAssert.AreEqual(
                new long[] { 9, 10 },
                viewModel.Segments.Select(segment => segment.Sequence).ToArray());
            Assert.AreEqual(originalId, viewModel.Segments[1].Id);
            Assert.AreEqual("complete sentence", viewModel.Segments[1].SourceText);
            Assert.AreEqual(0, viewModel.PendingSegmentCount);
        }

        [TestMethod]
        public void BrowsingHistoryCountsNewSegmentsUntilReturnToLive()
        {
            var viewModel = new TranscriptSessionViewModel();
            viewModel.ApplySegment(Create(1));
            viewModel.BeginBrowsingHistory();
            viewModel.ApplySegment(Create(2));
            viewModel.ApplySegment(Create(3));

            Assert.AreEqual(2, viewModel.PendingSegmentCount);
            Assert.IsTrue(viewModel.IsReturnToLiveVisible);

            viewModel.ReturnToLive();
            viewModel.CompleteReturnToLive();

            Assert.AreEqual(0, viewModel.PendingSegmentCount);
            Assert.IsTrue(viewModel.IsFollowingLive);
        }

        [TestMethod]
        public void StreamingDraftTranslationIsClearedWithTheDraft()
        {
            var viewModel = new TranscriptSessionViewModel();
            viewModel.SetDraft("A live sentence");
            viewModel.SetDraftTranslation("一条实时译文");

            Assert.IsTrue(viewModel.HasDraftTranslation);
            Assert.AreEqual("一条实时译文", viewModel.DraftTranslation);

            viewModel.SetDraft(string.Empty);

            Assert.IsFalse(viewModel.HasDraftTranslation);
            Assert.AreEqual(string.Empty, viewModel.DraftTranslation);
        }

        private static TranscriptSegment Create(
            long sequence,
            Guid? id = null,
            int revision = 0,
            string? source = null)
        {
            return new TranscriptSegment(
                id ?? Guid.NewGuid(),
                sequence,
                revision,
                source ?? $"source {sequence}",
                $"translated {sequence}",
                SegmentState.Translated,
                DateTimeOffset.UtcNow);
        }
    }
}
