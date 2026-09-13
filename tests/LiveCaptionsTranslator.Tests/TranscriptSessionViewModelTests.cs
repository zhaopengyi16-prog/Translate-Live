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
        public void RepeatedEventsForOneIdentityRemainOneVisibleRow()
        {
            var viewModel = new TranscriptSessionViewModel();
            Guid id = Guid.NewGuid();

            for (int index = 0; index < 4; index++)
                viewModel.ApplySegment(Create(1, id, source: "Same sentence."));

            Assert.HasCount(1, viewModel.Segments);
            Assert.AreEqual(id, viewModel.Segments[0].Id);
        }

        [TestMethod]
        public void RevisionCannotMoveTheOriginalTimeOrSequence()
        {
            var viewModel = new TranscriptSessionViewModel();
            Guid id = Guid.NewGuid();
            DateTimeOffset firstCapture = new(
                2026, 9, 11, 14, 13, 29, TimeSpan.Zero);
            viewModel.ApplySegment(new TranscriptSegment(
                id,
                7,
                0,
                "Original.",
                null,
                SegmentState.Committed,
                firstCapture));

            viewModel.ApplySegment(new TranscriptSegment(
                id,
                99,
                1,
                "Corrected.",
                "修订。",
                SegmentState.Translated,
                firstCapture.AddSeconds(30)));

            Assert.AreEqual(7, viewModel.Segments[0].Sequence);
            Assert.AreEqual(firstCapture, viewModel.Segments[0].CapturedAt);
            Assert.AreEqual("Corrected.", viewModel.Segments[0].SourceText);
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
        public void BrowsingHistoryAlwaysOffersReturnToLive()
        {
            var viewModel = new TranscriptSessionViewModel();
            viewModel.ApplySegment(Create(1));

            viewModel.BeginBrowsingHistory();

            Assert.IsTrue(viewModel.IsBrowsingHistory);
            Assert.IsTrue(viewModel.IsReturnToLiveVisible);
            Assert.AreEqual("回到实时", viewModel.ReturnToLiveText);
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

        [TestMethod]
        public void CompletingAnOlderSegmentDoesNotClearTheNextDraft()
        {
            var viewModel = new TranscriptSessionViewModel();
            Guid firstId = Guid.NewGuid();
            Guid secondId = Guid.NewGuid();
            viewModel.SetDraft(new TranscriptSegment(
                secondId,
                2,
                1,
                "Second sentence is already being recognized",
                "第二句正在识别",
                SegmentState.Draft,
                DateTimeOffset.UtcNow));

            bool cleared = viewModel.ClearDraft(firstId, finalRevision: 4);

            Assert.IsFalse(cleared);
            Assert.AreEqual(secondId, viewModel.DraftSegmentId);
            Assert.AreEqual("Second sentence is already being recognized", viewModel.DraftText);
            Assert.AreEqual("第二句正在识别", viewModel.DraftTranslation);
        }

        [TestMethod]
        public void OlderDraftTranslationCannotOverwriteANewerRevision()
        {
            var viewModel = new TranscriptSessionViewModel();
            Guid id = Guid.NewGuid();
            viewModel.SetDraft(new TranscriptSegment(
                id,
                1,
                2,
                "Current revision",
                null,
                SegmentState.Draft,
                DateTimeOffset.UtcNow));

            bool applied = viewModel.SetDraftTranslation(id, 1, "过期译文");

            Assert.IsFalse(applied);
            Assert.AreEqual(string.Empty, viewModel.DraftTranslation);
        }

        [TestMethod]
        public void LoadedHistoryRowCanBeReboundToItsLiveIdentity()
        {
            var viewModel = new TranscriptSessionViewModel();
            Guid projectedId = Guid.NewGuid();
            Guid canonicalId = Guid.NewGuid();
            viewModel.ApplySegment(Create(
                10,
                projectedId,
                source: "loaded history"));

            bool rebound = viewModel.RebindSegmentIdentity(
                projectedId,
                Create(
                    3,
                    canonicalId,
                    revision: 1,
                    source: "canonical live row"));

            Assert.IsTrue(rebound);
            Assert.HasCount(1, viewModel.Segments);
            Assert.AreEqual(canonicalId, viewModel.Segments[0].Id);
            Assert.AreEqual(3, viewModel.Segments[0].Sequence);
            Assert.AreEqual("canonical live row", viewModel.Segments[0].SourceText);
        }

        [TestMethod]
        public void RebindingRemovesAnInterleavedHistoryProjectionDuplicate()
        {
            var viewModel = new TranscriptSessionViewModel();
            Guid projectedId = Guid.NewGuid();
            Guid canonicalId = Guid.NewGuid();
            viewModel.ApplySegment(Create(
                10,
                projectedId,
                source: "database projection"));
            TranscriptSegment canonical = Create(
                3,
                canonicalId,
                revision: 1,
                source: "live event");
            viewModel.ApplySegment(canonical);

            bool rebound = viewModel.RebindSegmentIdentity(
                projectedId,
                canonical);

            Assert.IsTrue(rebound);
            Assert.HasCount(1, viewModel.Segments);
            Assert.AreEqual(canonicalId, viewModel.Segments[0].Id);
            Assert.AreEqual("live event", viewModel.Segments[0].SourceText);
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
