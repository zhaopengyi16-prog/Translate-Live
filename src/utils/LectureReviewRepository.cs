using Microsoft.Data.Sqlite;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    internal sealed class LectureReviewRepository
    {
        private readonly string connectionString;

        public LectureReviewRepository(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("Connection string cannot be empty.", nameof(connectionString));
            this.connectionString = connectionString;
        }

        public async Task<LectureSessionEntry> CreateSessionAsync(
            string name,
            string summaryText,
            string apiUsed,
            string targetLanguage,
            DateTimeOffset? createdAt = null,
            CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Session name cannot be empty.", nameof(name));

            DateTimeOffset timestamp = createdAt ?? DateTimeOffset.UtcNow;
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO LectureSessions
                    (StartedAt, EndedAt, Mode, ApiUsed, TargetLanguage, SummaryText)
                VALUES
                    (@StartedAt, @EndedAt, @Mode, @ApiUsed, @TargetLanguage, @SummaryText)
                RETURNING Id;";
            command.Parameters.AddWithValue("@StartedAt", timestamp.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("@EndedAt", timestamp.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("@Mode", name.Trim());
            command.Parameters.AddWithValue("@ApiUsed", apiUsed);
            command.Parameters.AddWithValue("@TargetLanguage", targetLanguage);
            command.Parameters.AddWithValue("@SummaryText", summaryText.Trim());
            long id = Convert.ToInt64(await command.ExecuteScalarAsync(token));

            return new LectureSessionEntry
            {
                Id = id,
                StartedAt = timestamp,
                EndedAt = timestamp,
                Mode = name.Trim(),
                ApiUsed = apiUsed,
                TargetLanguage = targetLanguage,
                SummaryText = summaryText.Trim()
            };
        }

        public async Task<bool> UpdateSessionAsync(
            long sessionId,
            string name,
            string summaryText,
            CancellationToken token = default)
        {
            if (sessionId <= 0)
                return false;
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Session name cannot be empty.", nameof(name));

            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE LectureSessions
                SET Mode = @Mode,
                    SummaryText = @SummaryText
                WHERE Id = @Id;";
            command.Parameters.AddWithValue("@Mode", name.Trim());
            command.Parameters.AddWithValue("@SummaryText", summaryText.Trim());
            command.Parameters.AddWithValue("@Id", sessionId);
            return await command.ExecuteNonQueryAsync(token) == 1;
        }

        public async Task<bool> DeleteSessionAsync(
            long? sessionId,
            CancellationToken token = default)
        {
            await using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            if (sessionId.HasValue)
            {
                await using var deleteSession = connection.CreateCommand();
                deleteSession.Transaction = transaction;
                deleteSession.CommandText = "DELETE FROM LectureSessions WHERE Id = @Id;";
                deleteSession.Parameters.AddWithValue("@Id", sessionId.Value);
                if (await deleteSession.ExecuteNonQueryAsync(token) != 1)
                {
                    await transaction.RollbackAsync(token);
                    return false;
                }
            }

            await using var deleteEntries = connection.CreateCommand();
            deleteEntries.Transaction = transaction;
            deleteEntries.CommandText = sessionId.HasValue
                ? "DELETE FROM TranslationHistory WHERE SessionId = @SessionId;"
                : "DELETE FROM TranslationHistory WHERE SessionId IS NULL;";
            if (sessionId.HasValue)
                deleteEntries.Parameters.AddWithValue("@SessionId", sessionId.Value);
            int deletedEntries = await deleteEntries.ExecuteNonQueryAsync(token);

            await transaction.CommitAsync(token);
            return sessionId.HasValue || deletedEntries > 0;
        }

        public async Task<TranslationHistoryEntry> CreateTranslationAsync(
            long? sessionId,
            string sourceText,
            string translatedText,
            string targetLanguage,
            string apiUsed,
            DateTimeOffset? capturedAt = null,
            CancellationToken token = default)
        {
            ValidateTranslationText(sourceText, translatedText);
            DateTimeOffset timestamp = capturedAt ?? DateTimeOffset.UtcNow;

            await using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            if (sessionId.HasValue)
            {
                await using var sessionCheck = connection.CreateCommand();
                sessionCheck.Transaction = transaction;
                sessionCheck.CommandText =
                    "SELECT EXISTS(SELECT 1 FROM LectureSessions WHERE Id = @Id);";
                sessionCheck.Parameters.AddWithValue("@Id", sessionId.Value);
                bool sessionExists = Convert.ToInt64(
                    await sessionCheck.ExecuteScalarAsync(token)) == 1;
                if (!sessionExists)
                {
                    await transaction.RollbackAsync(token);
                    throw new InvalidOperationException("The selected lecture no longer exists.");
                }
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO TranslationHistory
                    (Timestamp, SourceText, TranslatedText, TargetLanguage, ApiUsed, SessionId)
                VALUES
                    (@Timestamp, @SourceText, @TranslatedText, @TargetLanguage, @ApiUsed, @SessionId)
                RETURNING Id;";
            command.Parameters.AddWithValue("@Timestamp", timestamp.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("@SourceText", sourceText.Trim());
            command.Parameters.AddWithValue("@TranslatedText", translatedText.Trim());
            command.Parameters.AddWithValue("@TargetLanguage", targetLanguage);
            command.Parameters.AddWithValue("@ApiUsed", apiUsed);
            command.Parameters.AddWithValue("@SessionId", (object?)sessionId ?? DBNull.Value);
            long id = Convert.ToInt64(await command.ExecuteScalarAsync(token));
            await transaction.CommitAsync(token);

            DateTime localTime = timestamp.LocalDateTime;
            return new TranslationHistoryEntry
            {
                Id = id,
                SessionId = sessionId,
                Timestamp = localTime.ToString("MM/dd HH:mm"),
                TimestampFull = localTime.ToString("MM/dd/yy, HH:mm:ss"),
                SourceText = sourceText.Trim(),
                TranslatedText = translatedText.Trim(),
                TargetLanguage = targetLanguage,
                ApiUsed = apiUsed
            };
        }

        public async Task<bool> UpdateTranslationAsync(
            long entryId,
            long? sessionId,
            string sourceText,
            string translatedText,
            CancellationToken token = default)
        {
            ValidateTranslationText(sourceText, translatedText);

            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE TranslationHistory
                SET SourceText = @SourceText,
                    TranslatedText = @TranslatedText
                WHERE Id = @Id
                  AND ((@SessionId IS NULL AND SessionId IS NULL) OR SessionId = @SessionId);";
            command.Parameters.AddWithValue("@SourceText", sourceText.Trim());
            command.Parameters.AddWithValue("@TranslatedText", translatedText.Trim());
            command.Parameters.AddWithValue("@Id", entryId);
            command.Parameters.AddWithValue("@SessionId", (object?)sessionId ?? DBNull.Value);
            return await command.ExecuteNonQueryAsync(token) == 1;
        }

        public async Task<TranslationHistoryEntry?> UpdateTranslationSnapshotAsync(
            long entryId,
            long? sessionId,
            string sourceText,
            string translatedText,
            string targetLanguage,
            string apiUsed,
            CancellationToken token = default)
        {
            ValidateTranslationText(sourceText, translatedText);

            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE TranslationHistory
                SET SourceText = @SourceText,
                    TranslatedText = @TranslatedText,
                    TargetLanguage = @TargetLanguage,
                    ApiUsed = @ApiUsed
                WHERE Id = @Id
                  AND ((@SessionId IS NULL AND SessionId IS NULL) OR SessionId = @SessionId)
                RETURNING Id, SessionId, Timestamp, SourceText, TranslatedText,
                          TargetLanguage, ApiUsed;";
            command.Parameters.AddWithValue("@Id", entryId);
            command.Parameters.AddWithValue("@SessionId", (object?)sessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("@SourceText", sourceText.Trim());
            command.Parameters.AddWithValue("@TranslatedText", translatedText.Trim());
            command.Parameters.AddWithValue("@TargetLanguage", targetLanguage);
            command.Parameters.AddWithValue("@ApiUsed", apiUsed);
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token))
                return null;

            long unixTimestamp = Convert.ToInt64(
                reader.GetValue(2),
                System.Globalization.CultureInfo.InvariantCulture);
            DateTime localTime = DateTimeOffset
                .FromUnixTimeSeconds(unixTimestamp)
                .LocalDateTime;
            return new TranslationHistoryEntry
            {
                Id = reader.GetInt64(0),
                SessionId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                Timestamp = localTime.ToString("MM/dd HH:mm"),
                TimestampFull = localTime.ToString("MM/dd/yy, HH:mm:ss"),
                SourceText = reader.GetString(3),
                TranslatedText = reader.GetString(4),
                TargetLanguage = reader.GetString(5),
                ApiUsed = reader.GetString(6)
            };
        }

        public async Task<bool> DeleteTranslationAsync(
            long entryId,
            long? sessionId,
            CancellationToken token = default)
        {
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                DELETE FROM TranslationHistory
                WHERE Id = @Id
                  AND ((@SessionId IS NULL AND SessionId IS NULL) OR SessionId = @SessionId);";
            command.Parameters.AddWithValue("@Id", entryId);
            command.Parameters.AddWithValue("@SessionId", (object?)sessionId ?? DBNull.Value);
            return await command.ExecuteNonQueryAsync(token) == 1;
        }

        private SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var busyTimeout = connection.CreateCommand();
            busyTimeout.CommandText = "PRAGMA busy_timeout=5000;";
            busyTimeout.ExecuteNonQuery();
            return connection;
        }

        private static void ValidateTranslationText(string sourceText, string translatedText)
        {
            if (string.IsNullOrWhiteSpace(sourceText))
                throw new ArgumentException("Source text cannot be empty.", nameof(sourceText));
            if (string.IsNullOrWhiteSpace(translatedText))
                throw new ArgumentException("Translated text cannot be empty.", nameof(translatedText));
        }
    }
}
