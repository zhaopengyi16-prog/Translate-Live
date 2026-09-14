using LiveCaptionsTranslator.services;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class LiveCaptionIdentityResolverTests
    {
        private const string A = "Alpha sentence is complete.";
        private const string B = "Bravo sentence is complete.";
        private const string C = "Charlie sentence is complete.";

        [TestMethod]
        public void StableCompletedPrefixAnchorsItsRadicallyCorrectedDraft()
        {
            var resolver = new LiveCaptionIdentityResolver(splitLongDrafts: false);
            var first = resolver.Process(A + " the valve is", Start());
            var correction = resolver.Process(A + " an unstable reading appears",
                Start().AddMilliseconds(200));
            var final = resolver.Process(A + " An unstable reading appears.",
                Start().AddMilliseconds(400));

            Assert.AreEqual(first.DraftSegment!.Id, correction.DraftSegment?.Id);
            Assert.AreEqual(first.DraftSegment.Id, final.FinalizedSegments.Single().Id);
            Assert.AreEqual(2, final.CurrentSegment!.Sequence);
        }

        [TestMethod]
        public void CompletingTheDraftAndAppendingAnotherSentenceKeepsBothOccurrences()
        {
            var resolver = new LiveCaptionIdentityResolver(splitLongDrafts: false);
            var first = resolver.Process(A + " the valve is open", Start());
            var next = resolver.Process(A + " The valve is open. An unstable reading appears.",
                Start().AddMilliseconds(200));

            Assert.HasCount(2, next.FinalizedSegments);
            Assert.AreEqual(first.DraftSegment!.Id, next.FinalizedSegments[0].Id);
            Assert.AreNotEqual(next.FinalizedSegments[0].Id, next.FinalizedSegments[1].Id);
        }

        [TestMethod]
        public void RadicalSingleDraftCorrectionRetainsOneIdentityUntilPunctuation()
        {
            var resolver = new LiveCaptionIdentityResolver(splitLongDrafts: false);
            LiveCaptionSegment first = resolver.Process("the valve is", Start()).DraftSegment!;
            LiveCaptionUpdate corrected = resolver.Process(
                "an unstable reading appears", Start().AddMilliseconds(200));
            LiveCaptionUpdate final = resolver.Process(
                "An unstable reading appears.", Start().AddMilliseconds(400));

            Assert.IsEmpty(corrected.FinalizedSegments);
            Assert.AreEqual(first.Id, corrected.DraftSegment?.Id);
            Assert.AreEqual(first.Id, final.FinalizedSegments.Single().Id);
            Assert.AreEqual(first.CapturedAt, final.FinalizedSegments.Single().CapturedAt);
            Assert.AreEqual("An unstable reading appears.", final.CurrentText);
        }

        [TestMethod]
        public void RadicalSingleDraftCanBeCorrectedDirectlyIntoItsFinal()
        {
            var resolver = new LiveCaptionIdentityResolver(splitLongDrafts: false);
            LiveCaptionSegment first = resolver.Process("the valve is", Start()).DraftSegment!;
            LiveCaptionSegment corrected = resolver.Process(
                "An unstable reading appears.", Start().AddMilliseconds(200))
                .FinalizedSegments.Single();

            Assert.AreEqual(first.Id, corrected.Id);
            Assert.IsTrue(corrected.Revision > first.Revision);
        }

        [TestMethod]
        public void HistoricalFinalCannotMasqueradeAsARadicalDraftCorrection()
        {
            var resolver = new LiveCaptionIdentityResolver(splitLongDrafts: false);
            resolver.Process(A, Start());
            LiveCaptionSegment draft = resolver.Process(
                "the valve is", Start().AddMilliseconds(100)).DraftSegment!;

            LiveCaptionUpdate replay = resolver.Process(A, Start().AddMilliseconds(200));

            Assert.IsEmpty(replay.FinalizedSegments);
            Assert.AreEqual(draft.Id, replay.DraftSegment?.Id);
            Assert.AreEqual(draft.Text, replay.CurrentText);
        }

        [TestMethod]
        public void StandaloneClippedHeadKeepsTheFullRecognizedSentence()
        {
            var resolver = new LiveCaptionIdentityResolver();
            const string full =
                "For the next experiment, the cooling circuit maintains a stable operating temperature.";
            const string clipped =
                "the cooling circuit maintains a stable operating temperature.";
            LiveCaptionSegment original = resolver.Process(full, Start())
                .FinalizedSegments.Single();

            LiveCaptionUpdate update = resolver.Process(
                clipped, Start().AddMilliseconds(200));

            Assert.IsEmpty(update.FinalizedSegments);
            Assert.AreEqual(original.Id, update.CurrentSegment?.Id);
            Assert.AreEqual(full, update.CurrentText);
            Assert.AreEqual(clipped, update.NormalizedText);
        }

        [TestMethod]
        public void ShiftedClippedHeadRetainsItsIdentityAndTheKnownSuffix()
        {
            var resolver = new LiveCaptionIdentityResolver();
            const string full =
                "During the trial, the cooling circuit maintains a stable operating temperature.";
            const string clipped =
                "the cooling circuit maintains a stable operating temperature.";
            LiveCaptionUpdate initial = resolver.Process($"{A} {full} {C}", Start());

            LiveCaptionUpdate update = resolver.Process(
                $"{clipped} {C}", Start().AddMilliseconds(200));

            Assert.IsEmpty(update.FinalizedSegments);
            CollectionAssert.AreEqual(
                initial.WindowSegmentIds.Skip(1).ToArray(),
                update.WindowSegmentIds.ToArray());
        }

        [TestMethod]
        public void ClippedCjkWindowPreservesTheOpeningWithoutAddingSpaces()
        {
            var resolver = new LiveCaptionIdentityResolver();
            const string full = "开始实验后，冷却回路保持稳定的工作温度。";
            LiveCaptionSegment first = resolver.Process(full, Start())
                .FinalizedSegments.Single();

            LiveCaptionUpdate clipped = resolver.Process(
                "冷却回路保持稳定的工作温度。", Start().AddMilliseconds(200));

            Assert.IsEmpty(clipped.FinalizedSegments);
            Assert.AreEqual(first.Id, clipped.CurrentSegment?.Id);
            Assert.AreEqual(full, clipped.CurrentText);
        }

        [TestMethod]
        public void ClippedFinalCanContinueWithoutDuplicatingOrLosingItsOpening()
        {
            var resolver = new LiveCaptionIdentityResolver();
            const string opening = "During the trial, ";
            const string body = "the cooling circuit maintains a stable operating temperature";
            LiveCaptionSegment first = resolver.Process(opening + body + ".", Start())
                .FinalizedSegments.Single();

            LiveCaptionUpdate growing = resolver.Process(
                body + " while the controller adjusts", Start().AddMilliseconds(200));
            LiveCaptionUpdate final = resolver.Process(
                body + " while the controller adjusts the inlet valve.",
                Start().AddMilliseconds(400));
            LiveCaptionSegment completed = final.FinalizedSegments.Single();

            Assert.AreEqual(first.Id, growing.DraftSegment?.Id);
            Assert.AreEqual(first.Id, completed.Id);
            Assert.AreEqual(first.CapturedAt, completed.CapturedAt);
            Assert.IsTrue(completed.Revision > growing.DraftSegment!.Revision);
            Assert.AreEqual(
                opening + body + " while the controller adjusts the inlet valve.",
                completed.Text);

            LiveCaptionUpdate replay = resolver.Process(
                body + ".", Start().AddMilliseconds(600));
            Assert.IsEmpty(replay.FinalizedSegments);
            Assert.AreEqual(completed.Text, replay.CurrentText);
        }

        [TestMethod]
        public void ANewDraftTrajectoryPreservesAGenuinelyRepeatedSuffix()
        {
            var resolver = new LiveCaptionIdentityResolver();
            const string body = "the cooling circuit maintains a stable operating temperature";
            LiveCaptionSegment first = resolver.Process(
                "During the trial, " + body + ".", Start()).FinalizedSegments.Single();
            LiveCaptionSegment nextDraft = resolver.Process(
                "the cooling", Start().AddMilliseconds(200)).DraftSegment!;
            resolver.Process(body, Start().AddMilliseconds(400));
            LiveCaptionSegment repeated = resolver.Process(
                body + ".", Start().AddMilliseconds(600)).FinalizedSegments.Single();

            Assert.AreNotEqual(first.Id, repeated.Id);
            Assert.AreEqual(nextDraft.Id, repeated.Id);
            Assert.AreEqual(body + ".", repeated.Text);
        }

        [TestMethod]
        public void ReplayedClippedIntermediateDraftCannotRegressTheLiveContinuation()
        {
            var resolver = new LiveCaptionIdentityResolver();
            const string opening = "During the trial, ";
            const string body = "the cooling circuit maintains a stable operating temperature";
            resolver.Process(opening + body + ".", Start());
            resolver.Process(body + " while the controller adjusts", Start().AddMilliseconds(100));
            LiveCaptionSegment newest = resolver.Process(
                body + " while the controller adjusts the inlet valve",
                Start().AddMilliseconds(200)).DraftSegment!;

            LiveCaptionUpdate replay = resolver.Process(
                body + " while the controller adjusts", Start().AddMilliseconds(300));

            Assert.IsEmpty(replay.FinalizedSegments);
            Assert.AreEqual(newest.Id, replay.DraftSegment?.Id);
            Assert.AreEqual(newest.Revision, replay.DraftSegment?.Revision);
            Assert.AreEqual(newest.Text, replay.CurrentText);
        }

        [TestMethod]
        public void CompleteSentenceModeKeepsLongClippedSpeechInOneIdentity()
        {
            var resolver = new LiveCaptionIdentityResolver(splitLongDrafts: false);
            const string opening = "Before beginning the controlled experiment, ";
            const string body =
                "the technician checks the cooling circuit and records the inlet pressure while " +
                "the controller maintains a steady flow through the primary channel and the " +
                "secondary sensor verifies the expected temperature";
            LiveCaptionSegment first = resolver.Process(opening + body, Start()).DraftSegment!;
            LiveCaptionUpdate clipped = resolver.Process(
                body + " throughout the test", Start().AddMilliseconds(200));
            LiveCaptionUpdate final = resolver.Process(
                body + " throughout the test.", Start().AddMilliseconds(400));

            Assert.IsEmpty(clipped.FinalizedSegments);
            Assert.AreEqual(first.Id, clipped.DraftSegment?.Id);
            Assert.HasCount(1, final.FinalizedSegments);
            Assert.AreEqual(first.Id, final.FinalizedSegments[0].Id);
            Assert.AreEqual(opening + body + " throughout the test.",
                final.FinalizedSegments[0].Text);
        }

        [TestMethod]
        public void RotatedCompletedWindowDoesNotCreateANewIdentity()
        {
            var resolver = new LiveCaptionIdentityResolver();
            DateTimeOffset start = Start();

            LiveCaptionUpdate initial = resolver.Process($"{A} {B} {C}", start);
            LiveCaptionUpdate rotated = resolver.Process(
                $"{B} {C} {A}",
                start.AddMilliseconds(200));

            Assert.HasCount(3, initial.FinalizedSegments);
            Assert.IsEmpty(rotated.FinalizedSegments);
            Assert.AreEqual(
                3,
                initial.FinalizedSegments
                    .Concat(rotated.FinalizedSegments)
                    .Select(segment => segment.Id)
                    .Distinct()
                    .Count());
        }

        [TestMethod]
        public void RepeatedRotationDoesNotReplaceGrowingFrontierDraft()
        {
            var resolver = new LiveCaptionIdentityResolver();
            DateTimeOffset start = Start();
            LiveCaptionUpdate initial = resolver.Process($"{A} {B} {C}", start);
            LiveCaptionSegment c = initial.FinalizedSegments[2];

            LiveCaptionUpdate rotated = resolver.Process(
                $"{B} {C} {A}",
                start.AddMilliseconds(200));
            LiveCaptionUpdate growing = resolver.Process(
                $"{B} {A} Charlie sentence is complete and keeps growing",
                start.AddMilliseconds(400));
            LiveCaptionUpdate final = resolver.Process(
                $"{B} {A} Charlie sentence is complete and keeps growing.",
                start.AddMilliseconds(600));

            Assert.IsEmpty(rotated.FinalizedSegments);
            Assert.IsEmpty(growing.FinalizedSegments);
            Assert.AreEqual(c.Id, growing.DraftSegment?.Id);
            Assert.HasCount(1, final.FinalizedSegments);
            Assert.AreEqual(c.Id, final.FinalizedSegments[0].Id);
        }

        [TestMethod]
        public void PunctuationlessContinuationInsideAFullWindowKeepsFinalIdentity()
        {
            var resolver = new LiveCaptionIdentityResolver();
            DateTimeOffset start = Start();
            LiveCaptionUpdate initial = resolver.Process($"{A} {B} {C}", start);
            LiveCaptionSegment c = initial.FinalizedSegments[2];

            LiveCaptionUpdate continuation = resolver.Process(
                $"{A} {B} Charlie sentence is complete and continues growing",
                start.AddMilliseconds(200));
            LiveCaptionUpdate final = resolver.Process(
                $"{A} {B} Charlie sentence is complete and continues growing.",
                start.AddMilliseconds(400));

            Assert.IsEmpty(continuation.FinalizedSegments);
            Assert.AreEqual(c.Id, continuation.DraftSegment?.Id);
            Assert.HasCount(1, final.FinalizedSegments);
            Assert.AreEqual(c.Id, final.FinalizedSegments[0].Id);
            Assert.AreEqual(c.CapturedAt, final.FinalizedSegments[0].CapturedAt);
        }

        [TestMethod]
        public void ReplayedFinalNeverCreatesAnAliasBeforeDraftContinuation()
        {
            var resolver = new LiveCaptionIdentityResolver();
            DateTimeOffset start = Start();
            LiveCaptionSegment canonical = resolver.Process(A, start)
                .FinalizedSegments.Single();
            LiveCaptionUpdate replay = resolver.Process(
                A + "\u200E",
                start.AddMilliseconds(100));
            LiveCaptionSegment draft = resolver.Process(
                "Alpha sentence is complete and continues",
                start.AddMilliseconds(200)).DraftSegment!;

            Assert.IsEmpty(replay.FinalizedSegments);
            Assert.AreEqual(canonical.Id, draft.Id);
            Assert.AreEqual(canonical.Sequence, draft.Sequence);
            Assert.AreEqual(canonical.CapturedAt, draft.CapturedAt);
        }

        [TestMethod]
        public void RotationFollowedByNewSentenceResumesAtTheExistingFrontier()
        {
            var resolver = new LiveCaptionIdentityResolver();
            DateTimeOffset start = Start();
            LiveCaptionUpdate initial = resolver.Process($"{A} {B} {C}", start);
            LiveCaptionUpdate rotated = resolver.Process(
                $"{B} {C} {A}",
                start.AddMilliseconds(200));
            LiveCaptionUpdate forward = resolver.Process(
                $"{C} {A} Delta sentence is genuinely new.",
                start.AddMilliseconds(400));

            Assert.IsEmpty(rotated.FinalizedSegments);
            Assert.HasCount(1, forward.FinalizedSegments);
            Assert.AreEqual(
                "Delta sentence is genuinely new.",
                forward.FinalizedSegments[0].Text);
            Assert.AreEqual(
                4,
                initial.FinalizedSegments
                    .Concat(rotated.FinalizedSegments)
                    .Concat(forward.FinalizedSegments)
                    .Select(segment => segment.Id)
                    .Distinct()
                    .Count());
        }

        [TestMethod]
        public void DifferentDirectionReordersReuseEveryWindowOccurrence()
        {
            var resolver = new LiveCaptionIdentityResolver();
            DateTimeOffset start = Start();
            const string d = "Delta sentence is complete.";
            LiveCaptionUpdate initial = resolver.Process(
                $"{A} {B} {C} {d}",
                start);
            Guid[] expected = initial.FinalizedSegments
                .Select(segment => segment.Id)
                .Order()
                .ToArray();

            LiveCaptionUpdate firstReorder = resolver.Process(
                $"{C} {A} {d} {B}",
                start.AddMilliseconds(100));
            LiveCaptionUpdate secondReorder = resolver.Process(
                $"{d} {B} {A} {C}",
                start.AddMilliseconds(200));

            Assert.IsEmpty(firstReorder.FinalizedSegments);
            Assert.IsEmpty(secondReorder.FinalizedSegments);
            CollectionAssert.AreEqual(
                expected,
                firstReorder.WindowSegmentIds.Order().ToArray());
            CollectionAssert.AreEqual(
                expected,
                secondReorder.WindowSegmentIds.Order().ToArray());
        }

        [TestMethod]
        public void TenSentenceWindowKeepsEveryIdentityWhenAnInteriorSentenceIsRewritten()
        {
            var resolver = new LiveCaptionIdentityResolver();
            DateTimeOffset start = Start();
            string[] sentences =
            [
                "Alpha introduces the lesson objective.",
                "Bravo explains the first classroom example.",
                "Charlie compares the available approaches.",
                "Delta describes the expected result.",
                "Echo records the recognizer's original wording.",
                "Foxtrot connects the example to the lecture.",
                "Golf summarizes the student's observation.",
                "Hotel provides a second supporting detail.",
                "India checks the conclusion for accuracy.",
                "Juliet closes the continuous explanation."
            ];
            LiveCaptionUpdate initial = resolver.Process(
                string.Join(' ', sentences),
                start);
            Guid rewrittenIdentity = initial.FinalizedSegments[4].Id;

            sentences[4] =
                "A recognition correction replaces the entire middle statement.";
            LiveCaptionUpdate corrected = resolver.Process(
                string.Join(' ', sentences),
                start.AddMilliseconds(120));

            Assert.HasCount(10, initial.FinalizedSegments);
            Assert.HasCount(
                10,
                corrected.WindowSegmentIds,
                "中间一句修订不能让它后面的既有句子从窗口身份映射中消失。");
            Assert.HasCount(1, corrected.FinalizedSegments);
            Assert.AreEqual(
                rewrittenIdentity,
                corrected.FinalizedSegments[0].Id,
                "有稳定左右邻句的位置修订必须沿用原 SegmentId。");
            Assert.AreEqual(1, corrected.FinalizedSegments[0].Revision);
        }

        [TestMethod]
        public void ContinuousTwelveSentenceReplayKeepsOneIdentityPerSpokenSentence()
        {
            var resolver = new LiveCaptionIdentityResolver();
            DateTimeOffset observedAt = Start();
            string[] sentences =
            [
                "Amber opens the continuous lecture.",
                "Birch states the first learning goal.",
                "Cedar gives a practical example.",
                "Denim explains the key distinction.",
                "Elm connects the preceding ideas.",
                "Flint contains the recognizer's changing wording.",
                "Grove checks the supporting evidence.",
                "Hazel describes the expected behavior.",
                "Ivory compares the two outcomes.",
                "Juniper answers the open question.",
                "Kite summarizes the final argument.",
                "Linen closes the spoken passage."
            ];
            var emitted = new List<LiveCaptionSegment>();
            for (int spoken = 1; spoken <= sentences.Length; spoken++)
            {
                LiveCaptionUpdate spokenUpdate = resolver.Process(
                    string.Join(' ', sentences.Take(spoken)),
                    observedAt);
                emitted.AddRange(spokenUpdate.FinalizedSegments);
                Assert.HasCount(1, spokenUpdate.FinalizedSegments);
                Assert.AreEqual(spoken, spokenUpdate.WindowSegmentIds.Count);
                observedAt = observedAt.AddMilliseconds(80);
            }
            Guid correctedIdentity = emitted[5].Id;

            for (int revision = 1; revision <= 24; revision++)
            {
                observedAt = observedAt.AddMilliseconds(80);
                sentences[5] =
                    $"Recognition pass {revision} rewrites the middle caption with different words.";
                LiveCaptionUpdate update = resolver.Process(
                    string.Join(' ', sentences),
                    observedAt);
                emitted.AddRange(update.FinalizedSegments);

                Assert.HasCount(12, update.WindowSegmentIds);
                Assert.HasCount(1, update.FinalizedSegments);
                Assert.AreEqual(correctedIdentity, update.FinalizedSegments[0].Id);
                Assert.AreEqual(revision, update.FinalizedSegments[0].Revision);
            }

            string[] rotated = sentences.Skip(3).Concat(sentences.Take(3)).ToArray();
            observedAt = observedAt.AddMilliseconds(80);
            LiveCaptionUpdate rotation = resolver.Process(
                string.Join(' ', rotated),
                observedAt);
            Assert.IsEmpty(rotation.FinalizedSegments);
            Assert.AreEqual(12, rotation.WindowSegmentIds.Distinct().Count());

            rotated[2] = "A second full correction changes one rotated interior caption.";
            observedAt = observedAt.AddMilliseconds(80);
            LiveCaptionUpdate rotatedCorrection = resolver.Process(
                string.Join(' ', rotated),
                observedAt);
            emitted.AddRange(rotatedCorrection.FinalizedSegments);
            Assert.HasCount(12, rotatedCorrection.WindowSegmentIds);
            Assert.HasCount(1, rotatedCorrection.FinalizedSegments);
            Assert.AreEqual(
                correctedIdentity,
                rotatedCorrection.FinalizedSegments[0].Id);

            string[] rolledForward = rotated
                .Skip(1)
                .Append("Maple is one genuinely appended sentence.")
                .ToArray();
            observedAt = observedAt.AddMilliseconds(80);
            LiveCaptionUpdate appended = resolver.Process(
                string.Join(' ', rolledForward),
                observedAt);
            emitted.AddRange(appended.FinalizedSegments);

            Assert.HasCount(1, appended.FinalizedSegments);
            Assert.AreEqual("Maple is one genuinely appended sentence.",
                appended.FinalizedSegments[0].Text);
            Assert.AreEqual(13, emitted.Select(segment => segment.Id).Distinct().Count());
            Assert.AreEqual(13, emitted.Select(segment => segment.Sequence).Distinct().Count());
        }

        [TestMethod]
        public void EightyContinuousSentencesProduceEightyIdentitiesNotWindowReplays()
        {
            var resolver = new LiveCaptionIdentityResolver();
            DateTimeOffset observedAt = Start();
            var visibleWindow = new List<string>();
            var emitted = new List<LiveCaptionSegment>();

            for (int spoken = 1; spoken <= 80; spoken++)
            {
                visibleWindow.Add(
                    $"Continuous lecture item {spoken} explains one distinct idea.");
                if (visibleWindow.Count > 10)
                    visibleWindow.RemoveAt(0);

                LiveCaptionUpdate update = resolver.Process(
                    string.Join(' ', visibleWindow),
                    observedAt);
                emitted.AddRange(update.FinalizedSegments);
                Assert.HasCount(
                    1,
                    update.FinalizedSegments,
                    $"第 {spoken} 句只应放行一个新逻辑句子。");

                if (visibleWindow.Count == 10 && spoken % 8 == 0)
                {
                    string[] rotated = visibleWindow
                        .Skip(3)
                        .Concat(visibleWindow.Take(3))
                        .ToArray();
                    LiveCaptionUpdate replay = resolver.Process(
                        string.Join(' ', rotated),
                        observedAt.AddMilliseconds(20));
                    Assert.IsEmpty(
                        replay.FinalizedSegments,
                        $"spoken={spoken}; " + string.Join(" | ",
                            replay.FinalizedSegments.Select(segment =>
                                $"{segment.Sequence}:{segment.Revision}:{segment.Text}")));

                    LiveCaptionUpdate restored = resolver.Process(
                        string.Join(' ', visibleWindow),
                        observedAt.AddMilliseconds(40));
                    Assert.IsEmpty(restored.FinalizedSegments);
                }

                observedAt = observedAt.AddMilliseconds(100);
            }

            Assert.AreEqual(80, emitted.Count);
            Assert.AreEqual(80, emitted.Select(segment => segment.Id).Distinct().Count());
            Assert.AreEqual(80, emitted.Select(segment => segment.Sequence).Distinct().Count());
            Assert.IsLessThanOrEqualTo(
                LiveCaptionSegmentationThresholds.RecentLedgerCapacity,
                resolver.RecentLedgerCount);
        }

        [TestMethod]
        public void TwoGenuineEqualOccurrencesRemainDistinctWhenWindowReorders()
        {
            var resolver = new LiveCaptionIdentityResolver();
            DateTimeOffset start = Start();
            LiveCaptionSegment first = resolver.Process(A, start)
                .FinalizedSegments.Single();
            LiveCaptionSegment draft = resolver.Process(
                "Alpha sentence",
                start.AddMilliseconds(100)).DraftSegment!;
            LiveCaptionSegment second = resolver.Process(
                A,
                start.AddMilliseconds(200)).FinalizedSegments.Single();

            LiveCaptionUpdate reordered = resolver.Process(
                $"{A} {A}",
                start.AddMilliseconds(300));

            Assert.AreEqual(draft.Id, second.Id);
            Assert.AreNotEqual(first.Id, second.Id);
            Assert.IsEmpty(reordered.FinalizedSegments);
            Assert.HasCount(2, reordered.WindowSegmentIds);
            Assert.AreEqual(2, reordered.WindowSegmentIds.Distinct().Count());
            CollectionAssert.AreEquivalent(
                new[] { first.Id, second.Id },
                reordered.WindowSegmentIds.ToArray());
        }

        [TestMethod]
        public void PendingCandidatesAndSnapshotHistoryRemainBounded()
        {
            var resolver = new LiveCaptionIdentityResolver();
            DateTimeOffset observedAt = Start();
            string[] historical =
            [
                "Amber lecture sentence is complete.",
                "Birch classroom sentence is complete.",
                "Cedar discussion sentence is complete.",
                "Denim example sentence is complete.",
                "Elm practice sentence is complete.",
                "Flint review sentence is complete.",
                "Grove study sentence is complete.",
                "Hazel summary sentence is complete.",
                "Ivory topic sentence is complete.",
                "Juniper lesson sentence is complete."
            ];

            foreach (string text in historical)
            {
                resolver.Process(text, observedAt);
                observedAt = observedAt.AddMilliseconds(10);
            }

            for (int index = 0; index < historical.Length; index++)
            {
                resolver.Process(
                    $"A genuinely new marker {index} appears now.",
                    observedAt);
                observedAt = observedAt.AddMilliseconds(10);
                LiveCaptionUpdate ambiguous = resolver.Process(
                    historical[index],
                    observedAt);
                Assert.IsEmpty(ambiguous.FinalizedSegments);
                observedAt = observedAt.AddMilliseconds(10);
            }

            Assert.AreEqual(
                LiveCaptionSegmentationThresholds.PendingCandidateCapacity,
                resolver.PendingCandidateCount);
            Assert.IsLessThanOrEqualTo(
                LiveCaptionSegmentationThresholds.RecentSnapshotCapacity,
                resolver.RecentSnapshotCount);
        }

        [TestMethod]
        public void ElapsedTimeNeverPromotesAnAmbiguousRepeat()
        {
            var resolver = new LiveCaptionIdentityResolver();
            DateTimeOffset start = Start();
            resolver.Process(A, start);
            resolver.Process(B, start.AddSeconds(1));
            LiveCaptionUpdate pending = resolver.Process(
                A,
                start.AddSeconds(2));

            LiveCaptionUpdate muchLater = resolver.Process(
                A,
                start.AddMinutes(1));

            Assert.HasCount(1, pending.PendingCandidates);
            Assert.IsEmpty(muchLater.FinalizedSegments);
            Assert.HasCount(1, muchLater.PendingCandidates);
        }

        [TestMethod]
        public void ExpiredPendingEvidenceClosesWithoutCreatingSpeech()
        {
            var resolver = new LiveCaptionIdentityResolver();
            DateTimeOffset start = Start();
            resolver.Process(A, start);
            resolver.Process(B, start.AddSeconds(1));
            LiveCaptionUpdate pending = resolver.Process(
                A,
                start.AddSeconds(2));

            LiveCaptionUpdate expired = resolver.Process(
                A,
                start.AddSeconds(2) +
                LiveCaptionSegmentationThresholds.RecentLedgerRetention +
                TimeSpan.FromMilliseconds(1));

            Assert.HasCount(1, pending.PendingCandidates);
            Assert.IsEmpty(expired.FinalizedSegments);
            Assert.IsEmpty(expired.PendingCandidates);
        }

        [TestMethod]
        public void PendingRepeatBecomesANewIdentityWhenItsDraftGrows()
        {
            var resolver = new LiveCaptionIdentityResolver();
            DateTimeOffset start = Start();
            LiveCaptionSegment first = resolver.Process(A, start)
                .FinalizedSegments.Single();
            resolver.Process(B, start.AddMilliseconds(100));
            LiveCaptionUpdate ambiguous = resolver.Process(
                A,
                start.AddMilliseconds(200));
            Guid pendingId = ambiguous.PendingCandidates.Single().CandidateId;

            LiveCaptionSegment draft = resolver.Process(
                "Alpha sentence is complete and continues growing",
                start.AddMilliseconds(300)).DraftSegment!;
            LiveCaptionSegment final = resolver.Process(
                "Alpha sentence is complete and continues growing.",
                start.AddMilliseconds(400)).FinalizedSegments.Single();

            Assert.AreEqual(pendingId, draft.Id);
            Assert.AreEqual(pendingId, final.Id);
            Assert.AreNotEqual(first.Id, final.Id);
            Assert.AreEqual(
                ambiguous.PendingCandidates[0].FirstObservedAt,
                final.CapturedAt);
            Assert.AreEqual(0, resolver.PendingCandidateCount);
        }

        [TestMethod]
        public void ReconnectBaselineDoesNotReplayVisibleWindowOrPendingEvidence()
        {
            var resolver = new LiveCaptionIdentityResolver();
            DateTimeOffset start = Start();
            resolver.Process(A, start);
            resolver.Process(B, start.AddMilliseconds(100));
            resolver.Process(A, start.AddMilliseconds(200));
            Assert.AreEqual(1, resolver.PendingCandidateCount);

            resolver.StartFromCurrentSnapshot(
                $"{A} {B}",
                start.AddMilliseconds(250));
            LiveCaptionUpdate unchanged = resolver.Process(
                $"{A} {B}",
                start.AddMilliseconds(300));
            LiveCaptionUpdate forward = resolver.Process(
                $"{A} {B} Delta sentence is new after reconnect.",
                start.AddMilliseconds(400));

            Assert.IsEmpty(unchanged.FinalizedSegments);
            Assert.AreEqual(0, resolver.PendingCandidateCount);
            Assert.HasCount(1, forward.FinalizedSegments);
            Assert.AreEqual(
                "Delta sentence is new after reconnect.",
                forward.FinalizedSegments[0].Text);
        }

        private static DateTimeOffset Start()
        {
            return new DateTimeOffset(2026, 9, 14, 0, 3, 47, TimeSpan.Zero);
        }
    }
}
