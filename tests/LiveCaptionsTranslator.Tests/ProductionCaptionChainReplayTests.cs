using System.Collections.Concurrent;

using Microsoft.Data.Sqlite;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.services;
using LiveCaptionsTranslator.utils;
using LiveCaptionsTranslator.viewmodels;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class ProductionCaptionChainReplayTests
    {
        [TestMethod]
        public async Task AlternatingWindowRollbackPersistsTwoLogicalSentences()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "LectureCopilot.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "history.db"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            try
            {
                await CreateSchemaAsync(connectionString);
                var repository = new LectureReviewRepository(connectionString);
                LectureSessionEntry session = await repository.CreateSessionAsync(
                    "隔离回放",
                    "",
                    "Fake",
                    "zh-CN");
                TranslationSegmentPersistence persistence =
                    CreatePersistence(repository);

                int persistedCallbacks = 0;
                var queue = new TranslationTaskQueue(async (result, token) =>
                {
                    TranslationTaskIdentity identity = result.Identity!;
                    await persistence.UpsertAsync(
                        new TranslationPersistenceRequest(
                            result.SessionId!.Value,
                            identity,
                            result.OriginalText,
                            result.TranslatedText,
                            result.TargetLanguage,
                            result.ApiName),
                        token);
                    Interlocked.Increment(ref persistedCallbacks);
                });
                var segmenter = new LiveCaptionSegmenter();
                var emitted = new List<LiveCaptionSegment>();
                int translationCalls = 0;
                DateTimeOffset start = new(2026, 9, 11, 14, 13, 29, TimeSpan.Zero);
                (string Text, DateTimeOffset At)[] replay =
                [
                    ("I already have something up my sleeve.", start),
                    ("Oh my God Keep that in mind for the.", start.AddSeconds(4)),
                    ("Oh my God.", start.AddSeconds(5)),
                    ("I already have something up my sleeve.", start.AddSeconds(6)),
                    ("Oh my God.", start.AddSeconds(7))
                ];

                foreach ((string snapshot, DateTimeOffset observedAt) in replay)
                {
                    LiveCaptionUpdate update = segmenter.Process(snapshot, observedAt);
                    foreach (LiveCaptionSegment segment in update.FinalizedSegments)
                    {
                        emitted.Add(segment);
                        queue.Enqueue(
                            (_, _) =>
                            {
                                Interlocked.Increment(ref translationCalls);
                                return Task.FromResult<(string, bool)>(
                                    ($"译文 revision {segment.Revision}", true));
                            },
                            segment.Text,
                            "Fake",
                            session.Id,
                            "zh-CN",
                            Identity(segment),
                            waitForPersistence: true);
                        await queue.ReadLatestResultAsync()
                            .AsTask()
                            .WaitAsync(TimeSpan.FromSeconds(2));
                    }
                }

                await WaitUntilAsync(() => Volatile.Read(ref persistedCallbacks) == 3);
                await queue.WaitForPersistenceAsync().WaitAsync(TimeSpan.FromSeconds(2));
                List<PersistedRow> rows = await LoadRowsAsync(connectionString, session.Id);

                Assert.AreEqual(3, emitted.Count, "A、B 及 B 的短句修订应产生三次修订事件。");
                Assert.AreEqual(2, emitted.Select(item => item.Id).Distinct().Count());
                Assert.AreEqual(emitted[1].Id, emitted[2].Id);
                Assert.AreEqual(emitted[1].Revision + 1, emitted[2].Revision);
                Assert.AreEqual(3, translationCalls, "旧 A/旧 B 回滚帧不得再次进入翻译队列。");
                Assert.AreEqual(2, rows.Count, "每个逻辑句子只能保留一条数据库记录。");
                Assert.AreEqual(replay[0].At.ToUnixTimeSeconds(), rows[0].CapturedAtUnix);
                Assert.AreEqual(replay[1].At.ToUnixTimeSeconds(), rows[1].CapturedAtUnix);
                Assert.AreEqual(replay[0].Text, rows[0].SourceText);
                Assert.AreEqual(replay[2].Text, rows[1].SourceText);
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public async Task RapidIdenticalFinalCandidatesAreAdmittedOnceAcrossProductionChain()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "LectureCopilot.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "history.db"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            try
            {
                await CreateSchemaAsync(connectionString);
                var repository = new LectureReviewRepository(connectionString);
                LectureSessionEntry session = await repository.CreateSessionAsync(
                    "同秒重复隔离回放",
                    "",
                    "Fake",
                    "zh-CN");
                TranslationSegmentPersistence persistence =
                    CreatePersistence(repository);
                int persistedCallbacks = 0;
                int translationCalls = 0;
                var queue = new TranslationTaskQueue(async (result, token) =>
                {
                    await persistence.UpsertAsync(
                        new TranslationPersistenceRequest(
                            result.SessionId!.Value,
                            result.Identity!,
                            result.OriginalText,
                            result.TranslatedText,
                            result.TargetLanguage,
                            result.ApiName),
                        token);
                    Interlocked.Increment(ref persistedCallbacks);
                });
                var viewModel = new TranscriptSessionViewModel();
                var resolver = new LiveCaptionIdentityResolver();
                DateTimeOffset start = new(2026, 9, 13, 23, 19, 5, TimeSpan.Zero);
                const string repeatedText =
                    "Everything else kind of face into the background.";
                (string Text, DateTimeOffset At)[] snapshots =
                [
                    (repeatedText, start),
                    ($"{repeatedText} {repeatedText}",
                        start.AddMilliseconds(180)),
                    ($"{repeatedText} {repeatedText} {repeatedText}",
                        start.AddMilliseconds(420)),
                    ($"{repeatedText} {repeatedText} {repeatedText} {repeatedText}",
                        start.AddMilliseconds(1050))
                ];
                int admittedCandidates = 0;

                foreach ((string snapshot, DateTimeOffset observedAt) in snapshots)
                {
                    LiveCaptionUpdate update = resolver.Process(snapshot, observedAt);
                    foreach (LiveCaptionSegment segment in update.FinalizedSegments)
                    {
                        admittedCandidates++;
                        queue.Enqueue(
                            (_, _) =>
                            {
                                Interlocked.Increment(ref translationCalls);
                                return Task.FromResult<(string, bool)>(
                                    ("其他一切都在某种程度上退居背景。", true));
                            },
                            segment.Text,
                            "Fake",
                            session.Id,
                            "zh-CN",
                            Identity(segment),
                            waitForPersistence: true);
                        TranslationQueueResult result = await queue.ReadLatestResultAsync()
                            .AsTask()
                            .WaitAsync(TimeSpan.FromSeconds(2));
                        viewModel.ApplySegment(new TranscriptSegment(
                            result.Identity!.SegmentId,
                            result.Identity.Sequence,
                            result.Identity.Revision,
                            result.OriginalText,
                            result.TranslatedText,
                            SegmentState.Translated,
                            result.Identity.CapturedAt));
                    }
                }

                await WaitUntilAsync(() =>
                    Volatile.Read(ref persistedCallbacks) == admittedCandidates);
                await queue.WaitForPersistenceAsync().WaitAsync(TimeSpan.FromSeconds(2));
                List<PersistedRow> rows = await LoadRowsAsync(
                    connectionString,
                    session.Id);

                Assert.AreEqual(1, translationCalls);
                Assert.AreEqual(1, viewModel.Segments.Count);
                Assert.AreEqual(1, rows.Count);
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public async Task AccessibilityDuplicateBurstCreatesOneTailRowAcrossProductionChain()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "LectureCopilot.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "history.db"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            try
            {
                await CreateSchemaAsync(connectionString);
                var repository = new LectureReviewRepository(connectionString);
                LectureSessionEntry session = await repository.CreateSessionAsync(
                    "重复爆发隔离回放",
                    "",
                    "Fake",
                    "zh-CN");
                TranslationSegmentPersistence persistence =
                    CreatePersistence(repository);
                int persistedCallbacks = 0;
                int translationCalls = 0;
                var queue = new TranslationTaskQueue(async (result, token) =>
                {
                    await persistence.UpsertAsync(
                        new TranslationPersistenceRequest(
                            result.SessionId!.Value,
                            result.Identity!,
                            result.OriginalText,
                            result.TranslatedText,
                            result.TargetLanguage,
                            result.ApiName),
                        token);
                    Interlocked.Increment(ref persistedCallbacks);
                });
                var segmenter = new LiveCaptionSegmenter();
                var viewModel = new TranscriptSessionViewModel();
                var emitted = new List<LiveCaptionSegment>();
                DateTimeOffset start = new(2026, 9, 13, 22, 10, 53, TimeSpan.Zero);
                const string firstText =
                    "Yes, I'll chat with you, give you instant feedback on.";
                const string repeatedText = "I can speak with you.";
                (string Text, DateTimeOffset At)[] replay =
                [
                    (firstText, start),
                    ($"{firstText} {repeatedText}", start.AddSeconds(1)),
                    ($"{repeatedText} {repeatedText}", start.AddMilliseconds(1250)),
                    ($"{repeatedText} {repeatedText} {repeatedText}", start.AddMilliseconds(1500))
                ];

                foreach ((string snapshot, DateTimeOffset observedAt) in replay)
                {
                    LiveCaptionUpdate update = segmenter.Process(snapshot, observedAt);
                    foreach (LiveCaptionSegment segment in update.FinalizedSegments)
                    {
                        emitted.Add(segment);
                        queue.Enqueue(
                            (_, _) =>
                            {
                                Interlocked.Increment(ref translationCalls);
                                return Task.FromResult<(string, bool)>(
                                    ($"译文 {segment.Sequence}", true));
                            },
                            segment.Text,
                            "Fake",
                            session.Id,
                            "zh-CN",
                            Identity(segment),
                            waitForPersistence: true);
                        TranslationQueueResult result = await queue.ReadLatestResultAsync()
                            .AsTask()
                            .WaitAsync(TimeSpan.FromSeconds(2));
                        viewModel.ApplySegment(new TranscriptSegment(
                            result.Identity!.SegmentId,
                            result.Identity.Sequence,
                            result.Identity.Revision,
                            result.OriginalText,
                            result.TranslatedText,
                            SegmentState.Translated,
                            result.Identity.CapturedAt));
                    }
                }

                await WaitUntilAsync(() =>
                    Volatile.Read(ref persistedCallbacks) == emitted.Count);
                await queue.WaitForPersistenceAsync().WaitAsync(TimeSpan.FromSeconds(2));
                List<PersistedRow> rows = await LoadRowsAsync(
                    connectionString,
                    session.Id);

                Assert.AreEqual(2, emitted.Count);
                Assert.AreEqual(2, emitted.Select(item => item.Id).Distinct().Count());
                Assert.AreEqual(2, translationCalls);
                Assert.AreEqual(2, viewModel.Segments.Count);
                Assert.AreEqual(2, rows.Count);
                Assert.AreEqual(repeatedText, rows[^1].SourceText);
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task GrowingPunctuatedRecognitionUpdatesOneDatabaseAndWorkspaceRow(
            bool includeIntermediateDrafts)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "LectureCopilot.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "history.db"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            try
            {
                await CreateSchemaAsync(connectionString);
                var repository = new LectureReviewRepository(connectionString);
                LectureSessionEntry session = await repository.CreateSessionAsync(
                    "增长修订回放",
                    "",
                    "Fake",
                    "zh-CN");
                TranslationSegmentPersistence persistence =
                    CreatePersistence(repository);
                int persistedCallbacks = 0;
                var queue = new TranslationTaskQueue(async (result, token) =>
                {
                    await persistence.UpsertAsync(
                        new TranslationPersistenceRequest(
                            result.SessionId!.Value,
                            result.Identity!,
                            result.OriginalText,
                            result.TranslatedText,
                            result.TargetLanguage,
                            result.ApiName),
                        token);
                    Interlocked.Increment(ref persistedCallbacks);
                });
                var segmenter = new LiveCaptionSegmenter();
                var viewModel = new TranscriptSessionViewModel();
                var emitted = new List<LiveCaptionSegment>();
                DateTimeOffset start = new(2026, 9, 11, 23, 4, 4, TimeSpan.Zero);
                string[] snapshots =
                [
                    "And you know?",
                    "And you know what happens?",
                    "And you know what happens when captions arrive continuously?",
                    "And you know what happens when captions arrive continuously during a fast lecture and punctuation changes?",
                    "And you know what happens when captions arrive continuously during a fast lecture and punctuation changes while the speaker keeps talking without creating duplicate rows."
                ];
                string[] staleDraftReplays = [];

                if (includeIntermediateDrafts)
                {
                    staleDraftReplays = snapshots
                        .Skip(1)
                        .Select(text => text.TrimEnd('.', '?'))
                        .TakeLast(2)
                        .ToArray();
                    snapshots = snapshots.SelectMany((text, index) => index == 0
                        ? new[] { text }
                        : new[] { text.TrimEnd('.', '?'), text }).ToArray();
                }

                for (int index = 0; index < snapshots.Length; index++)
                {
                    LiveCaptionUpdate update = segmenter.Process(
                        snapshots[index],
                        start.AddSeconds(index * 2));
                    LiveCaptionSegment segment = update.DraftSegment ??
                        update.FinalizedSegments.Single();
                    emitted.Add(segment);
                    queue.Enqueue(
                        (_, _) => Task.FromResult<(string, bool)>(
                            ($"译文 revision {segment.Revision}", segment.IsFinal)),
                        segment.Text,
                        "Fake",
                        session.Id,
                        "zh-CN",
                        Identity(segment),
                        waitForPersistence: true);
                    TranslationQueueResult result = await queue.ReadLatestResultAsync()
                        .AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(2));
                    var projected = new TranscriptSegment(
                        result.Identity!.SegmentId,
                        result.Identity.Sequence,
                        result.Identity.Revision,
                        result.OriginalText,
                        result.TranslatedText,
                        result.Identity.IsFinal ? SegmentState.Translated : SegmentState.Draft,
                        result.Identity.CapturedAt);
                    viewModel.ApplySegment(projected);
                    await queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    if (!result.Identity.IsFinal)
                    {
                        Assert.AreEqual(result.OriginalText, viewModel.LiveText);
                        Assert.HasCount(1, viewModel.Segments,
                            "Growing provisional revisions must not append classroom rows.");
                        Assert.HasCount(1, await LoadRowsAsync(connectionString, session.Id),
                            "A translated draft must not create or replace a formal stored sentence.");
                        Assert.AreNotEqual(result.OriginalText, viewModel.Segments[0].SourceText);
                    }
                }

                for (int index = 0; index < staleDraftReplays.Length; index++)
                {
                    LiveCaptionUpdate rollback = segmenter.Process(
                        staleDraftReplays[index],
                        start.AddSeconds((snapshots.Length + index) * 2));
                    Assert.IsNull(rollback.DraftSegment);
                    Assert.IsEmpty(rollback.FinalizedSegments);
                    Assert.AreEqual(emitted[^1].Id, rollback.CurrentSegment?.Id);
                    Assert.AreEqual(snapshots[^1], rollback.CurrentText);
                }

                await WaitUntilAsync(() =>
                    Volatile.Read(ref persistedCallbacks) == emitted.Count(segment => segment.IsFinal));
                await queue.WaitForPersistenceAsync().WaitAsync(TimeSpan.FromSeconds(2));
                List<PersistedRow> rows = await LoadRowsAsync(
                    connectionString,
                    session.Id);

                Assert.AreEqual(1, emitted.Select(segment => segment.Id).Distinct().Count());
                Assert.AreEqual(snapshots.Length - 1, emitted[^1].Revision);
                Assert.HasCount(1, viewModel.Segments);
                Assert.AreEqual(snapshots[^1], viewModel.Segments[0].SourceText);
                Assert.AreEqual(start, viewModel.Segments[0].CapturedAt);
                Assert.HasCount(1, rows);
                Assert.AreEqual(snapshots[^1], rows[0].SourceText);
                Assert.AreEqual(start.ToUnixTimeSeconds(), rows[0].CapturedAtUnix);
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public async Task PersistenceRejectsLateRevisionAndSeparatesLectureSessions()
        {
            var entries = new ConcurrentDictionary<(long SessionId, long EntryId),
                TranslationHistoryEntry>();
            long nextEntryId = 0;
            var persistence = new TranslationSegmentPersistence(
                (request, _) =>
                {
                    long entryId = Interlocked.Increment(ref nextEntryId);
                    TranslationHistoryEntry entry = Entry(entryId, request);
                    entries[(request.SessionId, entryId)] = entry;
                    return Task.FromResult(entry);
                },
                (entryId, request, _) =>
                {
                    if (!entries.ContainsKey((request.SessionId, entryId)))
                        return Task.FromResult<TranslationHistoryEntry?>(null);
                    TranslationHistoryEntry entry = Entry(entryId, request);
                    entries[(request.SessionId, entryId)] = entry;
                    return Task.FromResult<TranslationHistoryEntry?>(entry);
                });

            Guid sharedSegmentId = Guid.NewGuid();
            DateTimeOffset capturedAt = new(2026, 9, 11, 15, 0, 0, TimeSpan.Zero);
            TranslationPersistenceResult newest = await persistence.UpsertAsync(Request(
                10, sharedSegmentId, revision: 2, "Newest.", "最新。", capturedAt));
            TranslationPersistenceResult stale = await persistence.UpsertAsync(Request(
                10, sharedSegmentId, revision: 1, "Old.", "旧。", capturedAt));
            TranslationPersistenceResult otherLecture = await persistence.UpsertAsync(Request(
                11, sharedSegmentId, revision: 0, "Again.", "再次。", capturedAt.AddMinutes(1)));

            Assert.IsTrue(newest.Applied);
            Assert.IsFalse(stale.Applied);
            Assert.IsTrue(otherLecture.Applied);
            Assert.AreEqual(2, entries.Count);
            Assert.AreEqual("Newest.", entries[(10, newest.Entry.Id)].SourceText);
            Assert.AreEqual("Again.", entries[(11, otherLecture.Entry.Id)].SourceText);
        }

        [TestMethod]
        public async Task SlowSentenceAResultCannotClearSentenceBDraft()
        {
            var segmenter = new LiveCaptionSegmenter();
            LiveCaptionUpdate firstFrame = segmenter.Process("First sentence.");
            LiveCaptionSegment first = firstFrame.FinalizedSegments.Single();
            var releaseFirst = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var queue = new TranslationTaskQueue();
            queue.Enqueue(
                async (token, _) =>
                {
                    await releaseFirst.Task.WaitAsync(token);
                    return ("第一句。", true);
                },
                first.Text,
                "Test",
                null,
                "zh-CN",
                Identity(first));

            LiveCaptionUpdate nextFrame = segmenter.Process(
                "First sentence. Second sentence is already being recognized");
            LiveCaptionSegment second = nextFrame.DraftSegment!;
            var viewModel = new TranscriptSessionViewModel();
            viewModel.SetDraft(Draft(second));

            releaseFirst.SetResult();
            TranslationQueueResult result = await queue.ReadLatestResultAsync()
                .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            viewModel.ApplySegment(new TranscriptSegment(
                result.Identity!.SegmentId,
                result.Identity.Sequence,
                result.Identity.Revision,
                result.OriginalText,
                result.TranslatedText,
                SegmentState.Translated,
                result.Identity.CapturedAt));
            bool cleared = viewModel.ClearDraft(
                result.Identity.SegmentId,
                result.Identity.Revision);

            Assert.IsFalse(cleared);
            Assert.AreEqual(second.Id, viewModel.DraftSegmentId);
            Assert.AreEqual(second.Text, viewModel.DraftText);
            Assert.AreEqual(first.Id, viewModel.Segments.Single().Id);
        }

        [TestMethod]
        public async Task TwoEqualFinalUtterancesRemainTwoQueueResults()
        {
            var segmenter = new LiveCaptionSegmenter();
            var start = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            LiveCaptionSegment first = segmenter.Process("Hello.", start)
                .FinalizedSegments.Single();
            LiveCaptionSegment repeatedDraft = segmenter.Process(
                "Hello. Hel",
                start.AddMilliseconds(500)).DraftSegment!;
            LiveCaptionSegment second = segmenter.Process(
                "Hello. Hello.",
                start.AddSeconds(1))
                .FinalizedSegments.Single();
            Assert.AreEqual(repeatedDraft.Id, second.Id);
            Assert.AreNotEqual(first.Id, second.Id);
            var persisted = new ConcurrentQueue<Guid>();
            var queue = new TranslationTaskQueue((result, _) =>
            {
                persisted.Enqueue(result.Identity!.SegmentId);
                return Task.CompletedTask;
            });

            queue.Enqueue(
                (_, _) => Task.FromResult<(string, bool)>(("你好。", true)),
                first.Text,
                "Test",
                1,
                "zh-CN",
                Identity(first));
            queue.Enqueue(
                (_, _) => Task.FromResult<(string, bool)>(("你好。", true)),
                second.Text,
                "Test",
                1,
                "zh-CN",
                Identity(second));

            await WaitUntilAsync(() => persisted.Count == 2);
            CollectionAssert.AreEqual(
                new[] { first.Id, second.Id },
                persisted.ToArray());
        }

        [TestMethod]
        public async Task LateOldRevisionIsDiscardedAndLatestRevisionContinues()
        {
            var segmenter = new LiveCaptionSegmenter();
            LiveCaptionSegment draft = segmenter.Process("A useful draft is growing")
                .DraftSegment!;
            var oldRelease = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var oldStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var queue = new TranslationTaskQueue();
            queue.Enqueue(
                async (_, _) =>
                {
                    oldStarted.SetResult();
                    await oldRelease.Task;
                    return ("过期译文", false);
                },
                draft.Text,
                "Test",
                null,
                "zh-CN",
                Identity(draft));
            await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            LiveCaptionSegment final = segmenter.Process("A useful draft is growing.")
                .FinalizedSegments.Single();
            queue.Enqueue(
                (_, _) => Task.FromResult<(string, bool)>(("最新译文。", true)),
                final.Text,
                "Test",
                null,
                "zh-CN",
                Identity(final));
            oldRelease.SetResult();

            TranslationQueueResult result = await queue.ReadLatestResultAsync()
                .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(final.Id, result.Identity?.SegmentId);
            Assert.AreEqual(final.Revision, result.Identity?.Revision);
            Assert.AreEqual("最新译文。", result.TranslatedText);
        }

        private static TranslationTaskIdentity Identity(LiveCaptionSegment segment)
        {
            return new TranslationTaskIdentity(
                segment.Id,
                segment.Sequence,
                segment.Revision,
                segment.IsFinal,
                segment.CapturedAt);
        }

        private static TranscriptSegment Draft(LiveCaptionSegment segment)
        {
            return new TranscriptSegment(
                segment.Id,
                segment.Sequence,
                segment.Revision,
                segment.Text,
                null,
                SegmentState.Draft,
                segment.CapturedAt);
        }

        private static TranslationPersistenceRequest Request(
            long sessionId,
            Guid segmentId,
            int revision,
            string source,
            string translation,
            DateTimeOffset capturedAt)
        {
            return new TranslationPersistenceRequest(
                sessionId,
                new TranslationTaskIdentity(
                    segmentId,
                    Sequence: 1,
                    revision,
                    IsFinal: true,
                    capturedAt),
                source,
                translation,
                "zh-CN",
                "Fake");
        }

        private static TranslationSegmentPersistence CreatePersistence(
            LectureReviewRepository repository)
        {
            return new TranslationSegmentPersistence(
                (request, token) => repository.CreateTranslationAsync(
                    request.SessionId,
                    request.SourceText,
                    request.TranslatedText,
                    request.TargetLanguage,
                    request.ApiName,
                    request.Identity.CapturedAt,
                    token),
                (entryId, request, token) =>
                    repository.UpdateTranslationSnapshotAsync(
                        entryId,
                        request.SessionId,
                        request.SourceText,
                        request.TranslatedText,
                        request.TargetLanguage,
                        request.ApiName,
                        token));
        }

        private static TranslationHistoryEntry Entry(
            long entryId,
            TranslationPersistenceRequest request)
        {
            DateTime localTime = request.Identity.CapturedAt.LocalDateTime;
            return new TranslationHistoryEntry
            {
                Id = entryId,
                SessionId = request.SessionId,
                Timestamp = localTime.ToString("MM/dd HH:mm"),
                TimestampFull = localTime.ToString("MM/dd/yy, HH:mm:ss"),
                SourceText = request.SourceText,
                TranslatedText = request.TranslatedText,
                TargetLanguage = request.TargetLanguage,
                ApiUsed = request.ApiName
            };
        }

        private static async Task CreateSchemaAsync(string connectionString)
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE LectureSessions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    StartedAt TEXT NOT NULL,
                    EndedAt TEXT,
                    Mode TEXT NOT NULL,
                    ApiUsed TEXT,
                    TargetLanguage TEXT,
                    SummaryText TEXT
                );
                CREATE TABLE TranslationHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp TEXT,
                    SourceText TEXT,
                    TranslatedText TEXT,
                    TargetLanguage TEXT,
                    ApiUsed TEXT,
                    SessionId INTEGER
                );";
            await command.ExecuteNonQueryAsync();
        }

        private static async Task<List<PersistedRow>> LoadRowsAsync(
            string connectionString,
            long sessionId)
        {
            var rows = new List<PersistedRow>();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT Timestamp, SourceText
                FROM TranslationHistory
                WHERE SessionId = @SessionId
                ORDER BY Id;";
            command.Parameters.AddWithValue("@SessionId", sessionId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(new PersistedRow(
                    Convert.ToInt64(
                        reader.GetValue(0),
                        System.Globalization.CultureInfo.InvariantCulture),
                    reader.GetString(1)));
            }
            return rows;
        }

        private static async Task WaitUntilAsync(Func<bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!predicate())
                await Task.Delay(10, timeout.Token);
        }

        private sealed record PersistedRow(long CapturedAtUnix, string SourceText);
    }
}
