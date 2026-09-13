namespace LiveCaptionsTranslator.models
{
    public enum SegmentState
    {
        Draft,
        Committed,
        Queued,
        Translating,
        Translated,
        TranslationFailed
    }

    public sealed record TranscriptSegment(
        Guid Id,
        long Sequence,
        int Revision,
        string SourceText,
        string? TranslatedText,
        SegmentState State,
        DateTimeOffset CapturedAt,
        long? SessionId = null,
        long CaptureEpoch = 0,
        bool IsIncomplete = false);

    public sealed record TranslationRequest(
        Guid SegmentId,
        long Sequence,
        int Revision,
        string SourceText,
        string TargetLanguage);

    public sealed record TranslationResult(
        Guid SegmentId,
        long Sequence,
        int Revision,
        string? TranslatedText,
        string? ErrorMessage,
        TimeSpan Elapsed)
    {
        public bool IsSuccess => ErrorMessage == null;
    }
}
