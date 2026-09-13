using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.services
{
    internal sealed record RecordedCaption(LiveCaptionSegment Segment, bool IsIncomplete = false);

    /// <summary>
    /// Decides when an already resolved occurrence enters classroom history.
    /// Observation time stabilizes a punctuated candidate; it never establishes
    /// a new identity or turns an unresolved repeat into speech.
    /// </summary>
    internal sealed class CaptionRecordingPolicy
    {
        internal static readonly TimeSpan FinalStabilityDelay = TimeSpan.FromMilliseconds(800);

        private sealed class Candidate(LiveCaptionSegment segment, TimeSpan at)
        {
            public LiveCaptionSegment Segment = segment;
            public TimeSpan ChangedAt = at;
            public long LastSeenFrame;
            public int Observations;
            public int RecordedRevision = -1;
        }

        private readonly Dictionary<Guid, Candidate> candidates = [];
        // Admission metadata lives for the classroom, like the queue/history.
        // Evicting text-matching evidence must not invalidate slow final work.
        private readonly Dictionary<Guid, int> recordedRevisions = [];
        private long frameSequence;
        private TimeSpan lastObservedAt;
        private long frontierSequence;

        public IReadOnlyList<RecordedCaption> Observe(LiveCaptionUpdate update, TimeSpan observedAt)
        {
            // A monotonic clock is supplied by capture. Clamp external replay
            // clocks too, so wall-clock corrections cannot accelerate admission.
            observedAt = observedAt < lastObservedAt ? lastObservedAt : observedAt;
            lastObservedAt = observedAt;
            frameSequence++;
            foreach (LiveCaptionSegment segment in update.FinalizedSegments)
                Remember(segment, observedAt);
            if (update.DraftSegment != null && update.DraftIsEligible)
                Remember(update.DraftSegment, observedAt);

            LiveCaptionSegment? current = update.CurrentSegment;
            if (current != null && candidates.ContainsKey(current.Id))
                frontierSequence = Math.Max(frontierSequence, current.Sequence);

            var visibleIds = new HashSet<Guid>(update.WindowSegmentIds);
            if (current != null)
                visibleIds.Add(current.Id);
            foreach (Guid id in visibleIds)
            {
                if (candidates.TryGetValue(id, out Candidate? candidate))
                {
                    candidate.LastSeenFrame = frameSequence;
                    candidate.Observations++;
                }
            }

            var recorded = new List<RecordedCaption>();
            foreach (Candidate candidate in candidates.Values.OrderBy(value => value.Segment.Sequence))
            {
                LiveCaptionSegment segment = candidate.Segment;
                if (candidate.RecordedRevision >= segment.Revision)
                    continue;

                bool hasSuccessor = frontierSequence > segment.Sequence;
                bool complete = segment.IsFinal && IsCompleteSentence(segment.Text);
                bool stable = candidate.LastSeenFrame == frameSequence &&
                              candidate.Observations >= 2 &&
                              observedAt - candidate.ChangedAt >= FinalStabilityDelay;
                // A new draft is not proof that an abandoned partial was a
                // complete sentence. Keep unfinished words for explicit flush.
                if (!complete)
                    continue;
                if (hasSuccessor || stable)
                {
                    candidate.RecordedRevision = segment.Revision;
                    recordedRevisions[segment.Id] = segment.Revision;
                    recorded.Add(new RecordedCaption(segment with { IsFinal = true }, !complete));
                }
                else
                {
                    // Do not let a later record overtake an unresolved earlier
                    // occurrence. Source-ambiguous repeats never enter this map.
                    break;
                }
            }

            TrimRecordedHistory();
            return recorded;
        }

        public IReadOnlyList<RecordedCaption> Flush()
        {
            var recorded = new List<RecordedCaption>();
            foreach (Candidate candidate in candidates.Values.OrderBy(value => value.Segment.Sequence))
            {
                LiveCaptionSegment segment = candidate.Segment;
                if (candidate.RecordedRevision >= segment.Revision)
                    continue;
                candidate.RecordedRevision = segment.Revision;
                recordedRevisions[segment.Id] = segment.Revision;
                recorded.Add(new RecordedCaption(segment with { IsFinal = true },
                    !IsCompleteSentence(segment.Text)));
            }
            return recorded;
        }

        public bool IsRecorded(Guid id, int revision) =>
            candidates.TryGetValue(id, out Candidate? candidate) &&
            candidate.RecordedRevision == revision;

        private static bool IsCompleteSentence(string text) =>
            TranslationTextPolicy.IsCompleteSentence(text.TrimEnd(
                ' ', '\"', '\'', '”', '’', ')', ']', '}', '）', '」', '』', '】'));

        public bool IsCurrentRevision(TranslationTaskIdentity identity)
        {
            if (candidates.TryGetValue(identity.SegmentId, out Candidate? candidate))
                return candidate.Segment.Revision == identity.Revision &&
                       (!identity.IsFinal || candidate.RecordedRevision == identity.Revision);
            return identity.IsFinal &&
                   recordedRevisions.TryGetValue(identity.SegmentId, out int revision) &&
                   revision == identity.Revision;
        }

        public void Reset()
        {
            candidates.Clear();
            recordedRevisions.Clear();
            frameSequence = 0;
            lastObservedAt = TimeSpan.Zero;
            frontierSequence = 0;
        }

        private void Remember(LiveCaptionSegment segment, TimeSpan observedAt)
        {
            if (!candidates.TryGetValue(segment.Id, out Candidate? candidate))
            {
                candidates[segment.Id] = new Candidate(segment, observedAt)
                {
                    RecordedRevision = recordedRevisions.GetValueOrDefault(segment.Id, -1)
                };
                return;
            }
            if (segment.Revision < candidate.Segment.Revision)
                return;
            if (segment.Revision > candidate.Segment.Revision ||
                !string.Equals(segment.Text, candidate.Segment.Text, StringComparison.Ordinal) ||
                segment.IsFinal != candidate.Segment.IsFinal)
            {
                candidate.Segment = segment;
                candidate.ChangedAt = observedAt;
                candidate.Observations = 0;
            }
        }

        private void TrimRecordedHistory()
        {
            foreach (Candidate candidate in candidates.Values
                         .OrderByDescending(value => value.Segment.Sequence)
                         .Skip(LiveCaptionSegmentationThresholds.RecentLedgerCapacity).ToArray())
            {
                if (candidate.RecordedRevision >= candidate.Segment.Revision)
                    candidates.Remove(candidate.Segment.Id);
            }
        }
    }
}
