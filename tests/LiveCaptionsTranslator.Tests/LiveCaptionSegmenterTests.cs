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
        public void SameSentenceSpokenTwiceGetsTwoSegmentIdentities()
        {
            var segmenter = new LiveCaptionSegmenter();
            LiveCaptionUpdate first = segmenter.Process("Hello.");

            LiveCaptionUpdate second = segmenter.Process("Hello. Hello.");

            Assert.HasCount(1, first.FinalizedSegments);
            Assert.HasCount(1, second.FinalizedSegments);
            Assert.AreNotEqual(
                first.FinalizedSegments[0].Id,
                second.FinalizedSegments[0].Id);
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
        public void ExplicitWindowAppendPreservesARepeatedUtterance()
        {
            var segmenter = new LiveCaptionSegmenter();
            LiveCaptionSegment first = segmenter.Process("Hello.")
                .FinalizedSegments.Single();

            LiveCaptionSegment repeated = segmenter.Process("Hello. Hello.")
                .FinalizedSegments.Single();

            Assert.AreNotEqual(first.Id, repeated.Id);
            Assert.AreEqual(2, repeated.Sequence);
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
        public void LedgerExpiryDoesNotTurnAnUnchangedSnapshotIntoNewSpeech()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            segmenter.Process("The same stable snapshot.", start);

            LiveCaptionUpdate repeated = segmenter.Process(
                "The same stable snapshot.",
                start + LiveCaptionSegmentationThresholds.RecentLedgerRetention +
                TimeSpan.FromSeconds(1));

            Assert.IsEmpty(repeated.FinalizedSegments);
            Assert.AreEqual(0, segmenter.RecentLedgerCount);
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
