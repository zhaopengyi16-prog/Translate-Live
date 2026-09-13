namespace LiveCaptionsTranslator.services
{
    internal readonly record struct FinalCaptionAdmissionDecision(
        bool IsAdmitted,
        LiveCaptionSegment CanonicalSegment);

    /// <summary>
    /// Final version boundary for identities already resolved by
    /// <see cref="LiveCaptionSegmenter"/>. It never invents aliases or infers
    /// identity from text, time, or intervening captions. Its only job is to
    /// reject duplicate and stale revisions for the same SegmentId.
    /// </summary>
    internal sealed class FinalCaptionAdmissionGate
    {
        private readonly object stateLock = new();
        private readonly Dictionary<Guid, LiveCaptionSegment> latestById = [];
        private readonly LinkedList<Guid> identityOrder = [];

        public void ObserveDraft(
            LiveCaptionSegment draft,
            DateTimeOffset observedAt)
        {
            ResolveDraft(draft, observedAt);
        }

        public LiveCaptionSegment? ResolveDraft(
            LiveCaptionSegment draft,
            DateTimeOffset observedAt)
        {
            _ = observedAt;
            lock (stateLock)
            {
                if (latestById.TryGetValue(draft.Id, out LiveCaptionSegment? latest))
                {
                    if (draft.Revision < latest.Revision)
                        return null;
                    if (draft.Revision == latest.Revision)
                    {
                        return !latest.IsFinal && string.Equals(
                            latest.Text,
                            draft.Text,
                            StringComparison.Ordinal)
                            ? latest
                            : null;
                    }

                    latestById[draft.Id] = draft;
                    return draft;
                }

                RememberNewIdentity(draft);
                return draft;
            }
        }

        public FinalCaptionAdmissionDecision Evaluate(
            LiveCaptionSegment candidate,
            DateTimeOffset observedAt)
        {
            _ = observedAt;
            lock (stateLock)
            {
                if (latestById.TryGetValue(
                        candidate.Id,
                        out LiveCaptionSegment? latest))
                {
                    if (candidate.Revision <= latest.Revision)
                    {
                        return new FinalCaptionAdmissionDecision(
                            false,
                            latest);
                    }

                    latestById[candidate.Id] = candidate;
                    return new FinalCaptionAdmissionDecision(true, candidate);
                }

                RememberNewIdentity(candidate);
                return new FinalCaptionAdmissionDecision(true, candidate);
            }
        }

        public void Reset()
        {
            lock (stateLock)
            {
                latestById.Clear();
                identityOrder.Clear();
            }
        }

        private void RememberNewIdentity(LiveCaptionSegment segment)
        {
            latestById[segment.Id] = segment;
            identityOrder.AddLast(segment.Id);
            while (identityOrder.Count >
                   LiveCaptionSegmentationThresholds.RecentLedgerCapacity)
            {
                Guid oldest = identityOrder.First!.Value;
                identityOrder.RemoveFirst();
                latestById.Remove(oldest);
            }
        }
    }
}
