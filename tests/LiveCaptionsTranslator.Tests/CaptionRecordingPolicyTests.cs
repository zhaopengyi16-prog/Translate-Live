using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.services;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class CaptionRecordingPolicyTests
    {
        private static readonly DateTimeOffset Start = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);

        [TestMethod]
        public void CorrectedTailAfterStablePrefixDoesNotFlushAnObsoleteDraft()
        {
            var source = new LiveCaptionIdentityResolver(splitLongDrafts: false);
            var policy = new CaptionRecordingPolicy();
            const string prefix = "Alpha circuit is enabled.";
            var first = source.Process(prefix + " the valve is", Start);
            Guid tailId = first.DraftSegment!.Id;
            var accepted = new List<RecordedCaption>(policy.Observe(first, TimeSpan.Zero));
            var final = source.Process(prefix + " An unstable reading appears.",
                Start.AddMilliseconds(200));
            accepted.AddRange(policy.Observe(final, TimeSpan.FromMilliseconds(200)));
            accepted.AddRange(policy.Observe(source.Process(
                prefix + " An unstable reading appears.", Start.AddSeconds(1)),
                TimeSpan.FromSeconds(1)));
            accepted.AddRange(policy.Flush());

            Assert.HasCount(2, accepted);
            Assert.AreEqual(tailId, accepted[1].Segment.Id);
            Assert.IsFalse(accepted.Any(record => record.IsIncomplete));
            CollectionAssert.AreEqual(new[] { prefix, "An unstable reading appears." },
                accepted.Select(record => record.Segment.Text).ToArray());
            CollectionAssert.AreEqual(new long[] { 1, 2 },
                accepted.Select(record => record.Segment.Sequence).ToArray());
        }

        [TestMethod]
        public void PunctuationRemainsLiveUntilStableThenRecordsExactlyOnce()
        {
            var source = new LiveCaptionIdentityResolver(splitLongDrafts: false);
            var policy = new CaptionRecordingPolicy();
            const string text = "The copper coil produces a magnetic field.";
            var first = source.Process(text, Start);
            Assert.HasCount(1, first.FinalizedSegments, "Recognition must remain immediate.");
            Assert.IsEmpty(policy.Observe(first, TimeSpan.Zero));
            Assert.IsEmpty(policy.Observe(source.Process(text, Start.AddMilliseconds(799)), TimeSpan.FromMilliseconds(799)));
            var committed = policy.Observe(source.Process(text, Start.AddMilliseconds(800)), TimeSpan.FromMilliseconds(800));
            Assert.HasCount(1, committed);
            Assert.AreEqual(first.CurrentSegment!.Id, committed[0].Segment.Id);
            Assert.AreEqual(Start, committed[0].Segment.CapturedAt);
            Assert.IsFalse(committed[0].IsIncomplete);
            Assert.IsEmpty(policy.Observe(source.Process(text, Start.AddSeconds(2)), TimeSpan.FromSeconds(2)));
            Assert.IsEmpty(policy.Flush());
        }

        [TestMethod]
        public void LongGrowingSpeechDoesNotEnterHistoryAtCharacterLimits()
        {
            var source = new LiveCaptionIdentityResolver(splitLongDrafts: false);
            var policy = new CaptionRecordingPolicy();
            const string text = "The experimental controller measures the current through the copper winding and adjusts the output voltage while the operator slowly increases the load and watches the temperature of the motor during the entire measurement";
            Guid? id = null;
            for (int words = 5; words <= text.Split(' ').Length; words++)
            {
                string snapshot = string.Join(" ", text.Split(' ').Take(words));
                var update = source.Process(snapshot, Start.AddMilliseconds(words * 50));
                id ??= update.CurrentSegment!.Id;
                Assert.AreEqual(id, update.CurrentSegment!.Id);
                Assert.IsEmpty(policy.Observe(update, TimeSpan.FromMilliseconds(words * 50)));
            }
            var final = source.Process(text + ".", Start.AddSeconds(4));
            Assert.IsEmpty(policy.Observe(final, TimeSpan.FromSeconds(4)));
            var accepted = policy.Observe(source.Process(text + ".", Start.AddSeconds(5)), TimeSpan.FromSeconds(5));
            Assert.HasCount(1, accepted);
            Assert.AreEqual(id, accepted[0].Segment.Id);
            Assert.AreEqual(text + ".", accepted[0].Segment.Text);
        }

        [TestMethod]
        public void PunctuationWithdrawalResetsStabilityAndRejectsOldTranslation()
        {
            var source = new LiveCaptionIdentityResolver(splitLongDrafts: false);
            var policy = new CaptionRecordingPolicy();
            const string first = "The measuring instrument reports a steady voltage.";
            var initial = source.Process(first, Start);
            policy.Observe(initial, TimeSpan.Zero);
            var identity = Identity(initial.CurrentSegment!, false);
            const string growth = "The measuring instrument reports a steady voltage across both terminals";
            var growing = source.Process(growth, Start.AddMilliseconds(500));
            Assert.IsEmpty(policy.Observe(growing, TimeSpan.FromMilliseconds(500)));
            Assert.AreEqual(identity.SegmentId, growing.CurrentSegment!.Id);
            Assert.IsFalse(policy.IsCurrentRevision(identity));
            Assert.IsEmpty(policy.Observe(source.Process(growth, Start.AddSeconds(5)), TimeSpan.FromSeconds(5)));
            var final = source.Process(growth + ".", Start.AddSeconds(6));
            Assert.IsEmpty(policy.Observe(final, TimeSpan.FromSeconds(6)));
            var accepted = policy.Observe(source.Process(growth + ".", Start.AddSeconds(7)), TimeSpan.FromSeconds(7));
            Assert.HasCount(1, accepted);
            Assert.AreEqual(identity.SegmentId, accepted[0].Segment.Id);
        }

        [TestMethod]
        public void ForwardSpeechCommitsPreviousSentenceWithoutWaitingForTimer()
        {
            var source = new LiveCaptionIdentityResolver(splitLongDrafts: false);
            var policy = new CaptionRecordingPolicy();
            const string a = "The blue indicator is illuminated.";
            const string b = "The operator checks the";
            policy.Observe(source.Process(a, Start), TimeSpan.Zero);
            var next = source.Process(a + " " + b, Start.AddMilliseconds(100));
            var accepted = policy.Observe(next, TimeSpan.FromMilliseconds(100));
            Assert.HasCount(1, accepted);
            Assert.AreEqual(a, accepted[0].Segment.Text);
            Assert.AreEqual(b, next.CurrentText);
        }

        [TestMethod]
        public void RotationAndLaterAppendDoNotMultiplyRecordedOccurrences()
        {
            var source = new LiveCaptionIdentityResolver(splitLongDrafts: false);
            var policy = new CaptionRecordingPolicy();
            const string a = "The copper winding remains cool.";
            const string b = "The power supply is enabled.";
            const string c = "The operator measures the output.";
            const string d = "The measured current is stable.";
            var accepted = new List<RecordedCaption>();
            string[] snapshots = [a, $"{a} {b}", $"{a} {b} {c}", $"{b} {c} {a}", $"{c} {a} {b}", $"{a} {b} {c} {d}"];
            for (int i = 0; i < snapshots.Length; i++)
                accepted.AddRange(policy.Observe(source.Process(snapshots[i], Start.AddMilliseconds(i * 100)), TimeSpan.FromMilliseconds(i * 100)));
            accepted.AddRange(policy.Flush());
            Assert.AreEqual(4, accepted.Select(item => item.Segment.Id).Distinct().Count());
            Assert.HasCount(4, accepted);
        }

        [TestMethod]
        public void WaitingDoesNotPromoteAmbiguousRepeatedSnapshot()
        {
            var source = new LiveCaptionIdentityResolver(splitLongDrafts: false);
            var policy = new CaptionRecordingPolicy();
            const string a = "The red switch is released.";
            const string b = "The green switch is pressed.";
            var accepted = new List<RecordedCaption>();
            accepted.AddRange(policy.Observe(source.Process(a, Start), TimeSpan.Zero));
            accepted.AddRange(policy.Observe(source.Process(b, Start.AddSeconds(1)), TimeSpan.FromSeconds(1)));
            accepted.AddRange(policy.Observe(source.Process(a, Start.AddSeconds(2)), TimeSpan.FromSeconds(2)));
            accepted.AddRange(policy.Observe(source.Process(a, Start.AddSeconds(30)), TimeSpan.FromSeconds(30)));
            accepted.AddRange(policy.Flush());
            Assert.AreEqual(2, accepted.Select(item => item.Segment.Id).Distinct().Count());
        }

        [TestMethod]
        public void StopPreservesUnfinishedWordsWithoutPretendingTheyAreComplete()
        {
            var source = new LiveCaptionIdentityResolver(splitLongDrafts: false);
            var policy = new CaptionRecordingPolicy();
            const string text = "The probe should be connected to the";
            var update = source.Process(text, Start);
            Assert.IsEmpty(policy.Observe(update, TimeSpan.Zero));
            var accepted = policy.Flush();
            Assert.HasCount(1, accepted);
            Assert.IsTrue(accepted[0].IsIncomplete);
            Assert.AreEqual(text, accepted[0].Segment.Text);
            Assert.IsEmpty(policy.Flush());
        }

        [TestMethod]
        public void ResetRejectsPreviousSessionAndStartsFreshObservationClock()
        {
            var source = new LiveCaptionIdentityResolver(splitLongDrafts: false);
            var policy = new CaptionRecordingPolicy();
            var old = source.Process("The calibration has finished.", Start);
            policy.Observe(old, TimeSpan.FromMinutes(1));
            var oldIdentity = Identity(old.CurrentSegment!, false);
            policy.Reset();
            source.StartFromCurrentSnapshot("The calibration has finished.", Start.AddMinutes(1));
            Assert.IsFalse(policy.IsCurrentRevision(oldIdentity));
            Assert.IsEmpty(policy.Observe(source.Process("The calibration has finished.", Start.AddMinutes(1)), TimeSpan.Zero));
            Assert.IsEmpty(policy.Flush());
        }

        [TestMethod]
        public void ClosingQuoteCountsAsSentenceEndAndBackwardClockCannotCommitEarly()
        {
            var source = new LiveCaptionIdentityResolver(splitLongDrafts: false);
            var policy = new CaptionRecordingPolicy();
            const string text = "The label reads \"Power is disconnected.\"";
            policy.Observe(source.Process(text, Start), TimeSpan.FromSeconds(10));
            Assert.IsEmpty(policy.Observe(source.Process(text, Start.AddMilliseconds(500)), TimeSpan.FromSeconds(1)));
            Assert.HasCount(1, policy.Observe(source.Process(text, Start.AddSeconds(1)), TimeSpan.FromMilliseconds(10800)));
        }

        private static TranslationTaskIdentity Identity(LiveCaptionSegment segment, bool final) =>
            new(segment.Id, segment.Sequence, segment.Revision, final, segment.CapturedAt);
    }
}
