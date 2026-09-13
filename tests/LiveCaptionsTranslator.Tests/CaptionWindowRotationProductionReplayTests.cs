using Microsoft.Data.Sqlite;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.services;
using LiveCaptionsTranslator.utils;
using LiveCaptionsTranslator.viewmodels;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class CaptionWindowRotationProductionReplayTests
    {
        [TestMethod]
        public async Task RotatedWindowAddsNoTranslationUiOrDatabaseRows()
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
                    "窗口旋转隔离回放",
                    "",
                    "Fake",
                    "zh-CN");
                TranslationSegmentPersistence persistence =
                    CreatePersistence(repository);
                int persistedCallbacks = 0;
                int translationCalls = 0;
                var viewModel = new TranscriptSessionViewModel();
                var releaseFirst = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var firstStarted = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var queue = new TranslationTaskQueue(
                    async (result, token) =>
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
                    },
                    result => viewModel.ApplySegment(new TranscriptSegment(
                        result.Identity!.SegmentId,
                        result.Identity.Sequence,
                        result.Identity.Revision,
                        result.OriginalText,
                        result.TranslatedText,
                        SegmentState.Translated,
                        result.Identity.CapturedAt)));
                var resolver = new LiveCaptionIdentityResolver();
                var emitted = new List<LiveCaptionSegment>();
                DateTimeOffset start = new(
                    2026, 9, 14, 0, 3, 47, TimeSpan.Zero);
                string first =
                    "Concentrated focus is about how to use your attention during a study session.";
                string second = "In practice is very simple.";
                string third =
                    "One task, one resource, one short time window, usually twenty to.";
                (string Text, DateTimeOffset At)[] replay =
                [
                    ($"{first} {second} {third}", start),
                    ($"{second} {third} {first}", start.AddMilliseconds(250))
                ];

                foreach ((string snapshot, DateTimeOffset observedAt) in replay)
                {
                    LiveCaptionUpdate update = resolver.Process(snapshot, observedAt);
                    foreach (LiveCaptionSegment segment in update.FinalizedSegments)
                    {
                        emitted.Add(segment);
                        queue.Enqueue(
                            async (token, _) =>
                            {
                                int call = Interlocked.Increment(ref translationCalls);
                                if (call == 1)
                                {
                                    firstStarted.SetResult();
                                    await releaseFirst.Task.WaitAsync(token);
                                }
                                return ($"译文 {segment.Sequence}", true);
                            },
                            segment.Text,
                            "Fake",
                            session.Id,
                            "zh-CN",
                            Identity(segment),
                            waitForPersistence: true);
                    }
                }

                await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                releaseFirst.SetResult();
                await WaitUntilAsync(() =>
                    Volatile.Read(ref persistedCallbacks) == emitted.Count);
                await queue.WaitForPersistenceAsync().WaitAsync(TimeSpan.FromSeconds(2));
                int databaseRows = await CountRowsAsync(
                    connectionString,
                    session.Id);
                string actual = string.Join(
                    "/",
                    emitted.Select(segment => segment.Id).Distinct().Count(),
                    translationCalls,
                    viewModel.Segments.Count,
                    databaseRows);

                Assert.AreEqual(
                    "3/3/3/3",
                    actual,
                    "格式为逻辑身份数/翻译请求数/时间线行数/数据库行数。");
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public async Task ContinuousTenSentenceCorrectionsStayTenUiAndDatabaseRows()
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
                    "连续十句隔离回放",
                    "",
                    "Fake",
                    "zh-CN");
                TranslationSegmentPersistence persistence =
                    CreatePersistence(repository);
                int persistedCallbacks = 0;
                int translationCalls = 0;
                var viewModel = new TranscriptSessionViewModel();
                var queue = new TranslationTaskQueue(
                    async (result, token) =>
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
                    },
                    result => viewModel.ApplySegment(new TranscriptSegment(
                        result.Identity!.SegmentId,
                        result.Identity.Sequence,
                        result.Identity.Revision,
                        result.OriginalText,
                        result.TranslatedText,
                        SegmentState.Translated,
                        result.Identity.CapturedAt)));
                var resolver = new LiveCaptionIdentityResolver();
                var emitted = new List<LiveCaptionSegment>();
                DateTimeOffset observedAt = new(
                    2026, 9, 14, 2, 7, 1, TimeSpan.Zero);
                string[] sentences =
                [
                    "Alpha introduces the topic clearly.",
                    "Bravo presents the first example.",
                    "Charlie compares two learning methods.",
                    "Delta describes the desired outcome.",
                    "Echo contains the changing recognizer text.",
                    "Foxtrot links the result to practice.",
                    "Golf checks the supporting evidence.",
                    "Hotel explains the remaining detail.",
                    "India summarizes the main conclusion.",
                    "Juliet finishes the continuous passage."
                ];

                async Task ApplyUpdateAsync(LiveCaptionUpdate update)
                {
                    foreach (LiveCaptionSegment segment in update.FinalizedSegments)
                    {
                        emitted.Add(segment);
                        queue.Enqueue(
                            (_, _) =>
                            {
                                Interlocked.Increment(ref translationCalls);
                                return Task.FromResult<(string, bool)>(
                                    ($"译文 {segment.Sequence}/{segment.Revision}", true));
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

                for (int spoken = 1; spoken <= sentences.Length; spoken++)
                {
                    LiveCaptionUpdate spokenUpdate = resolver.Process(
                        string.Join(' ', sentences.Take(spoken)),
                        observedAt);
                    await ApplyUpdateAsync(spokenUpdate);
                    observedAt = observedAt.AddMilliseconds(90);
                }

                for (int frame = 1; frame <= 18; frame++)
                {
                    sentences[4] =
                        $"Recognition frame {frame} fully replaces the middle caption wording.";
                    LiveCaptionUpdate corrected = resolver.Process(
                        string.Join(' ', sentences),
                        observedAt);
                    await ApplyUpdateAsync(corrected);
                    observedAt = observedAt.AddMilliseconds(90);
                }

                await WaitUntilAsync(() =>
                    Volatile.Read(ref persistedCallbacks) == emitted.Count);
                await queue.WaitForPersistenceAsync().WaitAsync(TimeSpan.FromSeconds(2));
                int databaseRows = await CountRowsAsync(connectionString, session.Id);

                Assert.AreEqual(28, emitted.Count);
                Assert.AreEqual(10, emitted.Select(segment => segment.Id).Distinct().Count());
                Assert.AreEqual(28, translationCalls);
                Assert.AreEqual(10, viewModel.Segments.Count);
                Assert.AreEqual(10, databaseRows);
                Assert.AreEqual(18, viewModel.Segments.Single(
                    segment => segment.Sequence == 5).Revision);
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        private static TranslationTaskIdentity Identity(
            LiveCaptionSegment segment)
        {
            return new TranslationTaskIdentity(
                segment.Id,
                segment.Sequence,
                segment.Revision,
                segment.IsFinal,
                segment.CapturedAt);
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

        private static async Task<int> CountRowsAsync(
            string connectionString,
            long sessionId)
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT COUNT(*)
                FROM TranslationHistory
                WHERE SessionId = @SessionId;";
            command.Parameters.AddWithValue("@SessionId", sessionId);
            object? value = await command.ExecuteScalarAsync();
            return Convert.ToInt32(
                value,
                System.Globalization.CultureInfo.InvariantCulture);
        }

        private static async Task WaitUntilAsync(Func<bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!predicate())
                await Task.Delay(10, timeout.Token);
        }
    }
}
