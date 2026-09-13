using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.services
{
    internal readonly record struct FinalCaptionAdmissionDecision(
        bool IsAdmitted,
        LiveCaptionSegment CanonicalSegment);

    /// <summary>
    /// Last safety boundary before a finalized recognizer candidate becomes a
    /// translation request, a visible history row, and a persisted record.
    /// Windows Live Captions has no native sentence identity and can recycle the
    /// same completed accessibility row with a new local identity during rapid
    /// updates. Consecutive exact candidates without draft or intervening-final
    /// evidence therefore reuse the last admitted logical sentence.
    /// </summary>
    internal sealed class FinalCaptionAdmissionGate
    {
        private sealed class AcceptedEntry
        {
            public AcceptedEntry(
                LiveCaptionSegment segment,
                DateTimeOffset observedAt,
                long generation)
            {
                Segment = segment;
                LastObservedAt = observedAt;
                Generation = generation;
            }

            public LiveCaptionSegment Segment { get; set; }
            public DateTimeOffset LastObservedAt { get; set; }
            public long Generation { get; }
        }

        private sealed class AliasEntry
        {
            public AliasEntry(Guid canonicalId, DateTimeOffset observedAt)
            {
                CanonicalId = canonicalId;
                LastObservedAt = observedAt;
            }

            public Guid CanonicalId { get; }
            public DateTimeOffset LastObservedAt { get; set; }
        }

        private readonly object stateLock = new();
        private readonly List<AcceptedEntry> accepted = [];
        private readonly Dictionary<Guid, DateTimeOffset> observedDrafts = [];
        private readonly Dictionary<Guid, AliasEntry> canonicalAliases = [];
        private long currentGeneration;

        internal int AliasCount
        {
            get
            {
                lock (stateLock)
                    return canonicalAliases.Count;
            }
        }

        public void ObserveDraft(
            LiveCaptionSegment draft,
            DateTimeOffset observedAt)
        {
            lock (stateLock)
            {
                Prune(observedAt);
                if (canonicalAliases.TryGetValue(
                        draft.Id,
                        out AliasEntry? alias))
                {
                    alias.LastObservedAt = observedAt;
                    return;
                }

                observedDrafts[draft.Id] = observedAt;
                TrimDraftEvidence();
            }
        }

        public FinalCaptionAdmissionDecision Evaluate(
            LiveCaptionSegment candidate,
            DateTimeOffset observedAt)
        {
            lock (stateLock)
            {
                Prune(observedAt);

                if (canonicalAliases.TryGetValue(
                        candidate.Id,
                        out AliasEntry? alias))
                {
                    AcceptedEntry? canonical = accepted.FirstOrDefault(
                        entry => entry.Segment.Id == alias.CanonicalId);
                    if (canonical != null)
                    {
                        observedDrafts.Remove(candidate.Id);
                        alias.LastObservedAt = observedAt;
                        canonical.LastObservedAt = observedAt;
                        if (string.Equals(
                                Normalize(canonical.Segment.Text),
                                Normalize(candidate.Text),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return new FinalCaptionAdmissionDecision(
                                false,
                                canonical.Segment);
                        }

                        LiveCaptionSegment canonicalRevision = candidate with
                        {
                            Id = canonical.Segment.Id,
                            Sequence = canonical.Segment.Sequence,
                            Revision = canonical.Segment.Revision + 1,
                            CapturedAt = canonical.Segment.CapturedAt
                        };
                        canonical.Segment = canonicalRevision;
                        return new FinalCaptionAdmissionDecision(
                            true,
                            canonicalRevision);
                    }

                    canonicalAliases.Remove(candidate.Id);
                }

                AcceptedEntry? sameIdentity = accepted.FirstOrDefault(
                    entry => entry.Segment.Id == candidate.Id);
                if (sameIdentity != null)
                {
                    observedDrafts.Remove(candidate.Id);
                    if (candidate.Revision <= sameIdentity.Segment.Revision)
                    {
                        sameIdentity.LastObservedAt = observedAt;
                        return new FinalCaptionAdmissionDecision(
                            false,
                            sameIdentity.Segment);
                    }

                    sameIdentity.Segment = candidate;
                    sameIdentity.LastObservedAt = observedAt;
                    return new FinalCaptionAdmissionDecision(true, candidate);
                }

                bool hasDraftEvidence = candidate.Revision > 0 ||
                    observedDrafts.Remove(candidate.Id);
                string normalized = Normalize(candidate.Text);
                AcceptedEntry? recentExact = accepted
                    .LastOrDefault(entry =>
                        string.Equals(
                            Normalize(entry.Segment.Text),
                            normalized,
                            StringComparison.OrdinalIgnoreCase) &&
                        IsInsideDuplicateBurst(entry, observedAt));

                if (!hasDraftEvidence &&
                    recentExact != null &&
                    recentExact.Generation == currentGeneration)
                {
                    // Refresh only observed activity. A continuously recycled
                    // accessibility row must not become a new utterance merely
                    // because the first observation is now several seconds old.
                    recentExact.LastObservedAt = observedAt;
                    RegisterAlias(
                        candidate.Id,
                        recentExact.Segment.Id,
                        observedAt);
                    return new FinalCaptionAdmissionDecision(
                        false,
                        recentExact.Segment);
                }

                var admitted = new AcceptedEntry(
                    candidate,
                    observedAt,
                    ++currentGeneration);
                canonicalAliases.Remove(candidate.Id);
                accepted.Add(admitted);
                while (accepted.Count >
                       LiveCaptionSegmentationThresholds.RecentLedgerCapacity)
                {
                    accepted.RemoveAt(0);
                }
                RemoveOrphanedAliases();
                return new FinalCaptionAdmissionDecision(true, candidate);
            }
        }

        public void Reset()
        {
            lock (stateLock)
            {
                accepted.Clear();
                observedDrafts.Clear();
                canonicalAliases.Clear();
                currentGeneration = 0;
            }
        }

        private static string Normalize(string text)
        {
            return TextUtil.NormalizeCaptionWhitespace(text);
        }

        private static bool IsInsideDuplicateBurst(
            AcceptedEntry entry,
            DateTimeOffset observedAt)
        {
            return observedAt >= entry.LastObservedAt &&
                   observedAt - entry.LastObservedAt <=
                       LiveCaptionSegmentationThresholds
                           .AccessibilityDuplicateBurstWindow;
        }

        private void Prune(DateTimeOffset observedAt)
        {
            accepted.RemoveAll(entry =>
                observedAt >= entry.LastObservedAt &&
                observedAt - entry.LastObservedAt >
                    LiveCaptionSegmentationThresholds.RecentLedgerRetention);

            Guid[] expiredDrafts = observedDrafts
                .Where(pair =>
                    observedAt >= pair.Value &&
                    observedAt - pair.Value >
                        LiveCaptionSegmentationThresholds.RecentLedgerRetention)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (Guid segmentId in expiredDrafts)
                observedDrafts.Remove(segmentId);

            Guid[] expiredAliases = canonicalAliases
                .Where(pair =>
                    observedAt >= pair.Value.LastObservedAt &&
                    observedAt - pair.Value.LastObservedAt >
                        LiveCaptionSegmentationThresholds.RecentLedgerRetention)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (Guid alias in expiredAliases)
                canonicalAliases.Remove(alias);
            RemoveOrphanedAliases();
        }

        private void TrimDraftEvidence()
        {
            int capacity = LiveCaptionSegmentationThresholds.RecentLedgerCapacity;
            if (observedDrafts.Count <= capacity)
                return;

            foreach (Guid segmentId in observedDrafts
                         .OrderBy(pair => pair.Value)
                         .Take(observedDrafts.Count - capacity)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                observedDrafts.Remove(segmentId);
            }
        }

        private void RemoveOrphanedAliases()
        {
            HashSet<Guid> canonicalIds = accepted
                .Select(entry => entry.Segment.Id)
                .ToHashSet();
            foreach (Guid alias in canonicalAliases
                         .Where(pair => !canonicalIds.Contains(pair.Value.CanonicalId))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                canonicalAliases.Remove(alias);
            }

            int capacity = LiveCaptionSegmentationThresholds
                .FinalAdmissionAliasCapacity;
            foreach (Guid alias in canonicalAliases
                         .OrderBy(pair => pair.Value.LastObservedAt)
                         .Take(Math.Max(0, canonicalAliases.Count - capacity))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                canonicalAliases.Remove(alias);
            }
        }

        private void RegisterAlias(
            Guid aliasId,
            Guid canonicalId,
            DateTimeOffset observedAt)
        {
            canonicalAliases[aliasId] = new AliasEntry(canonicalId, observedAt);
            RemoveOrphanedAliases();
        }
    }
}
