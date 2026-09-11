using Microsoft.Data.Sqlite;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class LectureReviewRepositoryTests
    {
        [TestMethod]
        public async Task SessionAndTranslationCrudRoundTripsInAnIsolatedDatabase()
        {
            string root = Path.Combine(
                Path.GetTempPath(), "LectureCopilot.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string databaseFile = Path.Combine(root, "history.db");
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databaseFile,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            try
            {
                await CreateSchemaAsync(connectionString);
                var repository = new LectureReviewRepository(connectionString);
                var timestamp = new DateTimeOffset(2026, 9, 10, 8, 30, 0, TimeSpan.Zero);

                var session = await repository.CreateSessionAsync(
                    "人工整理课",
                    "初始总结",
                    "OpenAI / deepseek-v4-flash",
                    "zh-CN",
                    timestamp);
                var entry = await repository.CreateTranslationAsync(
                    session.Id,
                    "Original sentence.",
                    "原始译文。",
                    "zh-CN",
                    "OpenAI",
                    timestamp);

                Assert.IsTrue(await repository.UpdateSessionAsync(
                    session.Id, "修改后的课程", "修改后的总结"));
                Assert.IsTrue(await repository.UpdateTranslationAsync(
                    entry.Id, session.Id, "Edited sentence.", "修改后的译文。"));

                await using (var connection = new SqliteConnection(connectionString))
                {
                    await connection.OpenAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandText = @"
                        SELECT s.Mode, s.SummaryText, h.SourceText, h.TranslatedText
                        FROM LectureSessions s
                        JOIN TranslationHistory h ON h.SessionId = s.Id
                        WHERE s.Id = @Id;";
                    command.Parameters.AddWithValue("@Id", session.Id);
                    await using var reader = await command.ExecuteReaderAsync();
                    Assert.IsTrue(await reader.ReadAsync());
                    Assert.AreEqual("修改后的课程", reader.GetString(0));
                    Assert.AreEqual("修改后的总结", reader.GetString(1));
                    Assert.AreEqual("Edited sentence.", reader.GetString(2));
                    Assert.AreEqual("修改后的译文。", reader.GetString(3));
                }

                Assert.IsTrue(await repository.DeleteTranslationAsync(entry.Id, session.Id));
                await repository.CreateTranslationAsync(
                    session.Id, "Second.", "第二。", "zh-CN", "OpenAI", timestamp);
                Assert.IsTrue(await repository.DeleteSessionAsync(session.Id));
                Assert.AreEqual(0L, await CountAsync(connectionString, "LectureSessions"));
                Assert.AreEqual(0L, await CountAsync(connectionString, "TranslationHistory"));
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public async Task EntryMutationCannotCrossSessionBoundary()
        {
            string root = Path.Combine(
                Path.GetTempPath(), "LectureCopilot.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string databaseFile = Path.Combine(root, "history.db");
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databaseFile,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            try
            {
                await CreateSchemaAsync(connectionString);
                var repository = new LectureReviewRepository(connectionString);
                var first = await repository.CreateSessionAsync("第一课", "", "OpenAI", "zh-CN");
                var second = await repository.CreateSessionAsync("第二课", "", "OpenAI", "zh-CN");
                var entry = await repository.CreateTranslationAsync(
                    first.Id, "Original.", "译文。", "zh-CN", "OpenAI");

                Assert.IsFalse(await repository.UpdateTranslationAsync(
                    entry.Id, second.Id, "Wrong.", "错误。"));
                Assert.IsFalse(await repository.DeleteTranslationAsync(entry.Id, second.Id));
                Assert.AreEqual(1L, await CountAsync(connectionString, "TranslationHistory"));
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public async Task EntryCannotBeCreatedForMissingSession()
        {
            string root = Path.Combine(
                Path.GetTempPath(), "LectureCopilot.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string databaseFile = Path.Combine(root, "history.db");
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databaseFile,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            try
            {
                await CreateSchemaAsync(connectionString);
                var repository = new LectureReviewRepository(connectionString);

                bool rejected = false;
                try
                {
                    await repository.CreateTranslationAsync(
                        999, "Original.", "译文。", "zh-CN", "手动编辑");
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }

                Assert.IsTrue(rejected);
                Assert.AreEqual(0L, await CountAsync(connectionString, "TranslationHistory"));
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
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

        private static async Task<long> CountAsync(string connectionString, string tableName)
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
    }
}
