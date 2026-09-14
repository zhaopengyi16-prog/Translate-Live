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
        public void UrlVersionAndMixedClosingPunctuationStayInTheirSentence()
        {
            var segmenter = new LiveCaptionSegmenter();

            LiveCaptionUpdate update = segmenter.Process(
                "Prof. Li said（see https://example.com/v3.14/docs。） 下一项。 ");

            CollectionAssert.AreEqual(
                new[]
                {
                    "Prof. Li said（see https://example.com/v3.14/docs。）",
                    "下一项。"
                },
                update.FinalizedSentences.ToArray());
        }

        [TestMethod]
        public void TerminalAbbreviationWaitsForItsContinuation()
        {
            var segmenter = new LiveCaptionSegmenter();

            LiveCaptionUpdate abbreviation = segmenter.Process("Dr.");
            LiveCaptionUpdate completed = segmenter.Process("Dr. Smith is here.");

            Assert.IsEmpty(abbreviation.FinalizedSentences);
            Assert.AreEqual("Dr.", abbreviation.DraftText);
            CollectionAssert.AreEqual(
                new[] { "Dr. Smith is here." },
                completed.FinalizedSentences.ToArray());
        }

        [TestMethod]
        public void TerminalNumberWaitsForDecimalOrVersionContinuation()
        {
            var segmenter = new LiveCaptionSegmenter();

            LiveCaptionUpdate number = segmenter.Process("Version 3.");
            LiveCaptionUpdate completed = segmenter.Process("Version 3.14 is current.");

            Assert.IsEmpty(number.FinalizedSentences);
            Assert.AreEqual("Version 3.", number.DraftText);
            CollectionAssert.AreEqual(
                new[] { "Version 3.14 is current." },
                completed.FinalizedSentences.ToArray());
        }

        [TestMethod]
        public void ClosingQuoteBelongsToTheSentenceItCloses()
        {
            var segmenter = new LiveCaptionSegmenter();

            LiveCaptionUpdate update = segmenter.Process(
                "He said \"the build is stable.\" Next topic.");

            CollectionAssert.AreEqual(
                new[] { "He said \"the build is stable.\"", "Next topic." },
                update.FinalizedSentences.ToArray());
            Assert.AreEqual(string.Empty, update.DraftText);
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
        public void RepeatedTailRowsFromAccessibilityWindowDoNotEmitDuplicateFinals()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 22, 10, 53, TimeSpan.Zero);
            const string firstText =
                "Yes, I'll chat with you, give you instant feedback on.";
            const string repeatedText = "I can speak with you.";

            LiveCaptionSegment first = segmenter.Process(firstText, start)
                .FinalizedSegments.Single();
            LiveCaptionSegment second = segmenter.Process(
                $"{firstText} {repeatedText}",
                start.AddSeconds(1)).FinalizedSegments.Single();
            LiveCaptionUpdate duplicatePair = segmenter.Process(
                $"{repeatedText} {repeatedText}",
                start.AddMilliseconds(1250));
            LiveCaptionUpdate duplicateTriple = segmenter.Process(
                $"{repeatedText} {repeatedText} {repeatedText}",
                start.AddMilliseconds(1500));

            Assert.AreNotEqual(first.Id, second.Id);
            Assert.IsEmpty(duplicatePair.FinalizedSegments);
            Assert.IsEmpty(
                duplicateTriple.FinalizedSegments,
                string.Join(" | ", duplicateTriple.FinalizedSegments.Select(
                    segment => $"{segment.Sequence}:{segment.Revision}:{segment.Text}")));
            Assert.AreEqual(second.Id, duplicatePair.CurrentSegment?.Id);
            Assert.AreEqual(second.Id, duplicateTriple.CurrentSegment?.Id);
            Assert.AreEqual(2, segmenter.RecentLedgerCount);
        }

        [TestMethod]
        public void RepeatedTailRowsInsideOneAccessibilitySnapshotEmitOnce()
        {
            var segmenter = new LiveCaptionSegmenter();
            var observedAt = new DateTimeOffset(
                2026,
                1,
                1,
                22,
                10,
                53,
                TimeSpan.Zero);

            LiveCaptionUpdate update = segmenter.Process(
                "A different sentence. I can speak with you. I can speak with you.",
                observedAt);

            Assert.HasCount(2, update.FinalizedSegments);
            CollectionAssert.AreEqual(
                new[] { "A different sentence.", "I can speak with you." },
                update.FinalizedSentences.ToArray());
            Assert.AreEqual(2, update.FinalizedSegments
                .Select(segment => segment.Id)
                .Distinct()
                .Count());
        }

        [TestMethod]
        public void InvisibleAccessibilityFormatCharactersDoNotChangeCaptionIdentityText()
        {
            Assert.AreEqual(
                "I can speak with you.",
                TextUtil.NormalizeCaptionWhitespace(
                    "\u200eI can speak with you.\u200f"));
        }

        [TestMethod]
        public void DraftAndFinalKeepOneStableIdentity()
        {
            var segmenter = new LiveCaptionSegmenter();
            LiveCaptionUpdate draft = segmenter.Process("A sentence is being recognized");

            LiveCaptionUpdate final = segmenter.Process("A sentence is being recognized.");

            Assert.IsNotNull(draft.DraftSegment);
            Assert.HasCount(1, final.FinalizedSegments);
            Assert.AreEqual(draft.DraftSegment.Id, final.FinalizedSegments[0].Id);
            Assert.IsTrue(
                final.FinalizedSegments[0].Revision > draft.DraftSegment.Revision);
        }

        [TestMethod]
        public void SameTextWithoutForwardEvidenceRemainsPendingEvenAfterTime()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            LiveCaptionUpdate first = segmenter.Process("Hello.", start);

            LiveCaptionUpdate second = segmenter.Process(
                "Hello. Hello.",
                start.AddMinutes(1));

            Assert.HasCount(1, first.FinalizedSegments);
            Assert.IsEmpty(
                second.FinalizedSegments,
                string.Join(" | ", second.FinalizedSegments.Select(
                    segment => $"{segment.Sequence}:{segment.Revision}:{segment.Text}")));
            Assert.HasCount(1, second.PendingCandidates);
            Assert.AreEqual(
                first.FinalizedSegments[0].Id,
                second.PendingCandidates[0].HistoricalSegmentId);
        }

        [TestMethod]
        public void ConfirmedAlternatingWindowRollbackKeepsOnlyTwoLogicalIdentities()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

            LiveCaptionUpdate first = segmenter.Process(
                "I already have something up my sleeve.",
                start);
            LiveCaptionUpdate longSecond = segmenter.Process(
                "Oh my God Keep that in mind for the.",
                start.AddSeconds(1));
            LiveCaptionUpdate shortenedSecond = segmenter.Process(
                "Oh my God.",
                start.AddSeconds(2));
            LiveCaptionUpdate oldFirst = segmenter.Process(
                "I already have something up my sleeve.",
                start.AddSeconds(3));
            LiveCaptionUpdate oldSecond = segmenter.Process(
                "Oh my God.",
                start.AddSeconds(4));

            LiveCaptionSegment a = first.FinalizedSegments.Single();
            LiveCaptionSegment b = longSecond.FinalizedSegments.Single();
            LiveCaptionSegment bRevision = shortenedSecond.FinalizedSegments.Single();
            Assert.AreEqual(b.Id, bRevision.Id);
            Assert.IsTrue(bRevision.Revision > b.Revision);
            Assert.IsEmpty(oldFirst.FinalizedSegments);
            Assert.IsEmpty(oldSecond.FinalizedSegments);

            Guid[] logicalIdentities = first.FinalizedSegments
                .Concat(longSecond.FinalizedSegments)
                .Concat(shortenedSecond.FinalizedSegments)
                .Concat(oldFirst.FinalizedSegments)
                .Concat(oldSecond.FinalizedSegments)
                .Select(segment => segment.Id)
                .Distinct()
                .ToArray();
            CollectionAssert.AreEquivalent(new[] { a.Id, b.Id }, logicalIdentities);
        }

        [TestMethod]
        public void GrowingPunctuatedRecognitionFromWorkspaceStaysOneLogicalSentence()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 9, 11, 23, 4, 4, TimeSpan.Zero);
            string[] snapshots =
            [
                "Hello, can you speak my English and I will tell you what.",
                "Hello, can you speak my English and I will tell you what is I will say.",
                "Hello can you speak my English and I will tell you what is I will say in next time and.",
                "Hello, can you speak my English and I will tell you what is I will say in next time and you must record what?",
                "Hello, can you speak my English and I will tell you what is I will say in next time and you must record what I see."
            ];
            var revisions = new List<LiveCaptionSegment>();

            for (int index = 0; index < snapshots.Length; index++)
            {
                revisions.AddRange(segmenter.Process(
                    snapshots[index],
                    start.AddSeconds(index * 2)).FinalizedSegments);
            }

            Assert.HasCount(snapshots.Length, revisions);
            Assert.AreEqual(1, revisions.Select(segment => segment.Id).Distinct().Count());
            CollectionAssert.AreEqual(
                Enumerable.Range(0, snapshots.Length).ToArray(),
                revisions.Select(segment => segment.Revision).ToArray());
            Assert.AreEqual(start, revisions[^1].CapturedAt);
            Assert.AreEqual(snapshots[^1], revisions[^1].Text);
        }

        [TestMethod]
        public void GrowingFinalWithIntermediateDraftKeepsIdentityAndRevisionHistory()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            const string firstText = "The example explains how the reading window behaves.";
            const string draftText = "The example explains how the reading window behaves while text arrives";
            const string finalText = draftText + " continuously.";
            LiveCaptionSegment first = segmenter.Process(firstText, start)
                .FinalizedSegments.Single();
            LiveCaptionSegment draft = segmenter.Process(draftText, start.AddSeconds(1))
                .DraftSegment!;
            LiveCaptionSegment final = segmenter.Process(finalText, start.AddSeconds(2))
                .FinalizedSegments.Single();

            Assert.AreEqual(first.Id, draft.Id);
            Assert.AreEqual(first.Id, final.Id);
            Assert.AreEqual(first.Sequence, final.Sequence);
            Assert.AreEqual(first.CapturedAt, final.CapturedAt);
            Assert.IsTrue(draft.Revision > first.Revision);
            Assert.IsTrue(final.Revision > draft.Revision);
            Assert.AreEqual(1, segmenter.RecentLedgerCount);

            LiveCaptionUpdate rollback = segmenter.Process(firstText, start.AddSeconds(3));
            Assert.IsEmpty(rollback.FinalizedSegments);
            Assert.AreEqual(finalText, rollback.CurrentText);
            Assert.AreEqual(final.Revision, rollback.CurrentSegment!.Revision);
        }

        [TestMethod]
        public void FinalizedContinuationRejectsStaleIntermediateDraftRevisions()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            const string firstText = "The example explains how the reading window behaves.";
            const string firstDraft =
                "The example explains how the reading window behaves while text arrives";
            const string secondDraft = firstDraft + " continuously";
            const string finalText = secondDraft + " in the viewport.";

            LiveCaptionSegment first = segmenter.Process(firstText, start)
                .FinalizedSegments.Single();
            LiveCaptionSegment draftOne = segmenter.Process(
                firstDraft,
                start.AddSeconds(1)).DraftSegment!;
            LiveCaptionSegment draftTwo = segmenter.Process(
                secondDraft,
                start.AddSeconds(2)).DraftSegment!;
            LiveCaptionSegment final = segmenter.Process(
                finalText,
                start.AddSeconds(3)).FinalizedSegments.Single();

            Assert.AreEqual(first.Id, draftOne.Id);
            Assert.AreEqual(first.Id, draftTwo.Id);
            Assert.AreEqual(first.Id, final.Id);

            LiveCaptionUpdate firstRollback = segmenter.Process(
                firstDraft,
                start.AddSeconds(4));
            LiveCaptionUpdate secondRollback = segmenter.Process(
                secondDraft,
                start.AddSeconds(5));

            Assert.IsNull(firstRollback.DraftSegment);
            Assert.IsEmpty(firstRollback.FinalizedSegments);
            Assert.AreEqual(final.Id, firstRollback.CurrentSegment?.Id);
            Assert.AreEqual(final.Revision, firstRollback.CurrentSegment?.Revision);
            Assert.AreEqual(finalText, firstRollback.CurrentText);
            Assert.IsNull(secondRollback.DraftSegment);
            Assert.IsEmpty(secondRollback.FinalizedSegments);
            Assert.AreEqual(final.Id, secondRollback.CurrentSegment?.Id);
            Assert.AreEqual(final.Revision, secondRollback.CurrentSegment?.Revision);
            Assert.AreEqual(finalText, secondRollback.CurrentText);
        }

        [TestMethod]
        public void PunctuationRemovalWithoutGrowthIsTreatedAsTailRollback()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            const string finalText =
                "The example explains how the reading window behaves.";
            LiveCaptionSegment final = segmenter.Process(finalText, start)
                .FinalizedSegments.Single();

            LiveCaptionUpdate rollback = segmenter.Process(
                finalText.TrimEnd('.'),
                start.AddSeconds(1));

            Assert.IsNull(rollback.DraftSegment);
            Assert.IsEmpty(rollback.FinalizedSegments);
            Assert.AreEqual(final.Id, rollback.CurrentSegment?.Id);
            Assert.AreEqual(final.Revision, rollback.CurrentSegment?.Revision);
            Assert.AreEqual(finalText, rollback.CurrentText);
        }

        [TestMethod]
        public void ShortFinalContinuesThroughRemovedPunctuationWithoutNewIdentity()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            LiveCaptionSegment first = segmenter.Process("And you know?", start)
                .FinalizedSegments.Single();

            LiveCaptionSegment draft = segmenter.Process(
                "And you know what",
                start.AddSeconds(1)).DraftSegment!;
            LiveCaptionSegment final = segmenter.Process(
                "And you know what?",
                start.AddSeconds(2)).FinalizedSegments.Single();

            Assert.AreEqual(first.Id, draft.Id);
            Assert.AreEqual(first.Id, final.Id);
            Assert.AreEqual(first.Sequence, final.Sequence);
            Assert.AreEqual(first.CapturedAt, final.CapturedAt);
            Assert.IsTrue(final.Revision > first.Revision);
        }

        [TestMethod]
        public void ShortenedTailDraftReturningToFinalDoesNotDuplicate()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            const string finalText = "Can him remind your voice?";
            LiveCaptionSegment first = segmenter.Process(finalText, start)
                .FinalizedSegments.Single();

            LiveCaptionUpdate shortened = segmenter.Process(
                "Can him remind your",
                start.AddSeconds(1));
            LiveCaptionUpdate restored = segmenter.Process(
                finalText,
                start.AddSeconds(2));

            Assert.IsNull(shortened.DraftSegment);
            Assert.IsEmpty(shortened.FinalizedSegments);
            Assert.AreEqual(first.Id, shortened.CurrentSegment?.Id);
            Assert.AreEqual(finalText, shortened.CurrentText);
            Assert.IsNull(restored.DraftSegment);
            Assert.IsEmpty(restored.FinalizedSegments);
            Assert.AreEqual(first.Id, restored.CurrentSegment?.Id);
            Assert.AreEqual(finalText, restored.CurrentText);
        }

        [TestMethod]
        public void ShortTailGrowthOutsideEvidenceWindowRetainsIdentity()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            LiveCaptionSegment first = segmenter.Process("And you know?", start)
                .FinalizedSegments.Single();

            LiveCaptionSegment laterDraft = segmenter.Process(
                "And you know what",
                start + LiveCaptionSegmentationThresholds.ShortTailRevisionWindow +
                    TimeSpan.FromMilliseconds(1)).DraftSegment!;

            Assert.AreEqual(first.Id, laterDraft.Id);
            Assert.AreEqual(first.Sequence, laterDraft.Sequence);
            Assert.AreEqual(first.CapturedAt, laterDraft.CapturedAt);
        }

        [TestMethod]
        public void RapidContinuousLongSpeechKeepsOneIdentityAcrossProvisionalStops()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            string[] snapshots =
            [
                "And you know?",
                "And you know what",
                "And you know what?",
                "And you know what I",
                "And you know what I mean?",
                "And you know what I",
                "And you know what I mean?",
                "And you know what I mean when the captions arrive",
                "And you know what I mean when the captions arrive continuously?",
                "And you know what I mean when the captions arrive continuously during a lecture",
                "And you know what I mean when the captions arrive continuously during a lecture and the recognizer revises punctuation?",
                "And you know what I mean when the captions arrive continuously during a lecture and the recognizer revises punctuation while the speaker keeps talking",
                "And you know what I mean when the captions arrive continuously during a lecture and the recognizer revises punctuation while the speaker keeps talking without creating duplicate rows."
            ];
            var updates = new List<LiveCaptionUpdate>();

            for (int index = 0; index < snapshots.Length; index++)
            {
                updates.Add(segmenter.Process(
                    snapshots[index],
                    start.AddMilliseconds(index * 500)));
            }

            Guid[] identities = updates
                .Select(update => update.CurrentSegment?.Id)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToArray();
            LiveCaptionSegment final = updates[^1].FinalizedSegments.Single();

            Assert.HasCount(1, identities);
            Assert.AreEqual(identities[0], final.Id);
            Assert.AreEqual(start, final.CapturedAt);
            Assert.AreEqual(snapshots[^1], final.Text);
            Assert.IsTrue(final.Revision >= 8);
        }

        [TestMethod]
        public void DirectAppendedLongDraftWithoutTrajectoryReopensPreviousFinal()
        {
            var segmenter = new LiveCaptionSegmenter();
            const string text = "The example explains how the reading window behaves.";
            const string draftText = "The example explains how the reading window behaves differently";
            LiveCaptionSegment first = segmenter.Process(text).FinalizedSegments.Single();
            LiveCaptionSegment draft = segmenter.Process(text + " " + draftText).DraftSegment!;
            LiveCaptionSegment second = segmenter.Process(text + " " + draftText + ".")
                .FinalizedSegments.Single();

            Assert.AreEqual(first.Id, draft.Id);
            Assert.AreEqual(draft.Id, second.Id);
            Assert.AreEqual(first.Sequence, second.Sequence);
            Assert.AreEqual(first.CapturedAt, second.CapturedAt);
        }

        [TestMethod]
        public void NewShortDraftGrowthPreservesARepeatedLongUtterance()
        {
            var segmenter = new LiveCaptionSegmenter();
            const string text = "The example explains how the reading window behaves.";
            LiveCaptionSegment first = segmenter.Process(text).FinalizedSegments.Single();
            LiveCaptionSegment draft = segmenter.Process("The example").DraftSegment!;
            segmenter.Process("The example explains how the reading window behaves");
            LiveCaptionSegment repeated = segmenter.Process(text).FinalizedSegments.Single();

            Assert.AreNotEqual(first.Id, draft.Id);
            Assert.AreEqual(draft.Id, repeated.Id);
        }

        [TestMethod]
        public void LongSharedOpeningWithDifferentMeaningStartsANewSentence()
        {
            var segmenter = new LiveCaptionSegmenter();
            LiveCaptionSegment first = segmenter.Process(
                "Hello, can you speak my English about the weather today.")
                .FinalizedSegments.Single();

            LiveCaptionSegment second = segmenter.Process(
                "Hello, can you speak my English about architecture tomorrow.")
                .FinalizedSegments.Single();

            Assert.AreNotEqual(first.Id, second.Id);
            Assert.AreEqual(2, second.Sequence);
        }

        [TestMethod]
        public void OldLongRevisionReturningAfterShorteningIsIgnored()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            LiveCaptionSegment original = segmenter.Process(
                "Oh my God Keep that in mind for the.",
                start).FinalizedSegments.Single();
            LiveCaptionSegment shortened = segmenter.Process(
                "Oh my God.",
                start.AddSeconds(1)).FinalizedSegments.Single();

            LiveCaptionUpdate staleLongVersion = segmenter.Process(
                "Oh my God Keep that in mind for the.",
                start.AddSeconds(2));

            Assert.AreEqual(original.Id, shortened.Id);
            Assert.IsTrue(shortened.Revision > original.Revision);
            Assert.IsEmpty(staleLongVersion.FinalizedSegments);
            Assert.AreEqual(shortened.Id, staleLongVersion.CurrentSegment?.Id);
            Assert.AreEqual("Oh my God.", staleLongVersion.CurrentText);
        }

        [TestMethod]
        public void RepeatedUtteranceAfterNewDraftGrowthGetsANewIdentity()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            LiveCaptionSegment first = segmenter.Process("Hello.", start)
                .FinalizedSegments.Single();
            LiveCaptionSegment draft = segmenter.Process(
                "Hello. Hel",
                start.AddSeconds(1)).DraftSegment!;

            LiveCaptionSegment repeated = segmenter.Process(
                "Hello. Hello.",
                start.AddSeconds(2)).FinalizedSegments.Single();

            Assert.AreEqual(draft.Id, repeated.Id);
            Assert.AreNotEqual(first.Id, repeated.Id);
            Assert.IsTrue(repeated.Revision > draft.Revision);
        }

        [TestMethod]
        public void CompletedAndHistoricalDraftAlternationDoesNotMultiplyIdentities()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            const string text = "I can speak with you.";
            LiveCaptionSegment first = segmenter.Process(text, start)
                .FinalizedSegments.Single();

            var emitted = new List<LiveCaptionSegment> { first };
            for (int cycle = 0; cycle < 20; cycle++)
            {
                LiveCaptionUpdate draftReplay = segmenter.Process(
                    text + " I can speak with you",
                    start.AddMilliseconds(cycle * 100 + 25));
                LiveCaptionUpdate finalReplay = segmenter.Process(
                    text + " " + text,
                    start.AddMilliseconds(cycle * 100 + 50));
                emitted.AddRange(draftReplay.FinalizedSegments);
                emitted.AddRange(finalReplay.FinalizedSegments);
            }

            Assert.AreEqual(1, emitted.Select(segment => segment.Id).Distinct().Count());
            Assert.AreEqual(1, emitted.Select(segment => segment.Sequence).Distinct().Count());
            Assert.AreEqual(first.Id, segmenter.Process(
                text,
                start.AddSeconds(3)).CurrentSegment?.Id);
        }

        [TestMethod]
        public void FullWindowAppendAfterDifferentSentenceRemainsPendingWithoutDraftEvidence()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            LiveCaptionUpdate firstWindow = segmenter.Process(
                "Hello. A different sentence.",
                start);
            LiveCaptionSegment first = firstWindow.FinalizedSegments[0];

            LiveCaptionUpdate repeated = segmenter.Process(
                "Hello. A different sentence. Hello.",
                start.AddMilliseconds(200));

            Assert.IsEmpty(repeated.FinalizedSegments);
            Assert.HasCount(1, repeated.PendingCandidates);
            Assert.AreEqual(first.Id, repeated.PendingCandidates[0].HistoricalSegmentId);
            CollectionAssert.AreEqual(
                firstWindow.WindowSegmentIds.ToArray(),
                repeated.WindowSegmentIds.ToArray());
        }

        [TestMethod]
        public void VisibleWindowEvidenceDoesNotExpireIntoANewIdentity()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            LiveCaptionUpdate firstWindow = segmenter.Process(
                "Hello. A different sentence.",
                start);
            LiveCaptionSegment first = firstWindow.FinalizedSegments[0];

            segmenter.Process(
                "Hello. A different sentence.",
                start + LiveCaptionSegmentationThresholds.RecentLedgerRetention +
                    TimeSpan.FromMilliseconds(1));
            LiveCaptionUpdate replay = segmenter.Process(
                "Hello. A different sentence. Hello.",
                start + LiveCaptionSegmentationThresholds.RecentLedgerRetention +
                    TimeSpan.FromMilliseconds(2));

            Assert.IsEmpty(replay.FinalizedSegments);
            Assert.HasCount(1, replay.PendingCandidates);
            Assert.AreEqual(first.Id, replay.PendingCandidates[0].HistoricalSegmentId);
            Assert.AreEqual(2, segmenter.RecentLedgerCount);
        }

        [TestMethod]
        public void AdjacentProgressiveFinalsRemainOneLogicalIdentity()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            const string firstText =
                "Everything else kind of fades into the background.";
            const string revisedText =
                "Everything else kind of fades into the background during focused work.";
            LiveCaptionSegment first = segmenter.Process(firstText, start)
                .FinalizedSegments.Single();

            LiveCaptionUpdate update = segmenter.Process(
                firstText + " " + revisedText,
                start.AddMilliseconds(200));

            Assert.HasCount(1, update.FinalizedSegments);
            Assert.AreEqual(first.Id, update.FinalizedSegments[0].Id);
            Assert.AreEqual(first.Sequence, update.FinalizedSegments[0].Sequence);
            Assert.AreEqual(first.CapturedAt, update.FinalizedSegments[0].CapturedAt);
            Assert.AreEqual(revisedText, update.FinalizedSegments[0].Text);
            Assert.HasCount(1, update.WindowSegmentIds);
        }

        [TestMethod]
        public void VisibleFinalAndDirectGrowingDraftRemainOneLogicalIdentity()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            const string firstText = "And you know?";
            const string growingText = "And you know what happens next";
            const string finalText = "And you know what happens next?";
            LiveCaptionSegment first = segmenter.Process(firstText, start)
                .FinalizedSegments.Single();

            LiveCaptionUpdate growing = segmenter.Process(
                firstText + " " + growingText,
                start.AddMilliseconds(200));
            LiveCaptionUpdate final = segmenter.Process(
                firstText + " " + finalText,
                start.AddMilliseconds(400));

            Assert.AreEqual(first.Id, growing.DraftSegment?.Id);
            Assert.AreEqual(first.Sequence, growing.DraftSegment?.Sequence);
            Assert.HasCount(1, final.FinalizedSegments);
            Assert.AreEqual(first.Id, final.FinalizedSegments[0].Id);
            Assert.AreEqual(first.CapturedAt, final.FinalizedSegments[0].CapturedAt);
            Assert.HasCount(1, final.WindowSegmentIds);
        }

        [TestMethod]
        public void IsolatedAThenBThenAWithoutForwardEvidenceRemainsPending()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            LiveCaptionSegment first = segmenter.Process(
                "I can speak with you.",
                start).FinalizedSegments.Single();
            LiveCaptionSegment middle = segmenter.Process(
                "A different sentence.",
                start.AddMilliseconds(500)).FinalizedSegments.Single();

            LiveCaptionUpdate ambiguous = segmenter.Process(
                "I can speak with you.",
                start.AddSeconds(1));

            Assert.AreNotEqual(first.Id, middle.Id);
            Assert.IsEmpty(ambiguous.FinalizedSegments);
            Assert.HasCount(1, ambiguous.PendingCandidates);
            Assert.AreEqual(
                first.Id,
                ambiguous.PendingCandidates[0].HistoricalSegmentId);
        }

        [TestMethod]
        public void RollbackFollowedByANewSentenceResumesForwardProgress()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            LiveCaptionSegment first = segmenter.Process("First sentence.", start)
                .FinalizedSegments.Single();
            LiveCaptionSegment second = segmenter.Process(
                "Second sentence is longer here.",
                start.AddSeconds(1)).FinalizedSegments.Single();
            LiveCaptionSegment secondRevision = segmenter.Process(
                "Second sentence.",
                start.AddSeconds(2)).FinalizedSegments.Single();
            Assert.IsEmpty(segmenter.Process(
                "First sentence.",
                start.AddSeconds(3)).FinalizedSegments);

            LiveCaptionUpdate forward = segmenter.Process(
                "Second sentence. Third sentence.",
                start.AddSeconds(4));

            Assert.HasCount(1, forward.FinalizedSegments);
            Assert.AreEqual("Third sentence.", forward.FinalizedSegments[0].Text);
            Assert.AreNotEqual(first.Id, forward.FinalizedSegments[0].Id);
            Assert.AreNotEqual(second.Id, forward.FinalizedSegments[0].Id);
            Assert.AreEqual(second.Id, secondRevision.Id);
        }

        [TestMethod]
        public void RevisionKeepsTheFirstRecognitionTime()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            LiveCaptionSegment draft = segmenter.Process(
                "A sentence is being recognized",
                start).DraftSegment!;

            LiveCaptionSegment final = segmenter.Process(
                "A sentence is being recognized.",
                start.AddSeconds(5)).FinalizedSegments.Single();

            Assert.AreEqual(start, draft.CapturedAt);
            Assert.AreEqual(start, final.CapturedAt);
            Assert.AreEqual(draft.Id, final.Id);
        }

        [TestMethod]
        public void RecentLedgerIsCapacityBounded()
        {
            var segmenter = new LiveCaptionSegmenter();
            var observedAt = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

            for (int index = 0;
                 index < LiveCaptionSegmentationThresholds.RecentLedgerCapacity + 8;
                 index++)
            {
                string marker = Guid.NewGuid().ToString("N");
                LiveCaptionUpdate update = segmenter.Process(
                    $"Utterance {marker} finishes now.",
                    observedAt.AddSeconds(index));
                Assert.HasCount(1, update.FinalizedSegments);
                Assert.IsTrue(segmenter.RecentLedgerCount <=
                              LiveCaptionSegmentationThresholds.RecentLedgerCapacity);
            }

            Assert.AreEqual(
                LiveCaptionSegmentationThresholds.RecentLedgerCapacity,
                segmenter.RecentLedgerCount);
        }

        [TestMethod]
        public void UnchangedVisibleSnapshotRefreshesLedgerWithoutNewSpeech()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            segmenter.Process("The same stable snapshot.", start);

            LiveCaptionUpdate repeated = segmenter.Process(
                "The same stable snapshot.",
                start + LiveCaptionSegmentationThresholds.RecentLedgerRetention +
                TimeSpan.FromSeconds(1));

            Assert.IsEmpty(repeated.FinalizedSegments);
            Assert.AreEqual(1, segmenter.RecentLedgerCount);
        }

        [TestMethod]
        public void ResetClearsLedgerAndSeedsTheNextWindowAsBaseline()
        {
            var segmenter = new LiveCaptionSegmenter();
            segmenter.Process("Previous class sentence.");
            Assert.AreEqual(1, segmenter.RecentLedgerCount);

            segmenter.Reset();
            LiveCaptionUpdate baseline = segmenter.Process("Previous class sentence.");
            LiveCaptionUpdate next = segmenter.Process(
                "Previous class sentence. Current class sentence.");

            Assert.IsEmpty(baseline.FinalizedSegments);
            CollectionAssert.AreEqual(
                new[] { "Current class sentence." },
                next.FinalizedSentences.ToArray());
        }

        [TestMethod]
        public void StableTimeIsMeasuredWithoutFinalizingUnpunctuatedSpeech()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            LiveCaptionUpdate first = segmenter.Process(
                "A continuous explanation without sentence punctuation",
                start);

            LiveCaptionUpdate repeated = segmenter.Process(
                "A continuous explanation without sentence punctuation",
                start + TimeSpan.FromMilliseconds(700));

            Assert.AreEqual(first.DraftSegment?.Id, repeated.DraftSegment?.Id);
            Assert.AreEqual(TimeSpan.FromMilliseconds(700), repeated.DraftStableFor);
            Assert.IsEmpty(repeated.FinalizedSegments);
        }

        [TestMethod]
        public void LongUnpunctuatedSpeechSplitsAtNaturalBoundaryWithoutLosingText()
        {
            var segmenter = new LiveCaptionSegmenter();
            string firstClause = string.Join(' ', Enumerable.Repeat(
                "the lecture keeps moving",
                6));
            string secondClause = string.Join(' ', Enumerable.Repeat(
                "while the transcript remains readable",
                5));
            string snapshot = $"{firstClause}, {secondClause}";

            LiveCaptionUpdate update = segmenter.Process(snapshot);

            Assert.IsNotEmpty(update.FinalizedSegments);
            Assert.IsTrue(update.FinalizedSegments.Any(segment => segment.Text.EndsWith(',')));
            Assert.IsTrue(update.FinalizedSegments.All(segment =>
                segment.Text.Length <=
                LiveCaptionSegmentationThresholds.LongDraftHardLimitChars));
            Assert.IsNotNull(update.DraftSegment);
            Assert.AreEqual(
                TextUtil.NormalizeCaptionWhitespace(snapshot),
                TextUtil.NormalizeCaptionWhitespace(
                    $"{string.Join(' ', update.FinalizedSentences)} {update.DraftText}"));
        }

        [TestMethod]
        public void LongUnpunctuatedSpeechWithoutCommaSplitsAtAWordBoundary()
        {
            var segmenter = new LiveCaptionSegmenter();
            string snapshot = string.Join(' ', Enumerable.Repeat(
                "continuous explanation stays associated with its source",
                7));

            LiveCaptionUpdate update = segmenter.Process(snapshot);

            Assert.IsNotEmpty(update.FinalizedSegments);
            Assert.IsTrue(update.FinalizedSegments.All(segment =>
                !char.IsWhiteSpace(segment.Text[^1]) &&
                segment.Text.Length <=
                LiveCaptionSegmentationThresholds.LongDraftHardLimitChars));
            Assert.IsNotNull(update.DraftSegment);
            Assert.AreEqual(
                TextUtil.NormalizeCaptionWhitespace(snapshot),
                TextUtil.NormalizeCaptionWhitespace(
                    $"{string.Join(' ', update.FinalizedSentences)} {update.DraftText}"));
        }

        [TestMethod]
        public void FirstPostEnableSnapshotMustNotBecomeTheNewClassroomBaseline()
        {
            var segmenter = new LiveCaptionSegmenter(splitLongDrafts: false);
            DateTimeOffset start = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);
            segmenter.StartFromCurrentSnapshot("Old visible sentence.", start);

            // Classroom startup now keeps the pre-enable baseline instead of
            // resetting again after the microphone has become active.
            LiveCaptionUpdate firstMicrophoneSnapshot = segmenter.Process(
                "Old visible sentence. The microphone is working.",
                start.AddSeconds(1));

            CollectionAssert.AreEqual(
                new[] { "The microphone is working." },
                firstMicrophoneSnapshot.FinalizedSentences.ToArray());
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
        public void VeryShortTruncatedTailAtWindowHeadIsNotReemitted()
        {
            var segmenter = new LiveCaptionSegmenter();
            segmenter.Process("The explanation ends with the word so.");

            LiveCaptionUpdate update = segmenter.Process("So. The next topic begins.");

            CollectionAssert.AreEqual(
                new[] { "The next topic begins." },
                update.FinalizedSentences.ToArray());
        }

        [TestMethod]
        public void WindowHeadTruncationKeepsTheFullLogicalSentenceText()
        {
            var segmenter = new LiveCaptionSegmenter();
            LiveCaptionSegment original = segmenter.Process(
                "The API returns exactly one stable result.")
                .FinalizedSegments.Single();

            LiveCaptionUpdate truncated = segmenter.Process("one stable result.");

            Assert.IsEmpty(truncated.FinalizedSegments);
            Assert.AreEqual(original.Id, truncated.CurrentSegment?.Id);
            Assert.AreEqual(original.Text, truncated.CurrentText);
        }

        [TestMethod]
        public void MostRecentFinalCorrectionIsEmittedAsOneRevision()
        {
            var segmenter = new LiveCaptionSegmenter();
            LiveCaptionSegment original = segmenter.Process("The model is fast.")
                .FinalizedSegments.Single();

            LiveCaptionUpdate update = segmenter.Process("The model is faster.");

            CollectionAssert.AreEqual(
                new[] { "The model is faster." },
                update.FinalizedSentences.ToArray());
            Assert.AreEqual(original.Id, update.FinalizedSegments.Single().Id);
            Assert.IsTrue(update.FinalizedSegments.Single().Revision > original.Revision);
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
