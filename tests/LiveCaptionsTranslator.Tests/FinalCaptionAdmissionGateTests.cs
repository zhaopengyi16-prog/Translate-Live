using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.services;
using LiveCaptionsTranslator.viewmodels;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class FinalCaptionAdmissionGateTests
    {
        [TestMethod]
        public void ContinuousRapidExactCandidatesRemainOneLogicalFinal()
        {
            var gate = new FinalCaptionAdmissionGate();
            DateTimeOffset start = new(2026, 9, 13, 23, 19, 5, TimeSpan.Zero);
            const string text =
                "Everything else kind of face into the background.";
            var admitted = new List<LiveCaptionSegment>();

            for (int index = 0; index < 40; index++)
            {
                LiveCaptionSegment candidate = Create(
                    index + 1,
                    text,
                    start.AddMilliseconds(index * 250));
                FinalCaptionAdmissionDecision decision = gate.Evaluate(
                    candidate,
                    candidate.CapturedAt);
                if (decision.IsAdmitted)
                    admitted.Add(candidate);
                else
                    Assert.AreEqual(admitted[0].Id, decision.CanonicalSegment.Id);
            }

            Assert.HasCount(1, admitted);
        }

        [TestMethod]
        public void ContinuousDuplicateAliasesRemainBounded()
        {
            var gate = new FinalCaptionAdmissionGate();
            DateTimeOffset start = new(2026, 9, 13, 23, 19, 5, TimeSpan.Zero);
            const string text = "A recycled accessibility final.";

            for (int index = 0; index < 250; index++)
            {
                LiveCaptionSegment candidate = Create(
                    index + 1,
                    text,
                    start.AddMilliseconds(index * 100));
                gate.Evaluate(candidate, candidate.CapturedAt);
            }

            Assert.IsLessThanOrEqualTo(
                LiveCaptionSegmentationThresholds.FinalAdmissionAliasCapacity,
                gate.AliasCount);
        }

        [TestMethod]
        public void FreshDraftEvidencePreservesARealRepeatedUtterance()
        {
            var gate = new FinalCaptionAdmissionGate();
            DateTimeOffset start = new(2026, 9, 13, 23, 19, 5, TimeSpan.Zero);
            const string text = "I can speak with you.";
            LiveCaptionSegment first = Create(1, text, start);
            Assert.IsTrue(gate.Evaluate(first, start).IsAdmitted);

            LiveCaptionSegment draft = Create(
                2,
                "I can speak with",
                start.AddMilliseconds(300),
                revision: 0,
                isFinal: false);
            gate.ObserveDraft(draft, draft.CapturedAt);
            LiveCaptionSegment repeated = draft with
            {
                Revision = 1,
                Text = text,
                IsFinal = true
            };

            Assert.IsTrue(gate.Evaluate(
                repeated,
                start.AddMilliseconds(700)).IsAdmitted);
            Assert.AreNotEqual(first.Id, repeated.Id);
        }

        [TestMethod]
        public void InterveningFinalPreservesARealRepeatedUtterance()
        {
            var gate = new FinalCaptionAdmissionGate();
            DateTimeOffset start = new(2026, 9, 13, 23, 19, 5, TimeSpan.Zero);
            LiveCaptionSegment first = Create(1, "Same words.", start);
            LiveCaptionSegment middle = Create(
                2,
                "A different sentence.",
                start.AddMilliseconds(300));
            LiveCaptionSegment repeated = Create(
                3,
                "Same words.",
                start.AddMilliseconds(600));

            Assert.IsTrue(gate.Evaluate(first, first.CapturedAt).IsAdmitted);
            Assert.IsTrue(gate.Evaluate(middle, middle.CapturedAt).IsAdmitted);
            Assert.IsTrue(gate.Evaluate(repeated, repeated.CapturedAt).IsAdmitted);
        }

        [TestMethod]
        public void SameIdentityRevisionUpdatesInsteadOfBeingSuppressed()
        {
            var gate = new FinalCaptionAdmissionGate();
            DateTimeOffset start = new(2026, 9, 13, 23, 19, 5, TimeSpan.Zero);
            LiveCaptionSegment first = Create(1, "Initial final.", start);
            LiveCaptionSegment revision = first with
            {
                Revision = 1,
                Text = "Corrected final."
            };

            Assert.IsTrue(gate.Evaluate(first, start).IsAdmitted);
            Assert.IsTrue(gate.Evaluate(
                revision,
                start.AddMilliseconds(250)).IsAdmitted);
            Assert.IsFalse(gate.Evaluate(
                first,
                start.AddMilliseconds(500)).IsAdmitted);
        }

        [TestMethod]
        public void SuppressedIdentityGrowthRevisesTheCanonicalRow()
        {
            var gate = new FinalCaptionAdmissionGate();
            var viewModel = new TranscriptSessionViewModel();
            DateTimeOffset start = new(2026, 9, 13, 23, 19, 5, TimeSpan.Zero);
            LiveCaptionSegment first = Create(
                1,
                "Everything else fades into the background.",
                start);
            LiveCaptionSegment recycled = Create(
                2,
                first.Text,
                start.AddMilliseconds(200));

            FinalCaptionAdmissionDecision initial = gate.Evaluate(
                first,
                first.CapturedAt);
            Assert.IsTrue(initial.IsAdmitted);
            viewModel.ApplySegment(Project(initial.CanonicalSegment));
            FinalCaptionAdmissionDecision duplicate = gate.Evaluate(
                recycled,
                recycled.CapturedAt);
            Assert.IsFalse(duplicate.IsAdmitted);
            Assert.AreEqual(first.Id, duplicate.CanonicalSegment.Id);

            LiveCaptionSegment growth = recycled with
            {
                Revision = 1,
                Text = "Everything else fades into the background while the speaker continues."
            };
            FinalCaptionAdmissionDecision revision = gate.Evaluate(
                growth,
                start.AddMilliseconds(500));

            Assert.IsTrue(revision.IsAdmitted);
            Assert.AreEqual(first.Id, revision.CanonicalSegment.Id);
            Assert.AreEqual(first.Sequence, revision.CanonicalSegment.Sequence);
            Assert.AreEqual(first.CapturedAt, revision.CanonicalSegment.CapturedAt);
            Assert.AreEqual(first.Revision + 1, revision.CanonicalSegment.Revision);
            Assert.AreEqual(growth.Text, revision.CanonicalSegment.Text);
            viewModel.ApplySegment(Project(revision.CanonicalSegment));
            Assert.HasCount(1, viewModel.Segments);
            Assert.AreEqual(first.Id, viewModel.Segments[0].Id);
            Assert.AreEqual(growth.Text, viewModel.Segments[0].SourceText);
        }

        [TestMethod]
        public void QuietGapAllowsAnIdenticalDirectFinalToBeNewSpeech()
        {
            var gate = new FinalCaptionAdmissionGate();
            DateTimeOffset start = new(2026, 9, 13, 23, 19, 5, TimeSpan.Zero);
            LiveCaptionSegment first = Create(1, "Please repeat.", start);
            LiveCaptionSegment later = Create(
                2,
                "Please repeat.",
                start +
                    LiveCaptionSegmentationThresholds
                        .AccessibilityDuplicateBurstWindow +
                    TimeSpan.FromMilliseconds(1));

            Assert.IsTrue(gate.Evaluate(first, first.CapturedAt).IsAdmitted);
            Assert.IsTrue(gate.Evaluate(later, later.CapturedAt).IsAdmitted);
        }

        [TestMethod]
        public void ResetSeparatesClassroomSessions()
        {
            var gate = new FinalCaptionAdmissionGate();
            DateTimeOffset start = new(2026, 9, 13, 23, 19, 5, TimeSpan.Zero);
            LiveCaptionSegment first = Create(1, "Session boundary.", start);
            LiveCaptionSegment nextSession = Create(
                1,
                "Session boundary.",
                start.AddMilliseconds(100));

            Assert.IsTrue(gate.Evaluate(first, start).IsAdmitted);
            gate.Reset();
            Assert.IsTrue(gate.Evaluate(
                nextSession,
                nextSession.CapturedAt).IsAdmitted);
        }

        private static LiveCaptionSegment Create(
            long sequence,
            string text,
            DateTimeOffset capturedAt,
            int revision = 0,
            bool isFinal = true)
        {
            return new LiveCaptionSegment(
                Guid.NewGuid(),
                sequence,
                revision,
                text,
                isFinal,
                capturedAt);
        }

        private static TranscriptSegment Project(LiveCaptionSegment segment)
        {
            return new TranscriptSegment(
                segment.Id,
                segment.Sequence,
                segment.Revision,
                segment.Text,
                null,
                SegmentState.Committed,
                segment.CapturedAt);
        }
    }
}
