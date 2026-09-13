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

        [TestMethod]
        public void ProvisionalCaptionUsesLiveSurfaceUntilOneCommittedRowArrives()
        {
            var viewModel = new TranscriptSessionViewModel();
            TranscriptSegment draft = Create(1, source: "One task") with
            {
                State = SegmentState.Draft,
                TranslatedText = null
            };
            viewModel.ApplySegment(draft);
            viewModel.ApplySegment(draft with
            {
                Revision = 1,
                SourceText = "One task, one resource"
            });

            Assert.HasCount(0, viewModel.Segments);
            Assert.AreEqual("One task, one resource", viewModel.LiveText);

            TranscriptSegment committed = draft with
            {
                Revision = 2,
                SourceText = "One task, one resource.",
                State = SegmentState.Committed
            };
            viewModel.ApplySegment(committed);
            viewModel.ClearDraft(committed.Id, committed.Revision);

            Assert.HasCount(1, viewModel.Segments);
            Assert.IsFalse(viewModel.HasDraft);
            Assert.AreEqual(committed.SourceText, viewModel.LiveText);
            Assert.IsTrue(viewModel.HasLiveCaption);
        }

        [TestMethod]
        public void DelayedDifferentIdentityCannotReplaceTheCurrentLiveSentence()
        {
            var viewModel = new TranscriptSessionViewModel();
            TranscriptSegment current = Create(2, source: "Current sentence") with
            {
                State = SegmentState.Draft,
                TranslatedText = null
            };
            viewModel.SetDraft(current);
            viewModel.SetDraft(Create(1, source: "Old sentence") with
            {
                State = SegmentState.Draft
            });

            Assert.AreEqual(current.Id, viewModel.DraftSegmentId);
            Assert.AreEqual(current.SourceText, viewModel.LiveText);
            Assert.IsFalse(viewModel.HasLiveTranslation);
        }

        [TestMethod]
        public void ClearingADraftDoesNotForgetItsLiveOrderingFrontier()
        {
            var viewModel = new TranscriptSessionViewModel();
            TranscriptSegment latest = Create(3, source: "Latest final.");
            viewModel.ApplySegment(latest);
            viewModel.SetDraft((TranscriptSegment?)null);
            viewModel.SetDraft(Create(2, source: "Delayed old draft") with
            {
                State = SegmentState.Draft
            });

            Assert.AreEqual(latest.SourceText, viewModel.LiveText);
            Assert.IsFalse(viewModel.HasDraft);
        }

        [TestMethod]
        public void ReconnectingAcceptsANewSequenceAndRejectsEarlierEpochCallbacks()
        {
            var viewModel = new TranscriptSessionViewModel();
            viewModel.SetDraft(Create(40, source: "Before reconnect") with
            {
                State = SegmentState.Draft,
                SessionId = 1,
                CaptureEpoch = 1
            });
            TranscriptSegment current = Create(1, source: "After reconnect") with
            {
                State = SegmentState.Draft,
                SessionId = 1,
                CaptureEpoch = 2
            };
            viewModel.SetDraft(current);
            viewModel.SetDraft(Create(41, source: "Delayed provider callback") with
            {
                State = SegmentState.Draft,
                SessionId = 1,
                CaptureEpoch = 1
            });

            Assert.AreEqual(current.Id, viewModel.DraftSegmentId);
            Assert.AreEqual(current.SourceText, viewModel.LiveText);
        }

        [TestMethod]
        public void ClassroomResetAllowsNewScopeButRejectsOldClassroomDraft()
        {
            var viewModel = new TranscriptSessionViewModel();
            TranscriptSegment old = Create(20, source: "Old classroom") with
            {
                State = SegmentState.Draft,
                SessionId = 1
            };
            viewModel.SetDraft(old);
            viewModel.ResetTimeline();
            TranscriptSegment current = Create(1, source: "New classroom") with
            {
                State = SegmentState.Draft,
                SessionId = 2
            };
            viewModel.SetDraft(current);
            viewModel.SetDraft(old);

            Assert.AreEqual(current.SourceText, viewModel.LiveText);
            Assert.AreEqual(current.Id, viewModel.DraftSegmentId);
        }

        [TestMethod]
        public void LoadedHistoryDoesNotBecomeLiveAudio()
        {
            var viewModel = new TranscriptSessionViewModel();
            viewModel.ApplySegment(Create(500, source: "Historical sentence"), updateLive: false);

            Assert.HasCount(1, viewModel.Segments);
            Assert.IsFalse(viewModel.HasLiveCaption);

            viewModel.SetDraft(Create(1, source: "Currently spoken") with
            {
                State = SegmentState.Draft
            });

            Assert.AreEqual("Currently spoken", viewModel.LiveText);
        }

        [TestMethod]
        public void NewSentenceCannotDisplayThePreviousDraftTranslation()
        {
            var viewModel = new TranscriptSessionViewModel();
            viewModel.SetDraft(Create(1, source: "First sentence") with
            {
                State = SegmentState.Draft,
                TranslatedText = "第一句"
            });
            viewModel.ApplySegment(Create(2, source: "Second sentence.") with
            {
                State = SegmentState.Committed,
                TranslatedText = null
            });

            Assert.AreEqual("Second sentence.", viewModel.LiveText);
            Assert.IsFalse(viewModel.HasLiveTranslation);
        }

        [TestMethod]
        public void ReopenedDraftDoesNotChangeTheClassroomRowUntilReadmitted()
        {
            var viewModel = new TranscriptSessionViewModel();
            TranscriptSegment final = Create(1, source: "Initial sentence.");
            viewModel.ApplySegment(final);
            viewModel.ApplySegment(final with
            {
                Revision = 1,
                State = SegmentState.Draft,
                SourceText = "Initial sentence continues",
                TranslatedText = null
            });

            Assert.HasCount(1, viewModel.Segments);
            Assert.AreEqual(final.SourceText, viewModel.Segments[0].SourceText);
            Assert.AreEqual("Initial sentence continues", viewModel.LiveText);

            viewModel.ApplySegment(final with
            {
                Revision = 2,
                SourceText = "Initial sentence continues to completion."
            });

            Assert.HasCount(1, viewModel.Segments);
            Assert.AreEqual(2, viewModel.Segments[0].Revision);
        }

        [TestMethod]
        public void DelayedSameRevisionCannotDowngradeACompletedTranslation()
        {
            var viewModel = new TranscriptSessionViewModel();
            TranscriptSegment translated = Create(1, source: "Completed sentence.");
            viewModel.ApplySegment(translated);
            viewModel.ApplySegment(translated with
            {
                State = SegmentState.Committed,
                TranslatedText = null
            });
            viewModel.SetDraft(translated with
            {
                State = SegmentState.Draft,
                TranslatedText = null
            });

            Assert.AreEqual(SegmentState.Translated, viewModel.Segments[0].State);
            Assert.AreEqual(translated.TranslatedText, viewModel.Segments[0].TranslatedText);
            Assert.AreEqual(translated.TranslatedText, viewModel.LiveTranslation);
            Assert.IsFalse(viewModel.HasDraft);
        }

        [TestMethod]
        public void FinalRevisionRejectsLateDraftTranslationBeforeTheClearCallback()
        {
            var viewModel = new TranscriptSessionViewModel();
            TranscriptSegment draft = Create(1) with
            {
                State = SegmentState.Draft,
                TranslatedText = null
            };
            viewModel.SetDraft(draft);
            viewModel.ApplySegment(draft with
            {
                Revision = 1,
                State = SegmentState.Translated,
                TranslatedText = "最终译文"
            });

            bool applied = viewModel.SetDraftTranslation(draft.Id, 0, "旧草稿译文");

            Assert.IsFalse(applied);
            Assert.AreEqual("最终译文", viewModel.LiveTranslation);
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
