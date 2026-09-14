using Microsoft.Data.Sqlite;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.services;
using LiveCaptionsTranslator.utils;
using LiveCaptionsTranslator.viewmodels;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class ClassroomRecordingProductionReplayTests
    {
        private static readonly DateTimeOffset Start = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

        [TestMethod]
        public async Task GrowingClippedSnapshotsStayLiveUntilCompleteThenCreateOneRow()
        {
            await using Replay replay = await Replay.CreateAsync();
            const string opening = "Before beginning the controlled experiment, ";
            const string body = "the technician checks the cooling circuit and records the inlet pressure while " +
                "the controller maintains a steady flow through the primary channel and the " +
                "secondary sensor verifies the expected temperature";
            LiveCaptionUpdate initial = await replay.ObserveAsync(opening + body, 0);
            Guid identity = initial.CurrentSegment!.Id;
            Assert.AreEqual(opening + body, replay.ViewModel.LiveText);
            Assert.IsEmpty(replay.ViewModel.Segments);
            Assert.IsEmpty(await replay.RowsAsync());

            LiveCaptionUpdate growth = await replay.ObserveAsync(body + " throughout the test", 200);
            Assert.AreEqual(identity, growth.CurrentSegment!.Id);
            Assert.AreEqual(opening + body + " throughout the test", replay.ViewModel.LiveText);
            replay.Enqueue(growth.CurrentSegment, final: false,
                (_, _) => Task.FromResult<(string, bool)>(("实时预览译文", true)));
            await replay.DrainAsync();
            Assert.AreEqual("实时预览译文", replay.ViewModel.LiveTranslation);
            Assert.IsEmpty(replay.ViewModel.Segments);
            Assert.IsEmpty(await replay.RowsAsync(), "A successful provisional translation must not enter SQLite.");

            string completed = opening + body + " throughout the test.";
            await replay.ObserveAsync(body + " throughout the test.", 400);
            Assert.IsEmpty(replay.ViewModel.Segments, "Temporary punctuation is still live.");
            await replay.ObserveAsync(body + " throughout the test.", 1200);
            Assert.HasCount(1, replay.Recorded);
            Assert.AreEqual(identity, replay.Recorded[0].Segment.Id);
            Assert.HasCount(1, replay.ViewModel.Segments);
            Assert.AreEqual(completed, (await replay.RowsAsync()).Single().SourceText);

            const string next = "The second sensor measures the supply current.";
            await replay.ObserveAsync(completed + " The second sensor", 1300);
            await replay.ObserveAsync(completed + " " + next, 1400);
            await replay.ObserveAsync(next + " " + completed, 1500);
            await replay.ObserveAsync(completed + " " + next, 2300);
            Assert.HasCount(2, replay.Recorded);
            Assert.HasCount(2, replay.ViewModel.Segments);
            Assert.HasCount(2, await replay.RowsAsync());
            Assert.AreEqual(2, replay.Recorded.Select(item => item.Segment.Id).Distinct().Count());
            Assert.AreEqual(next, replay.ViewModel.LiveText, "A rotated older row must not replace current speech.");
        }

        [TestMethod]
        public async Task PreEnableBaselinePreservesTheFirstMicrophoneSentenceExactlyOnce()
        {
            await using Replay replay = await Replay.CreateAsync();
            const string oldWindow = "Text left visible before this classroom.";
            const string firstSentence = "The first microphone sentence is captured.";
            const string secondSentence = "The second microphone sentence follows normally.";
            replay.StartFromCurrentSnapshot(oldWindow, 0);

            LiveCaptionUpdate first = await replay.ObserveAsync(
                $"{oldWindow} {firstSentence}",
                100);
            Assert.AreEqual(firstSentence, replay.ViewModel.LiveText);
            Assert.IsNotNull(first.CurrentSegment);
            Guid firstId = first.CurrentSegment.Id;
            Assert.IsEmpty(replay.ViewModel.Segments);
            Assert.IsEmpty(await replay.RowsAsync());

            await replay.ObserveAsync($"{oldWindow} {firstSentence}", 900);
            await replay.ObserveAsync($"{oldWindow} {firstSentence}", 5000);

            Assert.HasCount(1, replay.Recorded);
            Assert.AreEqual(firstId, replay.Recorded[0].Segment.Id);
            Assert.AreEqual(firstSentence, replay.Recorded[0].Segment.Text);
            Assert.HasCount(1, replay.ViewModel.Segments);
            Assert.AreEqual(firstSentence, replay.ViewModel.Segments[0].SourceText);
            Assert.HasCount(1, await replay.RowsAsync());
            Assert.AreEqual(firstSentence, (await replay.RowsAsync())[0].SourceText);

            LiveCaptionUpdate second = await replay.ObserveAsync(
                $"{oldWindow} {firstSentence} {secondSentence}",
                5100);
            Assert.AreEqual(secondSentence, replay.ViewModel.LiveText);
            Assert.IsNotNull(second.CurrentSegment);
            Guid secondId = second.CurrentSegment.Id;
            Assert.AreNotEqual(firstId, secondId);

            await replay.ObserveAsync(
                $"{oldWindow} {firstSentence} {secondSentence}",
                5900);
            await replay.ObserveAsync(
                $"{oldWindow} {firstSentence} {secondSentence}",
                10000);

            Assert.HasCount(2, replay.Recorded);
            Assert.AreEqual(2, replay.Recorded.Select(item => item.Segment.Id).Distinct().Count());
            Assert.AreEqual(secondId, replay.Recorded[1].Segment.Id);
            Assert.AreEqual(secondSentence, replay.Recorded[1].Segment.Text);
            Assert.HasCount(2, replay.ViewModel.Segments);
            Assert.AreEqual(secondSentence, replay.ViewModel.Segments[1].SourceText);
            Assert.HasCount(2, await replay.RowsAsync());
            Assert.AreEqual(secondSentence, (await replay.RowsAsync())[1].SourceText);
        }

        [TestMethod]
        public async Task SourceExistsWhileProviderIsBlockedAndLatePlaceholderCannotClearTranslation()
        {
            await using Replay replay = await Replay.CreateAsync();
            const string text = "The voltage reference remains stable during calibration.";
            await replay.ObserveAsync(text, 0);
            await replay.ObserveAsync(text, 800);
            RecordedCaption recorded = replay.Recorded.Single();
            var started = NewSignal();
            var release = NewSignal();
            replay.Enqueue(recorded.Segment, final: true, async (token, _) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                return ("电压基准在校准期间保持稳定。", true);
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var before = await replay.RowsAsync();
            Assert.HasCount(1, before);
            Assert.AreEqual(text, before[0].SourceText);
            Assert.AreEqual(string.Empty, before[0].TranslatedText);
            Assert.AreEqual(text, replay.ViewModel.Segments.Single().SourceText);

            release.TrySetResult();
            await replay.DrainAsync();
            TranslationPersistenceResult stalePlaceholder = await replay.Persistence.UpsertAsync(
                replay.Request(recorded.Segment, string.Empty));
            Assert.IsFalse(stalePlaceholder.Applied);
            Assert.AreEqual("电压基准在校准期间保持稳定。", (await replay.RowsAsync()).Single().TranslatedText);
            Assert.AreEqual("电压基准在校准期间保持稳定。", replay.ViewModel.Segments.Single().TranslatedText);
        }

        [TestMethod]
        public async Task LateFinalCannotOverwriteANewerRecordedSourceRevision()
        {
            await using Replay replay = await Replay.CreateAsync();
            const string first = "The measured output voltage stays constant.";
            const string latest = "The measured output voltage stays constant across both terminals.";
            await replay.ObserveAsync(first, 0);
            await replay.ObserveAsync(first, 800);
            LiveCaptionSegment original = replay.Recorded.Single().Segment;
            var started = NewSignal();
            var release = NewSignal();
            replay.Enqueue(original, final: true, async (_, _) =>
            {
                started.TrySetResult();
                await release.Task; // Deliberately emulate a provider ignoring cancellation.
                return ("过期译文", true);
            });
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await replay.ObserveAsync(latest.TrimEnd('.'), 900);
                await replay.ObserveAsync(latest, 1000);
                await replay.ObserveAsync(latest, 1800);
                LiveCaptionSegment updated = replay.Recorded[^1].Segment;
                Assert.AreEqual(original.Id, updated.Id);
                Assert.IsTrue(updated.Revision > original.Revision);
                Assert.AreEqual(latest, (await replay.RowsAsync()).Single().SourceText);

                TranslationPersistenceResult stale = await replay.Persistence.UpsertAsync(
                    replay.Request(original, "迟到旧版本"));
                Assert.IsFalse(stale.Applied, "Storage must independently reject an older revision.");
                replay.Enqueue(updated, final: true,
                    (_, _) => Task.FromResult<(string, bool)>(("最新完整译文", true)));
                release.TrySetResult();
                await replay.DrainAsync();
                Assert.HasCount(1, await replay.RowsAsync());
                Assert.AreEqual(latest, (await replay.RowsAsync()).Single().SourceText);
                Assert.AreEqual("最新完整译文", (await replay.RowsAsync()).Single().TranslatedText);
                Assert.HasCount(1, replay.ViewModel.Segments);
                Assert.AreEqual(Start, replay.ViewModel.Segments[0].CapturedAt);
            }
            finally
            {
                release.TrySetResult();
            }
        }

        [TestMethod]
        public async Task StoppingPersistsTheWholeUnfinishedSentenceExactlyOnce()
        {
            await using Replay replay = await Replay.CreateAsync();
            const string text = "The probe should be connected to the terminal before the operator";
            await replay.ObserveAsync(text, 0);
            Assert.AreEqual(text, replay.ViewModel.LiveText);
            Assert.IsEmpty(await replay.RowsAsync());
            await replay.FlushAsync();
            await replay.FlushAsync();

            Assert.HasCount(1, replay.Recorded);
            Assert.IsTrue(replay.Recorded[0].IsIncomplete);
            Assert.HasCount(1, replay.ViewModel.Segments);
            Assert.IsTrue(replay.ViewModel.Segments[0].IsIncomplete);
            Assert.AreEqual(text, replay.ViewModel.LiveText);
            Assert.AreEqual(text, (await replay.RowsAsync()).Single().SourceText);
            Assert.AreEqual(0, replay.ProviderCalls);
        }

        [TestMethod]
        public async Task SlowProviderRetainsMoreThanSixtyFourDistinctRecordedFinals()
        {
            await using Replay replay = await Replay.CreateAsync();
            const int count = 80;
            string[] sentences = Enumerable.Range(0, count)
                .Select(index => $"Sample {index:000} contains a distinct calibration measurement.")
                .ToArray();
            var release = NewSignal();
            var started = NewSignal();
            int enqueued = 0;
            LiveCaptionSegment? first = null;
            for (int index = 0; index < count; index++)
            {
                await replay.ObserveAsync(string.Join(" ", sentences
                    .Take(index + 1).TakeLast(3)), index * 100);
                foreach (RecordedCaption item in replay.Recorded.Skip(enqueued).ToArray())
                {
                    if (first == null)
                    {
                        first = item.Segment;
                        replay.Enqueue(item.Segment, final: true, async (token, _) =>
                        {
                            started.TrySetResult();
                            await release.Task.WaitAsync(token);
                            return ("译文 000", true);
                        });
                        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    else
                    {
                        long sequence = item.Segment.Sequence;
                        replay.Enqueue(item.Segment, final: true,
                            (_, _) => Task.FromResult<(string, bool)>(($"译文 {sequence}", true)));
                    }
                    enqueued++;
                }
            }
            await replay.ObserveAsync(string.Join(" ", sentences.TakeLast(3)), count * 100 + 800);
            foreach (RecordedCaption item in replay.Recorded.Skip(enqueued).ToArray())
            {
                replay.Enqueue(item.Segment, final: true,
                    (_, _) => Task.FromResult<(string, bool)>(("最后一条译文", true)));
                enqueued++;
            }

            Assert.AreEqual(count, enqueued);
            Assert.IsTrue(replay.Policy.IsCurrentRevision(Identity(first!, final: true)),
                "Evicting source matching history must not invalidate a still-pending recorded final.");
            Assert.HasCount(count, await replay.RowsAsync(), "Accepted source records do not wait for the provider.");
            release.TrySetResult();
            await replay.DrainAsync();
            Assert.AreEqual(count, replay.ProviderCalls);
            Assert.AreEqual(count, replay.PersistedTranslations);
            Assert.HasCount(count, replay.ViewModel.Segments);
            Assert.IsTrue((await replay.RowsAsync()).All(row => !string.IsNullOrWhiteSpace(row.TranslatedText)));
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        [TestMethod]
        public async Task ManualCreateAndEditStillRejectEmptyTranslation()
        {
            await using Replay replay = await Replay.CreateAsync();
            await Assert.ThrowsAsync<ArgumentException>(() => replay.Repository.CreateTranslationAsync(
                replay.SessionId, "A manually entered source.", string.Empty, "zh-CN", "Manual"));
            TranslationHistoryEntry entry = await replay.Repository.CreateTranslationAsync(
                replay.SessionId, "A manually entered source.", "手动输入的译文。", "zh-CN", "Manual");
            await Assert.ThrowsAsync<ArgumentException>(() => replay.Repository.UpdateTranslationAsync(
                entry.Id, replay.SessionId, entry.SourceText, string.Empty));
            Assert.HasCount(1, await replay.RowsAsync());
            Assert.AreEqual("手动输入的译文。", (await replay.RowsAsync()).Single().TranslatedText);
        }

        private static TranslationTaskIdentity Identity(LiveCaptionSegment segment, bool final,
            bool incomplete = false) => new(segment.Id, segment.Sequence, segment.Revision,
                final, segment.CapturedAt, CaptureEpoch: 1, IsIncomplete: incomplete);

        private sealed record PersistedRow(string SourceText, string TranslatedText);

        private sealed class Replay : IAsyncDisposable
        {
            private readonly string root;
            private readonly string connectionString;
            private readonly object stateLock = new();
            private readonly LiveCaptionIdentityResolver source = new(splitLongDrafts: false);
            private readonly TranslationTaskQueue queue;
            private int providerCalls;
            private int persistedTranslations;
            public CaptionRecordingPolicy Policy { get; } = new();
            public TranscriptSessionViewModel ViewModel { get; } = new();
            public List<RecordedCaption> Recorded { get; } = [];
            public TranslationSegmentPersistence Persistence { get; }
            public LectureReviewRepository Repository { get; }
            public long SessionId { get; }
            public int ProviderCalls => Volatile.Read(ref providerCalls);
            public int PersistedTranslations => Volatile.Read(ref persistedTranslations);

            private Replay(string root, string connectionString, LectureReviewRepository repository, long sessionId)
            {
                this.root = root;
                this.connectionString = connectionString;
                Repository = repository;
                SessionId = sessionId;
                Persistence = new TranslationSegmentPersistence(
                    (request, token) => repository.CreateTranslationAsync(request.SessionId,
                        request.SourceText, request.TranslatedText, request.TargetLanguage,
                        request.ApiName, request.Identity.CapturedAt, token, allowUntranslated: true),
                    (id, request, token) => repository.UpdateTranslationSnapshotAsync(id,
                        request.SessionId, request.SourceText, request.TranslatedText,
                        request.TargetLanguage, request.ApiName, token));
                queue = new TranslationTaskQueue(async (result, token) =>
                {
                    if (result.Identity?.IsFinal != true || result.IsPartial || !result.IsComplete)
                        return;
                    await Persistence.UpsertAsync(new TranslationPersistenceRequest(SessionId,
                        result.Identity, result.OriginalText, result.TranslatedText,
                        result.TargetLanguage, result.ApiName), token);
                    Interlocked.Increment(ref persistedTranslations);
                }, result =>
                {
                    lock (stateLock)
                    {
                        if (result.Identity!.IsFinal)
                            ViewModel.ApplySegment(new TranscriptSegment(result.Identity.SegmentId,
                                result.Identity.Sequence, result.Identity.Revision, result.OriginalText,
                                result.TranslatedText, SegmentState.Translated, result.Identity.CapturedAt,
                                SessionId, 1, result.Identity.IsIncomplete));
                        else
                            ViewModel.SetDraftTranslation(result.Identity.SegmentId,
                                result.Identity.Revision, result.TranslatedText);
                    }
                }, result =>
                {
                    lock (stateLock)
                        return result.SessionId == SessionId && result.Identity != null &&
                            result.Identity.CaptureEpoch == 1 && Policy.IsCurrentRevision(result.Identity);
                });
            }

            public static async Task<Replay> CreateAsync()
            {
                string root = Path.Combine(Path.GetTempPath(), "LectureCopilot.Tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                string connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(root, "history.db"),
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false
                }.ToString();
                await using (var connection = new SqliteConnection(connectionString))
                {
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        CREATE TABLE LectureSessions (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT, StartedAt TEXT NOT NULL,
                            EndedAt TEXT, Mode TEXT NOT NULL, ApiUsed TEXT, TargetLanguage TEXT,
                            SummaryText TEXT);
                        CREATE TABLE TranslationHistory (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT, Timestamp TEXT, SourceText TEXT,
                            TranslatedText TEXT, TargetLanguage TEXT, ApiUsed TEXT, SessionId INTEGER);
                        """;
                    await command.ExecuteNonQueryAsync();
                }
                var repository = new LectureReviewRepository(connectionString);
                LectureSessionEntry session = await repository.CreateSessionAsync(
                    "Synthetic classroom replay", "", "Fake", "zh-CN");
                return new Replay(root, connectionString, repository, session.Id);
            }

            public void StartFromCurrentSnapshot(string snapshot, int milliseconds)
            {
                lock (stateLock)
                {
                    source.StartFromCurrentSnapshot(
                        snapshot,
                        Start.AddMilliseconds(milliseconds));
                    Policy.Reset();
                }
            }

            public async Task<LiveCaptionUpdate> ObserveAsync(string snapshot, int milliseconds)
            {
                LiveCaptionUpdate update;
                IReadOnlyList<RecordedCaption> records;
                lock (stateLock)
                {
                    update = source.Process(snapshot, Start.AddMilliseconds(milliseconds));
                    records = Policy.Observe(update, TimeSpan.FromMilliseconds(milliseconds));
                    if (update.CurrentSegment is LiveCaptionSegment current)
                        ViewModel.SetDraft(Project(current, SegmentState.Draft));
                }
                foreach (RecordedCaption record in records)
                    await PublishSourceAsync(record);
                return update;
            }

            public async Task FlushAsync()
            {
                IReadOnlyList<RecordedCaption> records;
                lock (stateLock)
                    records = Policy.Flush();
                foreach (RecordedCaption record in records)
                    await PublishSourceAsync(record);
            }

            private async Task PublishSourceAsync(RecordedCaption record)
            {
                lock (stateLock)
                {
                    Recorded.Add(record);
                    ViewModel.ApplySegment(Project(record.Segment, SegmentState.Committed,
                        record.IsIncomplete));
                    ViewModel.ClearDraft(record.Segment.Id, record.Segment.Revision);
                }
                await Persistence.UpsertAsync(Request(record.Segment, string.Empty, record.IsIncomplete));
            }

            public TranslationPersistenceRequest Request(LiveCaptionSegment segment, string translation,
                bool incomplete = false) => new(SessionId, Identity(segment, true, incomplete),
                    segment.Text, translation, "zh-CN", "Fake");

            public void Enqueue(LiveCaptionSegment segment, bool final,
                Func<CancellationToken, Action<string>, Task<(string, bool)>> provider)
            {
                queue.Enqueue(async (token, partial) =>
                {
                    Interlocked.Increment(ref providerCalls);
                    return await provider(token, partial);
                }, segment.Text, "Fake", SessionId, "zh-CN", Identity(segment, final),
                    waitForPersistence: true);
            }

            private TranscriptSegment Project(LiveCaptionSegment segment, SegmentState state,
                bool incomplete = false) => new(segment.Id, segment.Sequence, segment.Revision,
                    segment.Text, null, state, segment.CapturedAt, SessionId, 1, incomplete);

            public async Task<List<PersistedRow>> RowsAsync()
            {
                var rows = new List<PersistedRow>();
                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT SourceText, TranslatedText FROM TranslationHistory WHERE SessionId = @SessionId ORDER BY Id";
                command.Parameters.AddWithValue("@SessionId", SessionId);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    rows.Add(new PersistedRow(reader.GetString(0), reader.GetString(1)));
                return rows;
            }
            public Task DrainAsync() => queue.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));

            public async ValueTask DisposeAsync()
            {
                queue.CancelPendingAndActive();
                await DrainAsync();
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
