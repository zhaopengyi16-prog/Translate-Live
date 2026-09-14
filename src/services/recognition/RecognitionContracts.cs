namespace LiveCaptionsTranslator.services.recognition
{
    internal enum RecognitionEndpointReason
    {
        None,
        RecognizerFinal,
        VoiceActivity,
        Silence,
        MaximumDuration,
        SessionStopped
    }

    internal sealed record RecognitionEvent(
        long SessionId,
        long CaptureEpoch,
        Guid SegmentId,
        int Revision,
        long Sequence,
        DateTimeOffset CapturedAt,
        string Text,
        bool IsFinal,
        RecognitionEndpointReason EndpointReason);

    /// <summary>
    /// One callback from a streaming recognition engine. SourceRevision must
    /// increase monotonically within a capture epoch, including callbacks that
    /// do not change the recognized text.
    /// </summary>
    internal sealed record RecognitionUpdate(
        long SessionId,
        long CaptureEpoch,
        long SourceRevision,
        DateTimeOffset ObservedAt,
        string Text,
        bool IsFinal,
        RecognitionEndpointReason EndpointReason = RecognitionEndpointReason.None);

    internal enum RecognitionAcceptanceStatus
    {
        Accepted,
        NoChange,
        IgnoredEmptyText,
        RejectedNoActiveSession,
        RejectedSession,
        RejectedCaptureEpoch,
        RejectedSourceRevision,
        RejectedStopped
    }

    internal sealed record RecognitionAcceptance(
        RecognitionAcceptanceStatus Status,
        RecognitionEvent? Event = null)
    {
        public bool IsAccepted =>
            Status == RecognitionAcceptanceStatus.Accepted && Event != null;
    }
}
