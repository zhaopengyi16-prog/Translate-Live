using LiveCaptionsTranslator.services;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class FinalCaptionAdmissionGateTests
    {
        [TestMethod]
        public void DifferentIdentitiesAreNeverCollapsedByTextOrTime()
        {
            var gate = new FinalCaptionAdmissionGate();
            DateTimeOffset start = Start();
            LiveCaptionSegment first = Create(1, "Same words.", start);
            LiveCaptionSegment second = Create(
                2,
                "Same words.",
                start.AddMilliseconds(50));

            Assert.IsTrue(gate.Evaluate(first, start).IsAdmitted);
            Assert.IsTrue(gate.Evaluate(second, second.CapturedAt).IsAdmitted);
            Assert.AreNotEqual(first.Id, second.Id);
        }

        [TestMethod]
        public void DuplicateFinalRevisionForOneIdentityIsIdempotent()
        {
            var gate = new FinalCaptionAdmissionGate();
            LiveCaptionSegment first = Create(1, "One final.", Start());

            FinalCaptionAdmissionDecision admitted = gate.Evaluate(
                first,
                first.CapturedAt);
            FinalCaptionAdmissionDecision duplicate = gate.Evaluate(
                first,
                first.CapturedAt.AddSeconds(10));

            Assert.IsTrue(admitted.IsAdmitted);
            Assert.IsFalse(duplicate.IsAdmitted);
            Assert.AreEqual(first, duplicate.CanonicalSegment);
        }

        [TestMethod]
        public void NewerRevisionIsAdmittedAndOlderRevisionCannotReturn()
        {
            var gate = new FinalCaptionAdmissionGate();
            LiveCaptionSegment first = Create(1, "Initial final.", Start());
            LiveCaptionSegment newest = first with
            {
                Revision = 2,
                Text = "Newest final."
            };
            LiveCaptionSegment stale = first with
            {
                Revision = 1,
                Text = "Stale final."
            };

            Assert.IsTrue(gate.Evaluate(first, first.CapturedAt).IsAdmitted);
            Assert.IsTrue(gate.Evaluate(newest, Start().AddSeconds(1)).IsAdmitted);
            FinalCaptionAdmissionDecision rejected = gate.Evaluate(
                stale,
                Start().AddSeconds(2));

            Assert.IsFalse(rejected.IsAdmitted);
            Assert.AreEqual(newest, rejected.CanonicalSegment);
        }

        [TestMethod]
        public void RepeatedDraftFrameRemainsAvailableForStabilityMeasurement()
        {
            var gate = new FinalCaptionAdmissionGate();
            LiveCaptionSegment draft = Create(
                1,
                "A draft is still growing",
                Start(),
                isFinal: false);

            LiveCaptionSegment? first = gate.ResolveDraft(draft, Start());
            LiveCaptionSegment? repeated = gate.ResolveDraft(
                draft,
                Start().AddMilliseconds(700));

            Assert.AreEqual(draft, first);
            Assert.AreEqual(draft, repeated);
        }

        [TestMethod]
        public void FinalAfterDraftRequiresANewerResolvedRevision()
        {
            var gate = new FinalCaptionAdmissionGate();
            LiveCaptionSegment draft = Create(
                1,
                "A draft becomes final",
                Start(),
                isFinal: false);
            gate.ResolveDraft(draft, Start());

            FinalCaptionAdmissionDecision sameRevision = gate.Evaluate(
                draft with { IsFinal = true },
                Start().AddMilliseconds(100));
            FinalCaptionAdmissionDecision newerFinal = gate.Evaluate(
                draft with
                {
                    Revision = 1,
                    Text = "A draft becomes final.",
                    IsFinal = true
                },
                Start().AddMilliseconds(200));

            Assert.IsFalse(sameRevision.IsAdmitted);
            Assert.IsTrue(newerFinal.IsAdmitted);
        }

        [TestMethod]
        public void ResetSeparatesClassroomSessions()
        {
            var gate = new FinalCaptionAdmissionGate();
            LiveCaptionSegment first = Create(1, "Session boundary.", Start());

            Assert.IsTrue(gate.Evaluate(first, Start()).IsAdmitted);
            gate.Reset();
            Assert.IsTrue(gate.Evaluate(first, Start()).IsAdmitted);
        }

        private static DateTimeOffset Start()
        {
            return new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);
        }

        private static LiveCaptionSegment Create(
            long sequence,
            string text,
            DateTimeOffset capturedAt,
            bool isFinal = true)
        {
            return new LiveCaptionSegment(
                Guid.NewGuid(),
                sequence,
                0,
                text,
                isFinal,
                capturedAt);
        }
    }
}
