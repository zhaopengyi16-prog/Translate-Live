using System.Text;

namespace LiveCaptionsTranslator.services.recognition
{
    /// <summary>
    /// Converts engine callbacks into stable, revision-aware utterance events.
    /// The session owns identity only; audio capture and ASR implementation are
    /// deliberately outside this boundary.
    /// </summary>
    internal sealed class LocalRecognitionSession
    {
        public const int DefaultEventCapacity = 128;

        private readonly object stateLock = new();
        private readonly Queue<RecognitionEvent> recentEvents = [];
        private readonly Func<Guid> segmentIdFactory;
        private readonly int eventCapacity;

        private long sessionId;
        private long captureEpoch;
        private long lastSourceRevision = -1;
        private long nextSequence = 1;
        private RecognitionEvent? activeUtterance;
        private bool hasActiveSession;
        private bool isStopped;

        public LocalRecognitionSession(
            int eventCapacity = DefaultEventCapacity,
            Func<Guid>? segmentIdFactory = null)
        {
            if (eventCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(eventCapacity));

            this.eventCapacity = eventCapacity;
            this.segmentIdFactory = segmentIdFactory ?? Guid.NewGuid;
        }

        public void BeginSession(long newSessionId, long newCaptureEpoch)
        {
            if (newSessionId <= 0)
                throw new ArgumentOutOfRangeException(nameof(newSessionId));
            if (newCaptureEpoch < 0)
                throw new ArgumentOutOfRangeException(nameof(newCaptureEpoch));

            lock (stateLock)
            {
                sessionId = newSessionId;
                captureEpoch = newCaptureEpoch;
                lastSourceRevision = -1;
                nextSequence = 1;
                activeUtterance = null;
                recentEvents.Clear();
                hasActiveSession = true;
                isStopped = false;
            }
        }

        public RecognitionAcceptance Accept(RecognitionUpdate update)
        {
            ArgumentNullException.ThrowIfNull(update);

            lock (stateLock)
            {
                RecognitionAcceptanceStatus? rejection = Validate(update);
                if (rejection != null)
                    return new RecognitionAcceptance(rejection.Value);

                // Advancing this guard even for an idempotent callback prevents
                // an older engine result from being accepted afterwards.
                lastSourceRevision = update.SourceRevision;

                string normalizedText = NormalizeText(update.Text);
                if (normalizedText.Length == 0)
                {
                    if (!update.IsFinal || activeUtterance == null)
                    {
                        return new RecognitionAcceptance(
                            RecognitionAcceptanceStatus.IgnoredEmptyText);
                    }

                    normalizedText = activeUtterance.Text;
                }

                RecognitionEndpointReason endpointReason = update.IsFinal
                    ? update.EndpointReason == RecognitionEndpointReason.None
                        ? RecognitionEndpointReason.RecognizerFinal
                        : update.EndpointReason
                    : RecognitionEndpointReason.None;

                if (activeUtterance == null)
                {
                    RecognitionEvent created = new(
                        sessionId,
                        captureEpoch,
                        CreateSegmentId(),
                        Revision: 0,
                        Sequence: nextSequence++,
                        CapturedAt: update.ObservedAt,
                        Text: normalizedText,
                        IsFinal: update.IsFinal,
                        EndpointReason: endpointReason);

                    Append(created);
                    activeUtterance = update.IsFinal ? null : created;
                    return new RecognitionAcceptance(
                        RecognitionAcceptanceStatus.Accepted,
                        created);
                }

                bool textChanged = !string.Equals(
                    activeUtterance.Text,
                    normalizedText,
                    StringComparison.Ordinal);
                bool stateChanged = activeUtterance.IsFinal != update.IsFinal ||
                    activeUtterance.EndpointReason != endpointReason;
                if (!textChanged && !stateChanged)
                {
                    return new RecognitionAcceptance(
                        RecognitionAcceptanceStatus.NoChange);
                }

                RecognitionEvent revised = activeUtterance with
                {
                    Revision = activeUtterance.Revision + 1,
                    Text = normalizedText,
                    IsFinal = update.IsFinal,
                    EndpointReason = endpointReason
                };
                Append(revised);
                activeUtterance = update.IsFinal ? null : revised;
                return new RecognitionAcceptance(
                    RecognitionAcceptanceStatus.Accepted,
                    revised);
            }
        }

        public RecognitionAcceptance Stop(bool finalizeUnfinished)
        {
            lock (stateLock)
            {
                if (!hasActiveSession)
                {
                    return new RecognitionAcceptance(
                        RecognitionAcceptanceStatus.RejectedNoActiveSession);
                }
                if (isStopped)
                {
                    return new RecognitionAcceptance(
                        RecognitionAcceptanceStatus.RejectedStopped);
                }

                isStopped = true;
                if (!finalizeUnfinished || activeUtterance == null)
                {
                    activeUtterance = null;
                    return new RecognitionAcceptance(
                        RecognitionAcceptanceStatus.NoChange);
                }

                RecognitionEvent finalized = activeUtterance with
                {
                    Revision = activeUtterance.Revision + 1,
                    IsFinal = true,
                    EndpointReason = RecognitionEndpointReason.SessionStopped
                };
                activeUtterance = null;
                Append(finalized);
                return new RecognitionAcceptance(
                    RecognitionAcceptanceStatus.Accepted,
                    finalized);
            }
        }

        public RecognitionEvent? ActiveUtterance
        {
            get
            {
                lock (stateLock)
                    return activeUtterance;
            }
        }

        public IReadOnlyList<RecognitionEvent> RecentEvents
        {
            get
            {
                lock (stateLock)
                    return recentEvents.ToArray();
            }
        }

        private RecognitionAcceptanceStatus? Validate(RecognitionUpdate update)
        {
            if (!hasActiveSession)
                return RecognitionAcceptanceStatus.RejectedNoActiveSession;
            if (isStopped)
                return RecognitionAcceptanceStatus.RejectedStopped;
            if (update.SessionId != sessionId)
                return RecognitionAcceptanceStatus.RejectedSession;
            if (update.CaptureEpoch != captureEpoch)
                return RecognitionAcceptanceStatus.RejectedCaptureEpoch;
            if (update.SourceRevision <= lastSourceRevision)
                return RecognitionAcceptanceStatus.RejectedSourceRevision;

            return null;
        }

        private Guid CreateSegmentId()
        {
            Guid id = segmentIdFactory();
            if (id == Guid.Empty)
                throw new InvalidOperationException("Segment id factory returned an empty id.");
            return id;
        }

        private void Append(RecognitionEvent recognitionEvent)
        {
            recentEvents.Enqueue(recognitionEvent);
            while (recentEvents.Count > eventCapacity)
                recentEvents.Dequeue();
        }

        private static string NormalizeText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var normalized = new StringBuilder(text.Length);
            bool previousWasWhitespace = true;
            foreach (char character in text)
            {
                if (char.IsWhiteSpace(character))
                {
                    if (!previousWasWhitespace)
                        normalized.Append(' ');
                    previousWasWhitespace = true;
                    continue;
                }

                normalized.Append(character);
                previousWasWhitespace = false;
            }

            if (normalized.Length > 0 && normalized[^1] == ' ')
                normalized.Length--;
            return normalized.ToString();
        }
    }
}
