using System.Globalization;
using System.IO;
using System.Text;
using CsvHelper;
using Microsoft.Data.Sqlite;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    public static class SQLiteHistoryLogger
    {
        public static readonly string CONNECTION_STRING = new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.Current.HistoryDatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        private static readonly LectureReviewRepository reviewRepository =
            new(CONNECTION_STRING);

        static SQLiteHistoryLogger()
        {
            InitializeDatabase();
        }

        public static void EnsureInitialized()
        {
            // Calling this method forces the type initializer to run at app startup.
        }

        private static SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(CONNECTION_STRING);
            connection.Open();
            using var busyTimeout = connection.CreateCommand();
            busyTimeout.CommandText = "PRAGMA busy_timeout=5000;";
            busyTimeout.ExecuteNonQuery();
            return connection;
        }

        private static void InitializeDatabase()
        {
            AppPaths.EnsureCreated();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS LectureSessions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    StartedAt TEXT NOT NULL,
                    EndedAt TEXT,
                    Mode TEXT NOT NULL,
                    ApiUsed TEXT,
                    TargetLanguage TEXT,
                    SummaryText TEXT
                );
                CREATE TABLE IF NOT EXISTS TranslationHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp TEXT,
                    SourceText TEXT,
                    TranslatedText TEXT,
                    TargetLanguage TEXT,
                    ApiUsed TEXT,
                    SessionId INTEGER
                );";
            command.ExecuteNonQuery();

            using var schema = connection.CreateCommand();
            schema.CommandText = "PRAGMA table_info(TranslationHistory);";
            using var reader = schema.ExecuteReader();
            bool hasSessionId = false;
            while (reader.Read())
                hasSessionId |= string.Equals(
                    reader.GetString(1), "SessionId", StringComparison.OrdinalIgnoreCase);
            reader.Close();

            if (!hasSessionId)
            {
                using var migration = connection.CreateCommand();
                migration.CommandText = "ALTER TABLE TranslationHistory ADD COLUMN SessionId INTEGER;";
                migration.ExecuteNonQuery();
            }

            using var indexes = connection.CreateCommand();
            indexes.CommandText = @"
                CREATE INDEX IF NOT EXISTS IX_TranslationHistory_SessionId_Id
                    ON TranslationHistory(SessionId, Id);
                UPDATE LectureSessions
                SET EndedAt = COALESCE(
                    (SELECT MAX(Timestamp) FROM TranslationHistory h
                     WHERE h.SessionId = LectureSessions.Id),
                    StartedAt)
                WHERE EndedAt IS NULL;";
            indexes.ExecuteNonQuery();
        }

        public static async Task<LectureSessionEntry> BeginSessionAsync(
            string mode,
            string apiUsed,
            string targetLanguage,
            CancellationToken token = default)
        {
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO LectureSessions (StartedAt, Mode, ApiUsed, TargetLanguage, SummaryText)
                VALUES (@StartedAt, @Mode, @ApiUsed, @TargetLanguage, '')
                RETURNING Id;";
            command.Parameters.AddWithValue("@StartedAt", startedAt.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("@Mode", mode);
            command.Parameters.AddWithValue("@ApiUsed", apiUsed);
            command.Parameters.AddWithValue("@TargetLanguage", targetLanguage);
            long id = Convert.ToInt64(await command.ExecuteScalarAsync(token));
            return new LectureSessionEntry
            {
                Id = id,
                StartedAt = startedAt,
                Mode = mode,
                ApiUsed = apiUsed,
                TargetLanguage = targetLanguage
            };
        }

        public static async Task EndSessionAsync(
            long sessionId,
            string? summaryText,
            CancellationToken token = default)
        {
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE LectureSessions
                SET EndedAt = @EndedAt,
                    SummaryText = CASE
                        WHEN @SummaryText IS NULL THEN SummaryText
                        ELSE @SummaryText
                    END
                WHERE Id = @Id;";
            command.Parameters.AddWithValue("@EndedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("@SummaryText", (object?)summaryText ?? DBNull.Value);
            command.Parameters.AddWithValue("@Id", sessionId);
            await command.ExecuteNonQueryAsync(token);
        }

        public static async Task SaveSessionSummaryAsync(
            long sessionId,
            string summaryText,
            CancellationToken token = default)
        {
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE LectureSessions SET SummaryText = @SummaryText WHERE Id = @Id;";
            command.Parameters.AddWithValue("@SummaryText", summaryText);
            command.Parameters.AddWithValue("@Id", sessionId);
            await command.ExecuteNonQueryAsync(token);
        }

        public static Task<LectureSessionEntry> CreateReviewSessionAsync(
            string name,
            string summaryText,
            string apiUsed,
            string targetLanguage,
            CancellationToken token = default)
        {
            return reviewRepository.CreateSessionAsync(
                name, summaryText, apiUsed, targetLanguage, token: token);
        }

        public static Task<bool> UpdateSessionAsync(
            long sessionId,
            string name,
            string summaryText,
            CancellationToken token = default)
        {
            return reviewRepository.UpdateSessionAsync(
                sessionId, name, summaryText, token);
        }

        public static Task<bool> DeleteSessionAsync(
            long? sessionId,
            CancellationToken token = default)
        {
            return reviewRepository.DeleteSessionAsync(sessionId, token);
        }

        public static Task<TranslationHistoryEntry> CreateSessionTranslationAsync(
            long? sessionId,
            string sourceText,
            string translatedText,
            string targetLanguage,
            string apiUsed,
            CancellationToken token = default)
        {
            return reviewRepository.CreateTranslationAsync(
                sessionId, sourceText, translatedText, targetLanguage, apiUsed,
                token: token);
        }

        public static Task<bool> UpdateTranslationAsync(
            long entryId,
            long? sessionId,
            string sourceText,
            string translatedText,
            CancellationToken token = default)
        {
            return reviewRepository.UpdateTranslationAsync(
                entryId, sessionId, sourceText, translatedText, token);
        }

        public static Task<bool> DeleteTranslationAsync(
            long entryId,
            long? sessionId,
            CancellationToken token = default)
        {
            return reviewRepository.DeleteTranslationAsync(entryId, sessionId, token);
        }

        public static async Task<TranslationHistoryEntry> LogTranslation(
            string sourceText,
            string translatedText,
            string targetLanguage,
            string apiUsed,
            long? sessionId = null,
            CancellationToken token = default)
        {
            DateTimeOffset timestamp = DateTimeOffset.UtcNow;
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO TranslationHistory
                    (Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed, SessionId)
                VALUES
                    (@Timestamp, @SourceText, @TranslatedText, @TargetLanguage, @ApiUsed, @SessionId)
                RETURNING Id;";
            command.Parameters.AddWithValue("@Timestamp", timestamp.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("@SourceText", sourceText);
            command.Parameters.AddWithValue("@TranslatedText", translatedText);
            command.Parameters.AddWithValue("@TargetLanguage", targetLanguage);
            command.Parameters.AddWithValue("@ApiUsed", apiUsed);
            command.Parameters.AddWithValue("@SessionId", (object?)sessionId ?? DBNull.Value);
            long id = Convert.ToInt64(await command.ExecuteScalarAsync(token));
            return MapEntry(
                id, sessionId, timestamp, sourceText, translatedText, targetLanguage, apiUsed, "");
        }

        public static async Task<List<LectureSessionEntry>> LoadSessionsAsync(
            string searchText = "",
            CancellationToken token = default)
        {
            var sessions = new List<LectureSessionEntry>();
            await using var connection = OpenConnection();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    SELECT s.Id, s.StartedAt, s.EndedAt, s.Mode, s.ApiUsed,
                           s.TargetLanguage, s.SummaryText, COUNT(h.Id) AS EntryCount
                    FROM LectureSessions s
                    LEFT JOIN TranslationHistory h ON h.SessionId = s.Id
                    WHERE @Search = ''
                       OR s.Mode LIKE @LikeSearch
                       OR COALESCE(s.SummaryText, '') LIKE @LikeSearch
                       OR COALESCE(s.ApiUsed, '') LIKE @LikeSearch
                       OR COALESCE(s.TargetLanguage, '') LIKE @LikeSearch
                       OR EXISTS (
                        SELECT 1 FROM TranslationHistory x
                        WHERE x.SessionId = s.Id
                          AND (x.SourceText LIKE @LikeSearch
                               OR x.TranslatedText LIKE @LikeSearch))
                    GROUP BY s.Id
                    ORDER BY CAST(s.StartedAt AS INTEGER) DESC;";
                command.Parameters.AddWithValue("@Search", searchText);
                command.Parameters.AddWithValue("@LikeSearch", $"%{searchText}%");
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    sessions.Add(new LectureSessionEntry
                    {
                        Id = reader.GetInt64(0),
                        StartedAt = FromUnix(reader.GetString(1)),
                        EndedAt = reader.IsDBNull(2) ? null : FromUnix(reader.GetString(2)),
                        Mode = reader.GetString(3),
                        ApiUsed = reader.IsDBNull(4) ? "N/A" : reader.GetString(4),
                        TargetLanguage = reader.IsDBNull(5) ? "N/A" : reader.GetString(5),
                        SummaryText = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                        EntryCount = reader.GetInt32(7)
                    });
                }
            }

            await using (var legacyCommand = connection.CreateCommand())
            {
                legacyCommand.CommandText = @"
                    SELECT MIN(Timestamp), MAX(Timestamp), COUNT(*)
                    FROM TranslationHistory
                    WHERE SessionId IS NULL
                      AND (@Search = '' OR SourceText LIKE @LikeSearch
                           OR TranslatedText LIKE @LikeSearch);";
                legacyCommand.Parameters.AddWithValue("@Search", searchText);
                legacyCommand.Parameters.AddWithValue("@LikeSearch", $"%{searchText}%");
                await using var reader = await legacyCommand.ExecuteReaderAsync(token);
                if (await reader.ReadAsync(token) && reader.GetInt32(2) > 0)
                {
                    sessions.Add(new LectureSessionEntry
                    {
                        Id = 0,
                        StartedAt = FromUnix(reader.GetString(0)),
                        EndedAt = FromUnix(reader.GetString(1)),
                        Mode = "历史未分组",
                        ApiUsed = "多种引擎",
                        TargetLanguage = "多种语言",
                        EntryCount = reader.GetInt32(2)
                    });
                }
            }

            return sessions.OrderByDescending(session => session.StartedAt).ToList();
        }

        public static async Task<List<TranslationHistoryEntry>> LoadSessionHistoryAsync(
            long? sessionId,
            string searchText = "",
            CancellationToken token = default)
        {
            var history = new List<TranslationHistoryEntry>();
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT Id, SessionId, Timestamp, SourceText, TranslatedText,
                       TargetLanguage, ApiUsed
                FROM TranslationHistory
                WHERE ((@SessionId IS NULL AND SessionId IS NULL) OR SessionId = @SessionId)
                  AND (SourceText LIKE @Search OR TranslatedText LIKE @Search)
                ORDER BY Id;";
            command.Parameters.AddWithValue("@SessionId", (object?)sessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("@Search", $"%{searchText}%");
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                history.Add(MapEntry(reader));
            return history;
        }

        public static async Task<(List<TranslationHistoryEntry>, int)> LoadHistoryAsync(
            int page,
            int maxRow,
            string searchText,
            CancellationToken token = default)
        {
            var history = new List<TranslationHistoryEntry>();
            await using var connection = OpenConnection();
            int totalCount;
            await using (var count = connection.CreateCommand())
            {
                count.CommandText = @"
                    SELECT COUNT(*) FROM TranslationHistory
                    WHERE SourceText LIKE @Search OR TranslatedText LIKE @Search;";
                count.Parameters.AddWithValue("@Search", $"%{searchText}%");
                totalCount = Convert.ToInt32(await count.ExecuteScalarAsync(token));
            }

            int maxPage = Math.Max(1, (int)Math.Ceiling(totalCount / (double)maxRow));
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT h.Id, h.SessionId, h.Timestamp, h.SourceText, h.TranslatedText,
                       h.TargetLanguage, h.ApiUsed,
                       CASE WHEN s.Id IS NULL THEN '历史未分组'
                            ELSE s.Mode || ' · ' ||
                                 datetime(CAST(s.StartedAt AS INTEGER), 'unixepoch', 'localtime')
                       END
                FROM TranslationHistory h
                LEFT JOIN LectureSessions s ON s.Id = h.SessionId
                WHERE h.SourceText LIKE @Search OR h.TranslatedText LIKE @Search
                ORDER BY h.Id DESC
                LIMIT @MaxRow OFFSET @Offset;";
            command.Parameters.AddWithValue("@Search", $"%{searchText}%");
            command.Parameters.AddWithValue("@MaxRow", maxRow);
            command.Parameters.AddWithValue("@Offset", Math.Max(0, (page - 1) * maxRow));
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                history.Add(MapEntry(reader, hasSessionTitle: true));
            return (history, maxPage);
        }

        public static async Task ClearHistory(CancellationToken token = default)
        {
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                DELETE FROM TranslationHistory;
                DELETE FROM LectureSessions;
                DELETE FROM sqlite_sequence
                WHERE NAME IN ('TranslationHistory', 'LectureSessions');";
            await command.ExecuteNonQueryAsync(token);
        }

        public static async Task<string> LoadLastSourceText(
            long? sessionId = null,
            CancellationToken token = default)
        {
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT SourceText FROM TranslationHistory
                WHERE ((@SessionId IS NULL AND SessionId IS NULL) OR SessionId = @SessionId)
                ORDER BY Id DESC LIMIT 1;";
            command.Parameters.AddWithValue("@SessionId", (object?)sessionId ?? DBNull.Value);
            return Convert.ToString(await command.ExecuteScalarAsync(token)) ?? string.Empty;
        }

        public static async Task<TranslationHistoryEntry?> LoadLastTranslation(
            long? sessionId = null,
            CancellationToken token = default)
        {
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT Id, SessionId, Timestamp, SourceText, TranslatedText,
                       TargetLanguage, ApiUsed
                FROM TranslationHistory
                WHERE ((@SessionId IS NULL AND SessionId IS NULL) OR SessionId = @SessionId)
                ORDER BY Id DESC LIMIT 1;";
            command.Parameters.AddWithValue("@SessionId", (object?)sessionId ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(token);
            return await reader.ReadAsync(token) ? MapEntry(reader) : null;
        }

        public static async Task<long?> DeleteLastTranslation(
            long? sessionId = null,
            CancellationToken token = default)
        {
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                DELETE FROM TranslationHistory
                WHERE Id IN (
                    SELECT Id FROM TranslationHistory
                    WHERE ((@SessionId IS NULL AND SessionId IS NULL) OR SessionId = @SessionId)
                    ORDER BY Id DESC LIMIT 1)
                RETURNING Id;";
            command.Parameters.AddWithValue("@SessionId", (object?)sessionId ?? DBNull.Value);
            object? deletedId = await command.ExecuteScalarAsync(token);
            return deletedId == null || deletedId == DBNull.Value
                ? null
                : Convert.ToInt64(deletedId);
        }

        public static async Task ExportToCSV(string filePath, CancellationToken token = default)
        {
            var (history, _) = await LoadHistoryAsync(1, int.MaxValue, string.Empty, token);
            using var writer = new StreamWriter(filePath, false, new UTF8Encoding(true));
            using var csvWriter = new CsvWriter(writer, CultureInfo.InvariantCulture);
            await csvWriter.WriteRecordsAsync(history, token);
        }

        public static async Task ExportSessionToCSV(
            string filePath,
            long? sessionId,
            CancellationToken token = default)
        {
            var history = await LoadSessionHistoryAsync(sessionId, token: token);
            using var writer = new StreamWriter(filePath, false, new UTF8Encoding(true));
            using var csvWriter = new CsvWriter(writer, CultureInfo.InvariantCulture);
            await csvWriter.WriteRecordsAsync(history, token);
        }

        private static TranslationHistoryEntry MapEntry(
            SqliteDataReader reader,
            bool hasSessionTitle = false)
        {
            long? sessionId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            return MapEntry(
                reader.GetInt64(0),
                sessionId,
                FromUnix(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                hasSessionTitle && !reader.IsDBNull(7) ? reader.GetString(7) : string.Empty);
        }

        private static TranslationHistoryEntry MapEntry(
            long id,
            long? sessionId,
            DateTimeOffset timestamp,
            string sourceText,
            string translatedText,
            string targetLanguage,
            string apiUsed,
            string session)
        {
            DateTime localTime = timestamp.LocalDateTime;
            return new TranslationHistoryEntry
            {
                Id = id,
                SessionId = sessionId,
                Session = session,
                Timestamp = localTime.ToString("MM/dd HH:mm"),
                TimestampFull = localTime.ToString("MM/dd/yy, HH:mm:ss"),
                SourceText = sourceText,
                TranslatedText = translatedText,
                TargetLanguage = targetLanguage,
                ApiUsed = apiUsed
            };
        }

        private static DateTimeOffset FromUnix(string value)
        {
            if (long.TryParse(value, out long unixTime))
                return DateTimeOffset.FromUnixTimeSeconds(unixTime);
            if (DateTimeOffset.TryParse(value, out var timestamp))
                return timestamp;
            return DateTimeOffset.UnixEpoch;
        }
    }
}
