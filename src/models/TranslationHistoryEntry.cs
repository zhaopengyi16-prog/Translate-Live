using CsvHelper.Configuration.Attributes;

namespace LiveCaptionsTranslator.models
{
    public class TranslationHistoryEntry
    {
        [Ignore]
        public long Id { get; set; }
        [Ignore]
        public long? SessionId { get; set; }
        public string Session { get; set; } = string.Empty;
        public required string Timestamp { get; set; }
        [Ignore]
        public required string TimestampFull { get; set; }
        public required string SourceText { get; set; }
        public required string TranslatedText { get; set; }
        public required string TargetLanguage { get; set; }
        public required string ApiUsed { get; set; }
    }

    public sealed record TranslationHistoryChange(
        TranslationHistoryEntry Entry,
        long? ReplacedEntryId);

    public sealed class LectureSessionEntry
    {
        public long Id { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        public string Mode { get; set; } = string.Empty;
        public string ApiUsed { get; set; } = string.Empty;
        public string TargetLanguage { get; set; } = string.Empty;
        public int EntryCount { get; set; }
        public string SummaryText { get; set; } = string.Empty;
        public bool IsLegacy => Id == 0;
        public string Title => IsLegacy
            ? "历史未分组"
            : $"{StartedAt.LocalDateTime:MM月dd日 HH:mm} · {Mode}";
        public string Detail => $"{EntryCount} 条 · {ApiUsed} · {TargetLanguage}";
        public string Duration => IsLegacy
            ? "旧版本记录"
            : EndedAt.HasValue
                ? $"{StartedAt.LocalDateTime:HH:mm}–{EndedAt.Value.LocalDateTime:HH:mm}"
                : "进行中";
    }
}
