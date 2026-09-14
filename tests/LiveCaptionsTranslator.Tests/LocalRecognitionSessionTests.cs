using LiveCaptionsTranslator.services.recognition;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class LocalRecognitionSessionTests
    {
        private static readonly DateTimeOffset Start =
            new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);

        [TestMethod]
        public void LongUtteranceGrowthRevisesOneIdentityInPlace()
        {
            const long sessionId = 101;
            Guid segmentId = Guid.Parse("20000000-0000-0000-0000-000000000001");
            var session = CreateSession(segmentId);
            session.BeginSession(sessionId, newCaptureEpoch: 7);

            RecognitionAcceptance first = Accept(session, sessionId, 7, 1, 0, "A long");
            RecognitionAcceptance second = Accept(session, sessionId, 7, 2, 50, "A long sentence keeps");
            RecognitionAcceptance repeated = Accept(session, sessionId, 7, 3, 60, "  A long   sentence keeps  ");
            RecognitionAcceptance third = Accept(
                session, sessionId, 7, 4, 100,
                "A long sentence keeps growing until the speaker stops");
            RecognitionAcceptance final = Accept(
                session, sessionId, 7, 5, 150,
                "A long sentence keeps growing until the speaker stops.",
                isFinal: true,
                RecognitionEndpointReason.Silence);

            Assert.IsTrue(first.IsAccepted);
            Assert.IsTrue(second.IsAccepted);
            Assert.AreEqual(RecognitionAcceptanceStatus.NoChange, repeated.Status);
            Assert.IsTrue(third.IsAccepted);
            Assert.IsTrue(final.IsAccepted);

            RecognitionEvent[] emitted = session.RecentEvents.ToArray();
            Assert.HasCount(4, emitted);
            Assert.IsTrue(emitted.All(item => item.SegmentId == segmentId));
            Assert.IsTrue(emitted.All(item => item.Sequence == 1));
            Assert.IsTrue(emitted.All(item => item.CapturedAt == Start));
            CollectionAssert.AreEqual(
                new[] { 0, 1, 2, 3 },
                emitted.Select(item => item.Revision).ToArray());
            Assert.IsTrue(emitted[^1].IsFinal);
            Assert.AreEqual(RecognitionEndpointReason.Silence, emitted[^1].EndpointReason);
            Assert.IsNull(session.ActiveUtterance);
        }

        [TestMethod]
        public void DuplicatePartialDoesNotAdvanceRevisionAndOlderCallbackIsRejected()
        {
            const long sessionId = 102;
            var session = CreateSession(
                Guid.Parse("20000000-0000-0000-0000-000000000002"));
            session.BeginSession(sessionId, newCaptureEpoch: 9);

            RecognitionAcceptance first = Accept(session, sessionId, 9, 10, 0, "same partial");
            RecognitionAcceptance duplicate = Accept(session, sessionId, 9, 11, 20, "same partial");
            RecognitionAcceptance stale = Accept(session, sessionId, 9, 10, 30, "stale rewrite");

            Assert.IsTrue(first.IsAccepted);
            Assert.AreEqual(RecognitionAcceptanceStatus.NoChange, duplicate.Status);
            Assert.AreEqual(
                RecognitionAcceptanceStatus.RejectedSourceRevision,
                stale.Status);
            Assert.AreEqual(0, session.ActiveUtterance?.Revision);
            Assert.HasCount(1, session.RecentEvents);
        }

        [TestMethod]
        public void FinalThenSameTextStartsARealIndependentUtterance()
        {
            const long sessionId = 103;
            Guid firstId = Guid.Parse("20000000-0000-0000-0000-000000000003");
            Guid secondId = Guid.Parse("20000000-0000-0000-0000-000000000004");
            var session = CreateSession(firstId, secondId);
            session.BeginSession(sessionId, newCaptureEpoch: 4);

            RecognitionEvent firstFinal = Accept(
                session, sessionId, 4, 1, 0, "Repeat me.", isFinal: true).Event!;
            RecognitionEvent secondPartial = Accept(
                session, sessionId, 4, 2, 500, "Repeat me.").Event!;
            RecognitionEvent secondFinal = Accept(
                session, sessionId, 4, 3, 700, "Repeat me.", isFinal: true).Event!;

            Assert.AreEqual(firstId, firstFinal.SegmentId);
            Assert.AreEqual(secondId, secondPartial.SegmentId);
            Assert.AreNotEqual(firstFinal.SegmentId, secondPartial.SegmentId);
            Assert.AreEqual(1, firstFinal.Sequence);
            Assert.AreEqual(2, secondPartial.Sequence);
            Assert.AreEqual(secondPartial.SegmentId, secondFinal.SegmentId);
            Assert.AreEqual(0, secondPartial.Revision);
            Assert.AreEqual(1, secondFinal.Revision);
        }

        [TestMethod]
        public void SessionSwitchRejectsLateClassroomAndCaptureEpochCallbacks()
        {
            const long firstSessionId = 104;
            const long secondSessionId = 105;
            var session = CreateSession(
                Guid.Parse("20000000-0000-0000-0000-000000000005"),
                Guid.Parse("20000000-0000-0000-0000-000000000006"));

            session.BeginSession(firstSessionId, newCaptureEpoch: 11);
            Assert.IsTrue(Accept(session, firstSessionId, 11, 1, 0, "old class").IsAccepted);

            session.BeginSession(secondSessionId, newCaptureEpoch: 12);
            RecognitionAcceptance oldClass = Accept(
                session, firstSessionId, 11, 2, 100, "late old class");
            RecognitionAcceptance oldEpoch = Accept(
                session, secondSessionId, 11, 3, 110, "late old epoch");
            RecognitionAcceptance current = Accept(
                session, secondSessionId, 12, 1, 120, "current class");

            Assert.AreEqual(RecognitionAcceptanceStatus.RejectedSession, oldClass.Status);
            Assert.AreEqual(RecognitionAcceptanceStatus.RejectedCaptureEpoch, oldEpoch.Status);
            Assert.IsTrue(current.IsAccepted);
            Assert.HasCount(1, session.RecentEvents);
            Assert.AreEqual(secondSessionId, session.RecentEvents[0].SessionId);
            Assert.AreEqual(12, session.RecentEvents[0].CaptureEpoch);
            Assert.AreEqual(1, session.RecentEvents[0].Sequence);
        }

        [TestMethod]
        public void StopCanFinalizeOrDiscardAnUnfinishedUtterance()
        {
            const long sessionId = 106;
            Guid segmentId = Guid.Parse("20000000-0000-0000-0000-000000000007");
            var finalizedSession = CreateSession(segmentId);
            finalizedSession.BeginSession(sessionId, newCaptureEpoch: 1);
            RecognitionEvent partial = Accept(
                finalizedSession, sessionId, 1, 1, 0, "unfinished words").Event!;

            RecognitionAcceptance stop = finalizedSession.Stop(finalizeUnfinished: true);

            Assert.IsTrue(stop.IsAccepted);
            Assert.AreEqual(partial.SegmentId, stop.Event?.SegmentId);
            Assert.AreEqual(partial.CapturedAt, stop.Event?.CapturedAt);
            Assert.AreEqual(partial.Revision + 1, stop.Event?.Revision);
            Assert.IsTrue(stop.Event?.IsFinal);
            Assert.AreEqual(
                RecognitionEndpointReason.SessionStopped,
                stop.Event?.EndpointReason);

            var discardedSession = CreateSession(
                Guid.Parse("20000000-0000-0000-0000-000000000008"));
            discardedSession.BeginSession(sessionId, newCaptureEpoch: 2);
            Accept(discardedSession, sessionId, 2, 1, 0, "discard me");
            RecognitionAcceptance discarded = discardedSession.Stop(finalizeUnfinished: false);

            Assert.AreEqual(RecognitionAcceptanceStatus.NoChange, discarded.Status);
            Assert.IsNull(discardedSession.ActiveUtterance);
            Assert.AreEqual(
                RecognitionAcceptanceStatus.RejectedStopped,
                Accept(discardedSession, sessionId, 2, 2, 50, "too late").Status);
        }

        [TestMethod]
        public void ContinuousSentencesStayDistinctOrderedAndEventStateIsBounded()
        {
            const long sessionId = 107;
            var ids = Enumerable.Range(1, 20)
                .Select(index => Guid.Parse($"30000000-0000-0000-0000-{index:D12}"))
                .ToArray();
            var session = CreateSession(eventCapacity: 7, ids);
            session.BeginSession(sessionId, newCaptureEpoch: 3);
            var finals = new List<RecognitionEvent>();
            long sourceRevision = 0;

            for (int index = 0; index < 20; index++)
            {
                Accept(
                    session,
                    sessionId,
                    3,
                    ++sourceRevision,
                    index * 100,
                    $"Sentence {index + 1}");
                RecognitionAcceptance final = Accept(
                    session,
                    sessionId,
                    3,
                    ++sourceRevision,
                    index * 100 + 50,
                    $"Sentence {index + 1}.",
                    isFinal: true,
                    RecognitionEndpointReason.VoiceActivity);
                finals.Add(final.Event!);
            }

            Assert.HasCount(20, finals);
            Assert.AreEqual(20, finals.Select(item => item.SegmentId).Distinct().Count());
            CollectionAssert.AreEqual(
                Enumerable.Range(1, 20).Select(index => (long)index).ToArray(),
                finals.Select(item => item.Sequence).ToArray());
            Assert.HasCount(7, session.RecentEvents);
            Assert.AreEqual(17, session.RecentEvents[0].Sequence);
            Assert.AreEqual(20, session.RecentEvents[^1].Sequence);
            Assert.IsNull(session.ActiveUtterance);
        }

        [TestMethod]
        public void ThirtyMinuteSyntheticLectureDoesNotMultiplyFinalIdentities()
        {
            const int utteranceCount = 1800;
            const long sessionId = 108;
            long nextId = 0;
            var session = new LocalRecognitionSession(
                eventCapacity: 32,
                segmentIdFactory: () =>
                {
                    nextId++;
                    return Guid.Parse($"40000000-0000-0000-0000-{nextId:D12}");
                });
            session.BeginSession(sessionId, newCaptureEpoch: 5);
            var finals = new List<RecognitionEvent>(utteranceCount);

            for (int second = 0; second < utteranceCount; second++)
            {
                RecognitionAcceptance accepted = Accept(
                    session,
                    sessionId,
                    5,
                    sourceRevision: second + 1,
                    offsetMilliseconds: second * 1000,
                    text: $"Synthetic lecture sentence {second + 1}.",
                    isFinal: true,
                    RecognitionEndpointReason.Silence);
                Assert.IsTrue(accepted.IsAccepted);
                finals.Add(accepted.Event!);
            }

            Assert.HasCount(utteranceCount, finals);
            Assert.AreEqual(
                utteranceCount,
                finals.Select(item => item.SegmentId).Distinct().Count());
            Assert.AreEqual(utteranceCount, finals[^1].Sequence);
            Assert.HasCount(32, session.RecentEvents);
            Assert.IsNull(session.ActiveUtterance);
        }

        private static LocalRecognitionSession CreateSession(params Guid[] segmentIds)
        {
            return CreateSession(
                LocalRecognitionSession.DefaultEventCapacity,
                segmentIds);
        }

        private static LocalRecognitionSession CreateSession(
            int eventCapacity,
            params Guid[] segmentIds)
        {
            var ids = new Queue<Guid>(segmentIds);
            return new LocalRecognitionSession(
                eventCapacity,
                () => ids.Dequeue());
        }

        private static RecognitionAcceptance Accept(
            LocalRecognitionSession session,
            long sessionId,
            long captureEpoch,
            long sourceRevision,
            int offsetMilliseconds,
            string text,
            bool isFinal = false,
            RecognitionEndpointReason endpointReason = RecognitionEndpointReason.None)
        {
            return session.Accept(new RecognitionUpdate(
                sessionId,
                captureEpoch,
                sourceRevision,
                Start.AddMilliseconds(offsetMilliseconds),
                text,
                isFinal,
                endpointReason));
        }
    }
}
