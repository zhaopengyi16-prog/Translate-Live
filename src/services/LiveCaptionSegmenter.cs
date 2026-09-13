using System.Text;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.services
{
    internal sealed record LiveCaptionSegment(
        Guid Id,
        long Sequence,
        int Revision,
        string Text,
        bool IsFinal,
        DateTimeOffset CapturedAt);

    internal sealed record PendingCaptionCandidate(
        Guid CandidateId,
        Guid HistoricalSegmentId,
        string Text,
        DateTimeOffset FirstObservedAt,
        DateTimeOffset LastObservedAt);

    internal sealed record LiveCaptionUpdate(
        string NormalizedText,
        IReadOnlyList<LiveCaptionSegment> FinalizedSegments,
        LiveCaptionSegment? DraftSegment,
        LiveCaptionSegment? CurrentSegment,
        string CurrentText,
        bool DraftIsEligible,
        TimeSpan DraftStableFor,
        IReadOnlyList<PendingCaptionCandidate> PendingCandidates,
        IReadOnlyList<Guid> WindowSegmentIds)
    {
        public IReadOnlyList<string> FinalizedSentences =>
            FinalizedSegments.Select(segment => segment.Text).ToArray();
        public string DraftText => DraftSegment?.Text ?? string.Empty;
    }

    internal static class LiveCaptionSegmentationThresholds
    {
        // Quiet drafts can be translated for responsiveness, but time alone
        // never promotes them to persisted final sentences.
        public static readonly TimeSpan DraftQuietTranslationDelay =
            TimeSpan.FromMilliseconds(450);

        // Very long unpunctuated speech is committed as deterministic reading
        // units. Prefer a natural boundary near 120 characters and never let a
        // single draft grow beyond roughly 180 characters before splitting.
        public const int LongDraftMinimumChars = 72;
        public const int LongDraftPreferredChars = 120;
        public const int LongDraftHardLimitChars = 180;

        // The recognizer exposes a rolling text window without sentence IDs.
        // Keep only a small recent identity ledger; time removes evidence but is
        // never itself evidence that an identical snapshot is a new utterance.
        public const int RecentLedgerCapacity = 64;
        public const int RevisionTextCapacity = 8;
        public static readonly TimeSpan RecentLedgerRetention =
            TimeSpan.FromMinutes(2);
        public const int RecentSnapshotCapacity = 16;
        public const int PendingCandidateCapacity = 8;

        // Live Captions can temporarily punctuate a short phrase and then
        // continue or shorten it. Position, lexical boundaries, and this small
        // evidence window allow correction without delaying translation.
        public static readonly TimeSpan ShortTailRevisionWindow =
            TimeSpan.FromSeconds(4);
        public const int ShortTailMinimumLatinWords = 3;
        public const int ShortTailMinimumCjkCharacters = 4;
        public const double ShortTailRollbackMinimumLengthRatio = 0.60;

    }

    /// <summary>
    /// Converts the rolling text exposed by Windows Live Captions into stable
    /// sentence updates. Identity follows recognizer position rather than text,
    /// so repeated utterances remain distinct while revisions update in place.
    /// </summary>
    internal sealed class LiveCaptionSegmenter
    {
        private sealed class RecentSegmentEntry
        {
            private readonly Queue<string> previousTexts = new();

            public RecentSegmentEntry(
                LiveCaptionSegment segment,
                DateTimeOffset observedAt)
            {
                Segment = segment;
                FirstObservedAt = observedAt;
                LastForwardObservedAt = observedAt;
                LastSeenAt = observedAt;
            }

            public LiveCaptionSegment Segment { get; private set; }
            public DateTimeOffset FirstObservedAt { get; }
            public DateTimeOffset LastForwardObservedAt { get; private set; }
            public DateTimeOffset LastSeenAt { get; private set; }

            public IEnumerable<string> EarlierTexts => previousTexts;

            public bool IsCurrentText(string text)
            {
                return string.Equals(
                    NormalizeIdentityText(Segment.Text),
                    NormalizeIdentityText(text),
                    StringComparison.OrdinalIgnoreCase);
            }

            public bool IsHistoricalRevision(string text)
            {
                string normalized = NormalizeIdentityText(text);
                return previousTexts.Any(previous => string.Equals(
                    previous,
                    normalized,
                    StringComparison.OrdinalIgnoreCase));
            }

            public void MarkSeen(DateTimeOffset observedAt)
            {
                LastSeenAt = observedAt;
            }

            public LiveCaptionSegment ApplyRevision(
                string text,
                DateTimeOffset observedAt)
            {
                return AcceptRevision(Segment with
                {
                    Revision = Segment.Revision + 1,
                    Text = text,
                    IsFinal = true
                }, observedAt);
            }

            public LiveCaptionSegment AcceptRevision(
                LiveCaptionSegment revised,
                DateTimeOffset observedAt)
            {
                RememberHistoricalText(NormalizeIdentityText(Segment.Text));
                Segment = revised;
                LastForwardObservedAt = observedAt;
                LastSeenAt = observedAt;
                return Segment;
            }

            public void RememberRevisionText(
                string text,
                DateTimeOffset observedAt)
            {
                string normalized = NormalizeIdentityText(text);
                bool isKnown = normalized.Length == 0 ||
                    string.Equals(
                        NormalizeIdentityText(Segment.Text),
                        normalized,
                        StringComparison.OrdinalIgnoreCase) ||
                    previousTexts.Any(previous => string.Equals(
                        previous,
                        normalized,
                        StringComparison.OrdinalIgnoreCase));
                if (!isKnown)
                    RememberHistoricalText(normalized);

                LastForwardObservedAt = observedAt;
                LastSeenAt = observedAt;
            }

            private void RememberHistoricalText(string normalized)
            {
                if (normalized.Length == 0 ||
                    previousTexts.Any(previous => string.Equals(
                        previous,
                        normalized,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                if (previousTexts.Count ==
                    LiveCaptionSegmentationThresholds.RevisionTextCapacity)
                {
                    previousTexts.Dequeue();
                }
                previousTexts.Enqueue(normalized);
            }
        }

        private enum RecentMatchKind
        {
            Current,
            HistoricalRevision,
            TruncatedWindowHead,
            Revision
        }

        private sealed record RecentWindowMatch(
            RecentSegmentEntry Entry,
            RecentMatchKind Kind,
            string ObservedText);

        private sealed record RecentSnapshotEntry(
            DateTimeOffset ObservedAt,
            IReadOnlyList<Guid> CompletedSegmentIds,
            Guid? DraftSegmentId);

        private sealed class PendingCandidateEntry
        {
            public PendingCandidateEntry(
                Guid candidateId,
                Guid historicalSegmentId,
                string text,
                DateTimeOffset observedAt)
            {
                CandidateId = candidateId;
                HistoricalSegmentId = historicalSegmentId;
                Text = text;
                FirstObservedAt = observedAt;
                LastObservedAt = observedAt;
            }

            public Guid CandidateId { get; }
            public Guid HistoricalSegmentId { get; }
            public string Text { get; private set; }
            public DateTimeOffset FirstObservedAt { get; }
            public DateTimeOffset LastObservedAt { get; private set; }

            public void Observe(string text, DateTimeOffset observedAt)
            {
                Text = text;
                LastObservedAt = observedAt;
            }

            public PendingCaptionCandidate Snapshot()
            {
                return new PendingCaptionCandidate(
                    CandidateId,
                    HistoricalSegmentId,
                    Text,
                    FirstObservedAt,
                    LastObservedAt);
            }
        }

        private static readonly char[] ClosingPunctuation =
            ['"', '\'', '”', '’', '»', '）', ')', '】', ']', '》', '〉', '}', '｝'];

        private readonly object stateLock = new();
        private readonly List<RecentSegmentEntry> recentSegments = [];
        private readonly List<RecentSnapshotEntry> recentSnapshots = [];
        private readonly List<PendingCandidateEntry> pendingCandidates = [];
        private List<LiveCaptionSegment> previousCompleted = [];
        private LiveCaptionSegment? activeDraft;
        private LiveCaptionSegment? lastFinal;
        private string previousSnapshot = string.Empty;
        private bool suppressNextSnapshot;
        private bool activeDraftEligible;
        private DateTimeOffset activeDraftChangedAt;
        private long nextSequence;
        private readonly bool splitLongDrafts;

        public LiveCaptionSegmenter(bool splitLongDrafts = true)
        {
            this.splitLongDrafts = splitLongDrafts;
        }

        internal int RecentLedgerCount
        {
            get
            {
                lock (stateLock)
                    return recentSegments.Count;
            }
        }

        public LiveCaptionUpdate Process(string? rawText)
        {
            return Process(rawText, DateTimeOffset.UtcNow);
        }

        internal LiveCaptionUpdate Process(string? rawText, DateTimeOffset observedAt)
        {
            lock (stateLock)
            {
                PruneRecentSegments(observedAt);
                string normalized = TextUtil.NormalizeCaptionWhitespace(rawText);
                if (normalized.Length == 0)
                    return BuildUpdate(normalized, [], observedAt);

                // UI Automation can clip the beginning of its first visible
                // sentence. Repair that physical window edge before splitting;
                // otherwise the same speech can acquire different reading-unit
                // boundaries and new identities on every shifted snapshot.
                string reconciledWindow = RestoreClippedWindowHead(normalized);
                var (completed, observedDraft) = SplitSentences(reconciledWindow);
                if (suppressNextSnapshot)
                {
                    suppressNextSnapshot = false;
                    SeedWindow(completed, observedDraft, observedAt);
                    previousSnapshot = normalized;
                    return BuildUpdate(normalized, [], observedAt);
                }

                if (string.Equals(normalized, previousSnapshot, StringComparison.Ordinal))
                    return BuildUpdate(normalized, [], observedAt);

                if (IsHistoricalTailDraftRollback(
                        completed,
                        observedDraft,
                        observedAt))
                {
                    previousSnapshot = normalized;
                    return BuildUpdate(normalized, [], observedAt);
                }

                var currentCompleted = new List<LiveCaptionSegment>(completed.Count);
                var finalized = new List<LiveCaptionSegment>();
                int draftCompletionIndex = FindDraftCompletionIndex(completed);
                bool positionOnlyCompletion = draftCompletionIndex < 0 &&
                    IsPositionSupportedDraftCompletion(completed, observedDraft);
                if (positionOnlyCompletion)
                    draftCompletionIndex = completed.Count - 1;
                int alignmentLimit = draftCompletionIndex < 0
                    ? completed.Count
                    : draftCompletionIndex;
                IReadOnlyList<RecentWindowMatch?> recentMatches =
                    FindRecentWindowMatches(completed, alignmentLimit);
                var resolvedCompleted = new LiveCaptionSegment?[completed.Count];

                if (IsAmbiguousHistoricalOnlyWindow(
                        completed,
                        observedDraft,
                        recentMatches))
                {
                    RememberPendingCandidate(
                        completed[0],
                        recentMatches[0]!.Entry.Segment.Id,
                        observedAt);
                }

                for (int index = 0; index < alignmentLimit; index++)
                {
                    RecentWindowMatch? match = recentMatches[index];
                    if (match == null)
                        continue;

                    LiveCaptionSegment segment;
                    if (match.Kind == RecentMatchKind.Revision)
                    {
                        segment = match.Entry.ApplyRevision(
                            match.ObservedText,
                            observedAt);
                        finalized.Add(segment);
                        if (lastFinal?.Id == segment.Id)
                            lastFinal = segment;
                    }
                    else
                    {
                        match.Entry.MarkSeen(observedAt);
                        segment = match.Entry.Segment;
                    }
                    resolvedCompleted[index] = segment;
                }

                if (!recentMatches.Any(match => match != null))
                {
                    int overlap = draftCompletionIndex == 0
                        ? 0
                        : FindCompletedWindowOverlap(
                            previousCompleted,
                            completed.Take(alignmentLimit).ToArray());
                    int previousOverlapStart = previousCompleted.Count - overlap;

                    for (int index = 0; index < overlap; index++)
                    {
                        LiveCaptionSegment previous =
                            previousCompleted[previousOverlapStart + index];
                        string currentText = completed[index];
                        if (string.Equals(previous.Text, currentText, StringComparison.Ordinal))
                        {
                            resolvedCompleted[index] = previous;
                            continue;
                        }

                        if (IsLikelyTruncatedLeadingFinal(previous.Text, currentText))
                        {
                            // The rolling accessibility window dropped the start
                            // of a completed sentence. Keep the logical sentence's
                            // full text; this is not a recognizer correction.
                            RecentSegmentEntry? entry = recentSegments.FirstOrDefault(
                                candidate => candidate.Segment.Id == previous.Id);
                            entry?.MarkSeen(observedAt);
                            resolvedCompleted[index] = previous;
                            continue;
                        }

                        LiveCaptionSegment corrected = ApplyRecentRevision(
                            previous,
                            currentText,
                            observedAt);
                        resolvedCompleted[index] = corrected;

                        if (lastFinal?.Id == corrected.Id &&
                            CaptionRevisionPolicy.IsRevision(previous.Text, currentText))
                        {
                            finalized.Add(corrected);
                            lastFinal = corrected;
                        }
                    }

                    if (overlap == 0 &&
                        draftCompletionIndex != 0 &&
                        previousCompleted.Count == 0 &&
                        lastFinal != null &&
                        completed.Count > 0 &&
                        AreSameWindowSentence(lastFinal.Text, completed[0]))
                    {
                        resolvedCompleted[0] = lastFinal;
                    }
                    else if (overlap == 0 &&
                             draftCompletionIndex != 0 &&
                             previousCompleted.Count > 0 &&
                             completed.Count > 1 &&
                             IsLikelyTruncatedLeadingFinal(
                                 previousCompleted[^1].Text,
                                 completed[0]))
                    {
                        // A clipped head fragment is anchored to the window edge
                        // and followed by genuinely new content.
                        resolvedCompleted[0] = previousCompleted[^1];
                    }
                }

                for (int index = 0; index < completed.Count; index++)
                {
                    LiveCaptionSegment? resolved = resolvedCompleted[index];
                    if (resolved != null)
                    {
                        currentCompleted.Add(resolved);
                        continue;
                    }

                    string candidate = completed[index];
                    LiveCaptionSegment segment;
                    if (activeDraft != null &&
                        (TryReconcileDraftCompletion(
                            activeDraft.Text,
                            candidate,
                            out string reconciled) ||
                         positionOnlyCompletion && index == draftCompletionIndex))
                    {
                        segment = activeDraft with
                        {
                            Revision = activeDraft.Revision + 1,
                            Text = reconciled,
                            IsFinal = true
                        };
                        activeDraft = null;
                        activeDraftEligible = false;
                    }
                    else if (index < alignmentLimit &&
                             HasResolvedWindowContentAfter(
                                 resolvedCompleted,
                                 index,
                                 alignmentLimit))
                    {
                        // A new recognizer row is appended at the logical right
                        // edge. An unmatched row in front of already-mapped old
                        // rows is therefore a correction/reorder ambiguity, not
                        // proof of new speech. Keep the known suffix identities
                        // and wait for a later frame with stronger evidence.
                        continue;
                    }
                    else if (TryHoldAmbiguousHistoricalCandidate(
                                 candidate,
                                 currentCompleted,
                                 observedAt))
                    {
                        continue;
                    }
                    else
                    {
                        segment = CreateSegment(candidate, isFinal: true, observedAt);
                        ResolvePendingAsWindowReplay(currentCompleted);
                    }

                    currentCompleted.Add(segment);
                    finalized.Add(segment);
                    lastFinal = segment;
                    RegisterRecentSegment(segment, observedAt);
                }

                bool frontierIsVisible = lastFinal != null &&
                    currentCompleted.Any(segment => segment.Id == lastFinal.Id);
                UpdateDraft(
                    observedDraft,
                    observedAt,
                    canContinueFinal:
                        finalized.Count == 0 &&
                        lastFinal != null &&
                        !frontierIsVisible,
                    hasCompletedSentences:
                        finalized.Count > 0 ||
                        (activeDraft == null && currentCompleted.Count > 0),
                    canReviseDraftAtSamePosition:
                        finalized.Count == 0 &&
                        completed.Count == previousCompleted.Count &&
                        currentCompleted.Select(segment => segment.Id).SequenceEqual(
                            previousCompleted.Select(segment => segment.Id)));
                previousCompleted = currentCompleted;
                previousSnapshot = normalized;
                return BuildUpdate(normalized, finalized, observedAt);
            }
        }

        public void Reset()
        {
            lock (stateLock)
                ClearState(suppressFirstSnapshot: true);
        }

        /// <summary>
        /// Starts from text already visible in an existing Live Captions window.
        /// Existing text gets identity for reconciliation but is not emitted.
        /// </summary>
        public void StartFromCurrentSnapshot(string? rawText)
        {
            StartFromCurrentSnapshot(rawText, DateTimeOffset.UtcNow);
        }

        internal void StartFromCurrentSnapshot(
            string? rawText,
            DateTimeOffset observedAt)
        {
            lock (stateLock)
            {
                ClearState(suppressFirstSnapshot: false);
                string normalized = TextUtil.NormalizeCaptionWhitespace(rawText);
                if (normalized.Length == 0)
                    return;

                var (completed, draft) = SplitSentences(normalized);
                SeedWindow(completed, draft, observedAt);
                previousSnapshot = normalized;
            }
        }

        private int FindDraftCompletionIndex(IReadOnlyList<string> completed)
        {
            if (activeDraft == null)
                return -1;

            // A rolling window normally keeps older completed sentences before
            // the active draft, so prefer the right-most compatible final.
            for (int index = completed.Count - 1; index >= 0; index--)
            {
                if (TryReconcileDraftCompletion(
                        activeDraft.Text,
                        completed[index],
                        out _))
                {
                    return index;
                }
            }
            return -1;
        }

        private bool IsPositionSupportedDraftCompletion(
            IReadOnlyList<string> completed,
            string observedDraft)
        {
            if (activeDraft == null || observedDraft.Length != 0 ||
                completed.Count != previousCompleted.Count + 1 ||
                IsKnownHistoricalText(completed[^1]))
            {
                return false;
            }

            // The completed prefix must retain the same occurrences before the
            // old draft's right-edge slot. A shifted/reordered prefix or an
            // additional completed position is not a radical draft correction.
            for (int index = 0; index < previousCompleted.Count; index++)
            {
                LiveCaptionSegment previous = previousCompleted[index];
                RecentSegmentEntry? entry = recentSegments.FirstOrDefault(
                    candidate => candidate.Segment.Id == previous.Id);
                bool matches = entry != null
                    ? entry.IsCurrentText(completed[index]) ||
                      entry.IsHistoricalRevision(completed[index])
                    : string.Equals(NormalizeIdentityText(previous.Text),
                        NormalizeIdentityText(completed[index]),
                        StringComparison.OrdinalIgnoreCase);
                if (!matches)
                    return false;
            }
            return true;
        }

        internal int RecentSnapshotCount
        {
            get
            {
                lock (stateLock)
                    return recentSnapshots.Count;
            }
        }

        internal int PendingCandidateCount
        {
            get
            {
                lock (stateLock)
                    return pendingCandidates.Count;
            }
        }

        private static bool HasExplicitSentenceEnding(string text)
        {
            string normalized = TextUtil.NormalizeCaptionWhitespace(text)
                .TrimEnd(ClosingPunctuation)
                .TrimEnd();
            return normalized.Length > 0 &&
                   Array.IndexOf(TextUtil.PUNC_EOS, normalized[^1]) >= 0;
        }

        private static bool TryReconcileDraftCompletion(
            string draft,
            string completed,
            out string reconciled)
        {
            if (CaptionRevisionPolicy.TryReconcile(draft, completed, out reconciled))
                return true;

            string left = NormalizeIdentityText(draft);
            string right = NormalizeIdentityText(completed);
            int minimumLength = left.Any(TextUtil.isCJChar) ? 1 : 3;
            if (left.Length >= minimumLength &&
                right.Length >= left.Length &&
                right.StartsWith(left, StringComparison.OrdinalIgnoreCase))
            {
                reconciled = completed;
                return true;
            }

            reconciled = completed;
            return false;
        }

        private IReadOnlyList<RecentWindowMatch?> FindRecentWindowMatches(
            IReadOnlyList<string> completed,
            int matchLimit)
        {
            var matches = new RecentWindowMatch?[Math.Max(0, matchLimit)];
            if (matchLimit <= 0 || recentSegments.Count == 0)
                return matches;

            var usedIds = new HashSet<Guid>();

            // First retain identities that are still in the same accessibility
            // window slot. This is the strongest evidence and preserves two
            // genuine equal occurrences as two separate logical sentences.
            for (int currentIndex = 0; currentIndex < matchLimit; currentIndex++)
            {
                string observedText = completed[currentIndex];
                if (currentIndex >= previousCompleted.Count)
                    continue;

                Guid previousId = previousCompleted[currentIndex].Id;
                RecentSegmentEntry? entry = recentSegments.FirstOrDefault(
                    candidate => candidate.Segment.Id == previousId);
                if (entry == null)
                    continue;

                RecentMatchKind? kind = GetKnownTextMatchKind(
                    entry,
                    observedText,
                    allowTruncatedHead: currentIndex == 0 && completed.Count > 1);
                if (!kind.HasValue)
                    continue;

                matches[currentIndex] = new RecentWindowMatch(
                    entry,
                    kind.Value,
                    observedText);
                usedIds.Add(entry.Segment.Id);
            }

            // Align every remaining known occurrence across the whole window.
            // Do not stop at the first changed row: Windows Live Captions keeps
            // older rows visible after that row, and those suffix identities
            // must survive a correction, rotation, or head truncation.
            for (int currentIndex = 0; currentIndex < matchLimit; currentIndex++)
            {
                if (matches[currentIndex] != null)
                    continue;

                string observedText = completed[currentIndex];
                RecentSegmentEntry? entry = recentSegments
                    .Where(candidate => !usedIds.Contains(candidate.Segment.Id))
                    .Where(candidate =>
                        candidate.IsCurrentText(observedText) ||
                        candidate.IsHistoricalRevision(observedText))
                    .OrderByDescending(candidate =>
                        IsSamePreviousWindowSlot(candidate, currentIndex))
                    .ThenByDescending(candidate =>
                        GetRecentSnapshotPositionScore(candidate, currentIndex))
                    .ThenByDescending(candidate => candidate.IsCurrentText(observedText))
                    .ThenByDescending(candidate => candidate.LastSeenAt)
                    .ThenByDescending(candidate => candidate.Segment.Sequence)
                    .FirstOrDefault();
                if (entry == null)
                    continue;

                usedIds.Add(entry.Segment.Id);
                matches[currentIndex] = new RecentWindowMatch(
                    entry,
                    entry.IsCurrentText(observedText)
                        ? RecentMatchKind.Current
                        : RecentMatchKind.HistoricalRevision,
                    observedText);
            }

            // If one slot changed while neighboring slots retained their exact
            // identities, it is a recognizer correction even when the wording
            // changed too much for text similarity. This is the common source
            // of 10 spoken sentences expanding into dozens of UI/database rows.
            for (int currentIndex = 0; currentIndex < matchLimit; currentIndex++)
            {
                if (matches[currentIndex] != null ||
                    currentIndex >= previousCompleted.Count)
                {
                    continue;
                }

                Guid previousId = previousCompleted[currentIndex].Id;
                if (usedIds.Contains(previousId))
                    continue;

                string observedText = completed[currentIndex];
                bool textAlreadyRepresented = matches.Any(match =>
                    match != null &&
                    (match.Entry.IsCurrentText(observedText) ||
                     match.Entry.IsHistoricalRevision(observedText)));
                if (textAlreadyRepresented)
                {
                    // UI Automation can expose one physical caption row more
                    // than once. A duplicate of an already aligned occurrence
                    // is not a radical rewrite of the old sentence in this slot.
                    continue;
                }

                int anchorCount = CountStablePreviousSlotAnchors(
                    currentIndex,
                    matches,
                    matchLimit);
                bool retainsWindowShape = matchLimit == previousCompleted.Count;
                if (anchorCount == 0 ||
                    (!retainsWindowShape && anchorCount < 2))
                {
                    continue;
                }

                RecentSegmentEntry? entry = recentSegments.FirstOrDefault(
                    candidate => candidate.Segment.Id == previousId);
                if (entry == null)
                    continue;

                matches[currentIndex] = new RecentWindowMatch(
                    entry,
                    RecentMatchKind.Revision,
                    observedText);
                usedIds.Add(previousId);
            }

            // Finally accept high-confidence textual revisions that moved with
            // a rolling window. They need either logical-neighbor support, the
            // same prior slot, or the current speech frontier; similarity alone
            // is never used as a global de-duplication rule.
            for (int currentIndex = 0; currentIndex < matchLimit; currentIndex++)
            {
                if (matches[currentIndex] != null)
                    continue;

                string observedText = completed[currentIndex];
                RecentSegmentEntry? entry = recentSegments
                    .Where(candidate => !usedIds.Contains(candidate.Segment.Id))
                    .Where(candidate =>
                        CaptionRevisionPolicy.IsRevision(
                            candidate.Segment.Text,
                            observedText) ||
                        IsStrongPositionSupportedRevision(
                            candidate.Segment.Text,
                            observedText))
                    .Where(candidate =>
                        IsSamePreviousWindowSlot(candidate, currentIndex) ||
                        CountLogicalNeighborAnchors(
                            candidate,
                            currentIndex,
                            matches,
                            matchLimit) > 0 ||
                        lastFinal?.Id == candidate.Segment.Id)
                    .OrderByDescending(candidate =>
                        IsSamePreviousWindowSlot(candidate, currentIndex))
                    .ThenByDescending(candidate => CountLogicalNeighborAnchors(
                        candidate,
                        currentIndex,
                        matches,
                        matchLimit))
                    .ThenByDescending(candidate =>
                        lastFinal?.Id == candidate.Segment.Id)
                    .ThenByDescending(candidate =>
                        GetRecentSnapshotPositionScore(candidate, currentIndex))
                    .ThenByDescending(candidate => candidate.LastSeenAt)
                    .ThenByDescending(candidate => candidate.Segment.Sequence)
                    .FirstOrDefault();
                if (entry == null)
                    continue;

                matches[currentIndex] = new RecentWindowMatch(
                    entry,
                    RecentMatchKind.Revision,
                    observedText);
                usedIds.Add(entry.Segment.Id);
            }

            return matches;
        }

        private static RecentMatchKind? GetKnownTextMatchKind(
            RecentSegmentEntry entry,
            string observedText,
            bool allowTruncatedHead)
        {
            if (entry.IsCurrentText(observedText))
                return RecentMatchKind.Current;
            if (entry.IsHistoricalRevision(observedText))
                return RecentMatchKind.HistoricalRevision;
            if (allowTruncatedHead &&
                IsLikelyTruncatedLeadingFinal(entry.Segment.Text, observedText))
            {
                return RecentMatchKind.TruncatedWindowHead;
            }
            return null;
        }

        private int CountStablePreviousSlotAnchors(
            int currentIndex,
            IReadOnlyList<RecentWindowMatch?> matches,
            int matchLimit)
        {
            int count = 0;
            if (currentIndex > 0 &&
                matches[currentIndex - 1]?.Entry.Segment.Id ==
                    previousCompleted[currentIndex - 1].Id)
            {
                count++;
            }
            if (currentIndex + 1 < matchLimit &&
                currentIndex + 1 < previousCompleted.Count &&
                matches[currentIndex + 1]?.Entry.Segment.Id ==
                    previousCompleted[currentIndex + 1].Id)
            {
                count++;
            }
            return count;
        }

        private int CountLogicalNeighborAnchors(
            RecentSegmentEntry entry,
            int currentIndex,
            IReadOnlyList<RecentWindowMatch?> matches,
            int matchLimit)
        {
            int previousIndex = previousCompleted.FindIndex(
                segment => segment.Id == entry.Segment.Id);
            if (previousIndex < 0)
                return 0;

            int count = 0;
            if (previousIndex > 0 && currentIndex > 0 &&
                matches[currentIndex - 1]?.Entry.Segment.Id ==
                    previousCompleted[previousIndex - 1].Id)
            {
                count++;
            }
            if (previousIndex + 1 < previousCompleted.Count &&
                currentIndex + 1 < matchLimit &&
                matches[currentIndex + 1]?.Entry.Segment.Id ==
                    previousCompleted[previousIndex + 1].Id)
            {
                count++;
            }
            return count;
        }

        private static bool HasResolvedWindowContentAfter(
            IReadOnlyList<LiveCaptionSegment?> resolved,
            int currentIndex,
            int matchLimit)
        {
            for (int index = currentIndex + 1; index < matchLimit; index++)
            {
                if (resolved[index] != null)
                    return true;
            }
            return false;
        }

        private bool IsSamePreviousWindowSlot(
            RecentSegmentEntry entry,
            int currentIndex)
        {
            return currentIndex < previousCompleted.Count &&
                   previousCompleted[currentIndex].Id == entry.Segment.Id;
        }

        private int GetRecentSnapshotPositionScore(
            RecentSegmentEntry entry,
            int currentIndex)
        {
            for (int index = recentSnapshots.Count - 1; index >= 0; index--)
            {
                IReadOnlyList<Guid> ids = recentSnapshots[index].CompletedSegmentIds;
                int priorPosition = -1;
                for (int position = 0; position < ids.Count; position++)
                {
                    if (ids[position] == entry.Segment.Id)
                    {
                        priorPosition = position;
                        break;
                    }
                }

                if (priorPosition >= 0)
                    return priorPosition == currentIndex ? 2 : 1;
            }
            return 0;
        }

        private bool IsAmbiguousHistoricalOnlyWindow(
            IReadOnlyList<string> completed,
            string observedDraft,
            IReadOnlyList<RecentWindowMatch?> matches)
        {
            if (completed.Count != 1 ||
                observedDraft.Length != 0 ||
                activeDraft != null ||
                matches.Count != 1 ||
                lastFinal == null ||
                matches[0] == null ||
                matches[0]!.Entry.Segment.Id == lastFinal.Id ||
                previousCompleted.Count != 1 ||
                previousCompleted[0].Id != lastFinal.Id)
            {
                return false;
            }

            return matches[0]!.Kind is RecentMatchKind.Current or
                RecentMatchKind.HistoricalRevision;
        }

        private bool TryHoldAmbiguousHistoricalCandidate(
            string candidate,
            IReadOnlyList<LiveCaptionSegment> currentCompleted,
            DateTimeOffset observedAt)
        {
            // Deterministic reading units from one long unpunctuated snapshot
            // are forward content even when two adjacent units happen to have
            // identical words. Ambiguity applies only to recognizer finals.
            if (!HasExplicitSentenceEnding(candidate))
                return false;

            RecentSegmentEntry? historical = recentSegments
                .Where(entry =>
                    entry.IsCurrentText(candidate) ||
                    entry.IsHistoricalRevision(candidate))
                .OrderByDescending(entry => entry.LastSeenAt)
                .ThenByDescending(entry => entry.Segment.Sequence)
                .FirstOrDefault();
            if (historical == null ||
                HasTrustedRightEdgeAppendEvidence(
                    currentCompleted,
                    historical.Segment.Id))
            {
                return false;
            }

            RememberPendingCandidate(
                candidate,
                historical.Segment.Id,
                observedAt);
            historical.MarkSeen(observedAt);
            return true;
        }

        private bool HasTrustedRightEdgeAppendEvidence(
            IReadOnlyList<LiveCaptionSegment> currentCompleted,
            Guid repeatedHistoricalId)
        {
            RecentSnapshotEntry? previous = recentSnapshots.LastOrDefault();
            if (previous == null ||
                previous.CompletedSegmentIds.Count < 2 ||
                currentCompleted.Count != previous.CompletedSegmentIds.Count ||
                currentCompleted[^1].Id == repeatedHistoricalId)
            {
                return false;
            }

            for (int index = 0;
                 index < previous.CompletedSegmentIds.Count;
                 index++)
            {
                if (previous.CompletedSegmentIds[index] != currentCompleted[index].Id)
                    return false;
            }
            return true;
        }

        private void RememberPendingCandidate(
            string text,
            Guid historicalSegmentId,
            DateTimeOffset observedAt)
        {
            string normalized = NormalizeIdentityText(text);
            PendingCandidateEntry? existing = pendingCandidates.FirstOrDefault(
                candidate =>
                    candidate.HistoricalSegmentId == historicalSegmentId &&
                    string.Equals(
                        NormalizeIdentityText(candidate.Text),
                        normalized,
                        StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Observe(text, observedAt);
                return;
            }

            pendingCandidates.Add(new PendingCandidateEntry(
                Guid.NewGuid(),
                historicalSegmentId,
                text,
                observedAt));
            while (pendingCandidates.Count >
                   LiveCaptionSegmentationThresholds.PendingCandidateCapacity)
            {
                pendingCandidates.RemoveAt(0);
            }
        }

        private void ResolvePendingAsWindowReplay(
            IReadOnlyList<LiveCaptionSegment> currentCompleted)
        {
            if (currentCompleted.Count == 0 || pendingCandidates.Count == 0)
                return;

            HashSet<Guid> visibleIds = currentCompleted
                .Select(segment => segment.Id)
                .ToHashSet();
            pendingCandidates.RemoveAll(candidate =>
                visibleIds.Contains(candidate.HistoricalSegmentId));
        }

        private LiveCaptionSegment? TryTakePendingContinuation(
            string observedDraft)
        {
            PendingCandidateEntry? pending = pendingCandidates
                .LastOrDefault(candidate =>
                    IsStrictLexicalGrowth(candidate.Text, observedDraft));
            if (pending == null)
                return null;

            pendingCandidates.Remove(pending);
            return new LiveCaptionSegment(
                pending.CandidateId,
                ++nextSequence,
                1,
                observedDraft,
                false,
                pending.FirstObservedAt);
        }

        private static bool IsStrictLexicalGrowth(string previous, string current)
        {
            string left = NormalizeLexicalText(previous);
            string right = NormalizeLexicalText(current);
            return right.Length > left.Length &&
                   HasShortTailEvidence(left) &&
                   IsLexicalPrefixAtBoundary(right, left);
        }

        private static bool IsStrongPositionSupportedRevision(
            string previous,
            string current)
        {
            string left = NormalizeIdentityText(previous);
            string right = NormalizeIdentityText(current);
            if (left.Length == 0 || right.Length == 0 ||
                string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string longer = left.Length >= right.Length ? left : right;
            string shorter = left.Length >= right.Length ? right : left;
            int minimumLength = shorter.Any(TextUtil.isCJChar) ? 3 : 8;
            if (shorter.Length < minimumLength ||
                !longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase))
            {
                return IsLongLexicalPrefixRevision(left, right);
            }

            if (longer.Length == shorter.Length)
                return true;
            char boundary = longer[shorter.Length];
            return char.IsWhiteSpace(boundary) ||
                   char.IsPunctuation(boundary) ||
                   TextUtil.isCJChar(boundary);
        }

        private static bool IsLongLexicalPrefixRevision(string left, string right)
        {
            string normalizedLeft = NormalizeLexicalText(left);
            string normalizedRight = NormalizeLexicalText(right);
            if (normalizedLeft.Length == 0 || normalizedRight.Length == 0 ||
                string.Equals(
                    normalizedLeft,
                    normalizedRight,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string longer = normalizedLeft.Length >= normalizedRight.Length
                ? normalizedLeft
                : normalizedRight;
            string shorter = normalizedLeft.Length >= normalizedRight.Length
                ? normalizedRight
                : normalizedLeft;
            bool containsCjk = shorter.Any(TextUtil.isCJChar);
            int minimumLength = containsCjk ? 8 : 24;
            if (shorter.Length < minimumLength ||
                (!containsCjk && shorter.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries).Length < 5) ||
                !longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (longer.Length == shorter.Length)
                return true;
            char boundary = longer[shorter.Length];
            return char.IsWhiteSpace(boundary) || TextUtil.isCJChar(boundary);
        }

        private static string NormalizeLexicalText(string text)
        {
            var builder = new StringBuilder(text.Length);
            bool pendingSeparator = false;
            foreach (char character in text)
            {
                if (char.IsLetterOrDigit(character) || TextUtil.isCJChar(character))
                {
                    if (pendingSeparator && builder.Length > 0 &&
                        !TextUtil.isCJChar(builder[^1]) &&
                        !TextUtil.isCJChar(character))
                    {
                        builder.Append(' ');
                    }
                    builder.Append(character);
                    pendingSeparator = false;
                }
                else
                {
                    pendingSeparator = builder.Length > 0;
                }
            }
            return builder.ToString().Trim();
        }

        private LiveCaptionSegment ApplyRecentRevision(
            LiveCaptionSegment previous,
            string currentText,
            DateTimeOffset observedAt)
        {
            RecentSegmentEntry? entry = recentSegments
                .FirstOrDefault(candidate => candidate.Segment.Id == previous.Id);
            if (entry == null)
            {
                RegisterRecentSegment(previous, previous.CapturedAt);
                entry = recentSegments
                    .First(candidate => candidate.Segment.Id == previous.Id);
            }

            if (entry.IsCurrentText(currentText) ||
                entry.IsHistoricalRevision(currentText))
            {
                entry.MarkSeen(observedAt);
                return entry.Segment;
            }

            return entry.ApplyRevision(currentText, observedAt);
        }

        private void RegisterRecentSegment(
            LiveCaptionSegment segment,
            DateTimeOffset observedAt)
        {
            RecentSegmentEntry? existing = recentSegments
                .FirstOrDefault(candidate => candidate.Segment.Id == segment.Id);
            if (existing != null)
            {
                if (segment.Revision > existing.Segment.Revision)
                    existing.AcceptRevision(segment, observedAt);
                else
                    existing.MarkSeen(observedAt);
                return;
            }

            recentSegments.Add(new RecentSegmentEntry(segment, observedAt));
            while (recentSegments.Count >
                   LiveCaptionSegmentationThresholds.RecentLedgerCapacity)
            {
                recentSegments.RemoveAt(0);
            }
        }

        private void PruneRecentSegments(DateTimeOffset observedAt)
        {
            recentSegments.RemoveAll(entry =>
                observedAt >= entry.LastForwardObservedAt &&
                observedAt - entry.LastForwardObservedAt >
                    LiveCaptionSegmentationThresholds.RecentLedgerRetention);
            recentSnapshots.RemoveAll(snapshot =>
                observedAt >= snapshot.ObservedAt &&
                observedAt - snapshot.ObservedAt >
                    LiveCaptionSegmentationThresholds.RecentLedgerRetention);
            pendingCandidates.RemoveAll(candidate =>
                observedAt >= candidate.LastObservedAt &&
                observedAt - candidate.LastObservedAt >
                    LiveCaptionSegmentationThresholds.RecentLedgerRetention);
        }

        private void RememberSnapshot(DateTimeOffset observedAt)
        {
            Guid[] completedIds = previousCompleted
                .Select(segment => segment.Id)
                .ToArray();
            Guid? draftId = activeDraft?.Id;
            RecentSnapshotEntry? previous = recentSnapshots.LastOrDefault();
            if (previous != null &&
                previous.DraftSegmentId == draftId &&
                previous.CompletedSegmentIds.SequenceEqual(completedIds))
            {
                recentSnapshots[^1] = previous with { ObservedAt = observedAt };
                return;
            }

            recentSnapshots.Add(new RecentSnapshotEntry(
                observedAt,
                completedIds,
                draftId));
            while (recentSnapshots.Count >
                   LiveCaptionSegmentationThresholds.RecentSnapshotCapacity)
            {
                recentSnapshots.RemoveAt(0);
            }
        }

        private static string NormalizeIdentityText(string text)
        {
            return TextUtil.NormalizeCaptionWhitespace(text)
                .TrimEnd(TextUtil.PUNC_EOS)
                .TrimEnd(ClosingPunctuation)
                .Trim();
        }

        private void SeedWindow(
            IReadOnlyList<string> completed,
            string draft,
            DateTimeOffset observedAt)
        {
            previousCompleted = completed
                .Select(text => CreateSegment(text, isFinal: true, observedAt))
                .ToList();
            foreach (LiveCaptionSegment segment in previousCompleted)
                RegisterRecentSegment(segment, observedAt);
            lastFinal = previousCompleted.LastOrDefault();
            activeDraft = draft.Length == 0
                ? null
                : CreateSegment(draft, isFinal: false, observedAt);
            activeDraftChangedAt = observedAt;
            activeDraftEligible = false;
        }

        private void ClearState(bool suppressFirstSnapshot)
        {
            previousCompleted = [];
            recentSegments.Clear();
            recentSnapshots.Clear();
            pendingCandidates.Clear();
            activeDraft = null;
            lastFinal = null;
            previousSnapshot = string.Empty;
            suppressNextSnapshot = suppressFirstSnapshot;
            activeDraftEligible = false;
            activeDraftChangedAt = default;
            nextSequence = 0;
        }

        private void UpdateDraft(
            string observedDraft,
            DateTimeOffset observedAt,
            bool canContinueFinal,
            bool hasCompletedSentences,
            bool canReviseDraftAtSamePosition)
        {
            if (observedDraft.Length == 0)
            {
                if (hasCompletedSentences)
                {
                    activeDraft = null;
                    activeDraftEligible = false;
                }
                return;
            }

            if (activeDraft == null)
            {
                LiveCaptionSegment? pendingContinuation =
                    TryTakePendingContinuation(observedDraft);
                if (pendingContinuation != null)
                {
                    activeDraft = pendingContinuation;
                    activeDraftChangedAt = observedAt;
                    activeDraftEligible = true;
                    return;
                }

                // Temporary terminal punctuation can disappear as the same
                // tail continues. Explicit appended drafts and new short-prefix
                // utterances must still receive their own identity.
                RecentSegmentEntry? continuationEntry = null;
                if (canContinueFinal &&
                    lastFinal != null)
                {
                    continuationEntry = recentSegments.FirstOrDefault(
                        entry => entry.Segment.Id == lastFinal.Id);
                }
                bool continuesFinal = canContinueFinal &&
                    lastFinal != null &&
                    continuationEntry != null &&
                    NormalizeLexicalText(observedDraft).Length >
                        NormalizeLexicalText(lastFinal.Text).Length &&
                    (IsLongLexicalPrefixRevision(lastFinal.Text, observedDraft) ||
                     IsRecentShortTailGrowth(
                         continuationEntry,
                         lastFinal.Text,
                         observedDraft,
                         observedAt));
                if (continuesFinal)
                {
                    LiveCaptionSegment previousFinal = lastFinal!;
                    activeDraft = previousFinal with
                    {
                        Revision = previousFinal.Revision + 1,
                        Text = observedDraft,
                        IsFinal = false
                    };
                    continuationEntry!.RememberRevisionText(
                        observedDraft,
                        observedAt);
                }
                else
                {
                    activeDraft = CreateSegment(observedDraft, isFinal: false, observedAt);
                }
                activeDraftChangedAt = observedAt;
                activeDraftEligible = true;
                return;
            }

            RecentSegmentEntry? continuedEntry = recentSegments.FirstOrDefault(
                entry => entry.Segment.Id == activeDraft.Id);
            if (continuedEntry != null &&
                !string.Equals(
                    NormalizeIdentityText(activeDraft.Text),
                    NormalizeIdentityText(observedDraft),
                    StringComparison.OrdinalIgnoreCase) &&
                (continuedEntry.IsCurrentText(observedDraft) ||
                 continuedEntry.IsHistoricalRevision(observedDraft)))
            {
                // A reopened final can receive an older clipped draft while
                // its newer continuation is still live. Replaying a known
                // revision is not another forward recognizer correction.
                continuedEntry.MarkSeen(observedAt);
                return;
            }

            if (CaptionRevisionPolicy.TryReconcile(
                    activeDraft.Text,
                    observedDraft,
                    out string reconciled))
            {
                if (!string.Equals(activeDraft.Text, reconciled, StringComparison.Ordinal))
                {
                    activeDraft = activeDraft with
                    {
                        Revision = activeDraft.Revision + 1,
                        Text = reconciled
                    };
                    RememberContinuedDraftRevision(activeDraft, observedAt);
                    activeDraftChangedAt = observedAt;
                    activeDraftEligible = true;
                }
                return;
            }

            if (canReviseDraftAtSamePosition)
            {
                // A single live hypothesis may be substantially rewritten before
                // the recognizer establishes any sentence boundary. A failed
                // similarity score is not evidence of a second utterance. Known
                // historical text is a replay ambiguity and cannot overwrite it.
                if (IsKnownHistoricalText(observedDraft))
                    return;

                activeDraft = activeDraft with
                {
                    Revision = activeDraft.Revision + 1,
                    Text = observedDraft
                };
                RememberContinuedDraftRevision(activeDraft, observedAt);
            }
            else
            {
                activeDraft = CreateSegment(observedDraft, isFinal: false, observedAt);
            }
            activeDraftChangedAt = observedAt;
            activeDraftEligible = true;
        }

        private bool IsKnownHistoricalText(string text)
        {
            return recentSegments.Any(entry =>
                entry.IsCurrentText(text) || entry.IsHistoricalRevision(text));
        }

        private bool IsHistoricalTailDraftRollback(
            IReadOnlyList<string> completed,
            string observedDraft,
            DateTimeOffset observedAt)
        {
            if (completed.Count != 0 ||
                observedDraft.Length == 0 ||
                activeDraft != null ||
                lastFinal == null ||
                previousCompleted.LastOrDefault()?.Id != lastFinal.Id)
            {
                return false;
            }

            RecentSegmentEntry? entry = recentSegments.FirstOrDefault(
                candidate => candidate.Segment.Id == lastFinal.Id);
            if (entry == null ||
                (!entry.IsCurrentText(observedDraft) &&
                 !entry.IsHistoricalRevision(observedDraft) &&
                 !IsRecentShortTailRollback(
                     entry,
                     observedDraft,
                     observedAt)))
            {
                return false;
            }

            entry.MarkSeen(observedAt);
            return true;
        }

        private static bool IsRecentShortTailGrowth(
            RecentSegmentEntry entry,
            string previous,
            string current,
            DateTimeOffset observedAt)
        {
            if (!IsInsideShortTailRevisionWindow(entry, observedAt))
                return false;

            string left = NormalizeLexicalText(previous);
            string right = NormalizeLexicalText(current);
            return right.Length > left.Length &&
                   HasShortTailEvidence(left) &&
                   IsLexicalPrefixAtBoundary(right, left);
        }

        private static bool IsRecentShortTailRollback(
            RecentSegmentEntry entry,
            string observedDraft,
            DateTimeOffset observedAt)
        {
            if (!IsInsideShortTailRevisionWindow(entry, observedAt))
                return false;

            string current = NormalizeLexicalText(entry.Segment.Text);
            string observed = NormalizeLexicalText(observedDraft);
            if (observed.Length == 0 ||
                observed.Length >= current.Length ||
                !HasShortTailEvidence(observed) ||
                (double)observed.Length / current.Length <
                    LiveCaptionSegmentationThresholds
                        .ShortTailRollbackMinimumLengthRatio)
            {
                return false;
            }

            return IsLexicalPrefixAtBoundary(current, observed);
        }

        private static bool IsInsideShortTailRevisionWindow(
            RecentSegmentEntry entry,
            DateTimeOffset observedAt)
        {
            if (observedAt < entry.LastForwardObservedAt)
                return false;

            return observedAt - entry.LastForwardObservedAt <=
                   LiveCaptionSegmentationThresholds.ShortTailRevisionWindow;
        }

        private static bool HasShortTailEvidence(string text)
        {
            int cjkCount = text.Count(TextUtil.isCJChar);
            if (cjkCount > 0)
            {
                return cjkCount >= LiveCaptionSegmentationThresholds
                    .ShortTailMinimumCjkCharacters;
            }

            return text.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries).Length >=
                LiveCaptionSegmentationThresholds.ShortTailMinimumLatinWords;
        }

        private static bool IsLexicalPrefixAtBoundary(
            string text,
            string prefix)
        {
            if (prefix.Length == 0 ||
                !text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (text.Length == prefix.Length)
                return true;

            return char.IsWhiteSpace(text[prefix.Length]) ||
                   TextUtil.isCJChar(text[prefix.Length]);
        }

        private void RememberContinuedDraftRevision(
            LiveCaptionSegment draft,
            DateTimeOffset observedAt)
        {
            if (lastFinal?.Id != draft.Id)
                return;

            recentSegments.FirstOrDefault(
                entry => entry.Segment.Id == draft.Id)?.RememberRevisionText(
                    draft.Text,
                    observedAt);
        }

        private LiveCaptionUpdate BuildUpdate(
            string normalized,
            IReadOnlyList<LiveCaptionSegment> finalized,
            DateTimeOffset observedAt)
        {
            TimeSpan stableFor = activeDraft == null || activeDraftChangedAt == default
                ? TimeSpan.Zero
                : observedAt - activeDraftChangedAt;
            LiveCaptionSegment? snapshotTail = previousCompleted.LastOrDefault();
            LiveCaptionSegment? forwardTail = lastFinal;
            LiveCaptionSegment? stableTail = snapshotTail == null ||
                                             forwardTail == null ||
                                             snapshotTail.Sequence >= forwardTail.Sequence
                ? snapshotTail
                : forwardTail;
            LiveCaptionSegment? currentSegment = activeDraft ?? stableTail ?? forwardTail;
            string currentText = currentSegment?.Text ?? string.Empty;
            RememberSnapshot(observedAt);
            return new LiveCaptionUpdate(
                normalized,
                finalized,
                activeDraft,
                currentSegment,
                currentText,
                activeDraftEligible,
                stableFor < TimeSpan.Zero ? TimeSpan.Zero : stableFor,
                pendingCandidates.Select(candidate => candidate.Snapshot()).ToArray(),
                previousCompleted.Select(segment => segment.Id).ToArray());
        }

        private LiveCaptionSegment CreateSegment(
            string text,
            bool isFinal,
            DateTimeOffset observedAt)
        {
            return new LiveCaptionSegment(
                Guid.NewGuid(),
                ++nextSequence,
                0,
                text,
                isFinal,
                observedAt);
        }

        private readonly record struct CaptionWord(int Start, int Length);

        private string RestoreClippedWindowHead(string observed)
        {
            if (previousCompleted.Count == 0 && activeDraft == null)
                return observed;

            // A newly observed short draft growing into an old sentence's
            // suffix is evidence of a second utterance, not head clipping.
            // Preserve that already established trajectory before considering
            // historical completed text.
            if (activeDraft != null)
            {
                string draftPrefix = NormalizeLexicalText(activeDraft.Text);
                string observedPrefix = NormalizeLexicalText(observed);
                int minimum = draftPrefix.Any(TextUtil.isCJChar) ? 1 : 3;
                if (draftPrefix.Length >= minimum &&
                    observedPrefix.StartsWith(
                        draftPrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return observed;
                }
            }

            List<CaptionWord> currentWords = ReadCaptionWords(observed);
            if (currentWords.Count == 0)
                return observed;

            string? missingPrefix = null;
            int bestOverlapCharacters = 0;
            foreach (string previous in GetVisibleOccurrenceTexts())
            {
                List<CaptionWord> previousWords = ReadCaptionWords(previous);
                if (previousWords.Count == 0)
                    continue;

                // An intact leading occurrence must never be reconstructed
                // from a matching suffix of some other visible occurrence.
                if (WordsMatchAtHead(previous, previousWords, 0, observed, currentWords))
                    return observed;

                for (int start = 1; start < previousWords.Count; start++)
                {
                    int count = previousWords.Count - start;
                    if (count > currentWords.Count ||
                        !WordsMatchAtHead(previous, previousWords, start, observed, currentWords))
                    {
                        continue;
                    }

                    CaptionWord last = currentWords[count - 1];
                    int overlapCharacters = last.Start + last.Length;
                    int cjkCount = observed.AsSpan(0, overlapCharacters)
                        .ToString().Count(TextUtil.isCJChar);
                    bool sufficientEvidence = cjkCount > 0
                        ? cjkCount >= LiveCaptionSegmentationThresholds.ShortTailMinimumCjkCharacters
                        : count >= LiveCaptionSegmentationThresholds.ShortTailMinimumLatinWords &&
                          overlapCharacters >= 12;
                    if (!sufficientEvidence || overlapCharacters <= bestOverlapCharacters)
                        continue;

                    missingPrefix = previous[..previousWords[start].Start];
                    bestOverlapCharacters = overlapCharacters;
                }
            }

            // Only restore a prefix belonging to one retained occurrence. Do
            // not concatenate old complete windows: that would grow forever
            // and could replay older classroom records as new speech.
            return missingPrefix == null ? observed : missingPrefix + observed;
        }

        private IEnumerable<string> GetVisibleOccurrenceTexts()
        {
            if (activeDraft != null)
                yield return activeDraft.Text;

            foreach (LiveCaptionSegment segment in previousCompleted)
            {
                yield return segment.Text;
                RecentSegmentEntry? entry = recentSegments.FirstOrDefault(
                    candidate => candidate.Segment.Id == segment.Id);
                if (entry == null)
                    continue;

                foreach (string earlier in entry.EarlierTexts)
                    yield return earlier;
            }

            // When a final is reopened as a draft it temporarily leaves the
            // completed window. Its prior clipped revisions still need the
            // same reconstruction anchors for late window replays.
            if (activeDraft != null)
            {
                RecentSegmentEntry? entry = recentSegments.FirstOrDefault(
                    candidate => candidate.Segment.Id == activeDraft.Id);
                if (entry != null)
                {
                    yield return entry.Segment.Text;
                    foreach (string earlier in entry.EarlierTexts)
                        yield return earlier;
                }
            }
        }

        private static bool WordsMatchAtHead(
            string previous,
            IReadOnlyList<CaptionWord> previousWords,
            int start,
            string current,
            IReadOnlyList<CaptionWord> currentWords)
        {
            int count = previousWords.Count - start;
            if (count > currentWords.Count)
                return false;

            for (int index = 0; index < count; index++)
            {
                CaptionWord left = previousWords[start + index];
                CaptionWord right = currentWords[index];
                if (!previous.AsSpan(left.Start, left.Length).Equals(
                        current.AsSpan(right.Start, right.Length),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }

        private static List<CaptionWord> ReadCaptionWords(string text)
        {
            var words = new List<CaptionWord>();
            for (int index = 0; index < text.Length;)
            {
                if (!char.IsLetterOrDigit(text[index]))
                {
                    index++;
                    continue;
                }

                int start = index++;
                if (!TextUtil.isCJChar(text[start]))
                {
                    while (index < text.Length &&
                           char.IsLetterOrDigit(text[index]) &&
                           !TextUtil.isCJChar(text[index]))
                    {
                        index++;
                    }
                }
                words.Add(new CaptionWord(start, index - start));
            }
            return words;
        }

        private (List<string> Completed, string Draft) SplitSentences(string text)
        {
            var completed = new List<string>();
            int sentenceStart = 0;
            int index = 0;

            while (index < text.Length)
            {
                if (!IsSentenceBoundary(text, index))
                {
                    index++;
                    continue;
                }

                int sentenceEnd = index + 1;
                while (sentenceEnd < text.Length &&
                       (Array.IndexOf(TextUtil.PUNC_EOS, text[sentenceEnd]) >= 0 ||
                        Array.IndexOf(ClosingPunctuation, text[sentenceEnd]) >= 0))
                {
                    sentenceEnd++;
                }

                string sentence = text[sentenceStart..sentenceEnd].Trim();
                if (sentence.Length > 0)
                    completed.Add(sentence);

                sentenceStart = sentenceEnd;
                index = sentenceEnd;
            }

            string draft = sentenceStart < text.Length
                ? text[sentenceStart..].Trim()
                : string.Empty;
            while (splitLongDrafts &&
                   TryTakeLongReadingUnit(draft, out string unit, out string remainder))
            {
                completed.Add(unit);
                draft = remainder;
            }
            return (completed, draft);
        }

        private static bool TryTakeLongReadingUnit(
            string draft,
            out string unit,
            out string remainder)
        {
            unit = string.Empty;
            remainder = draft;
            if (draft.Length <= LiveCaptionSegmentationThresholds.LongDraftHardLimitChars)
                return false;

            int boundary = FindNaturalReadingBoundary(draft);
            if (boundary < LiveCaptionSegmentationThresholds.LongDraftMinimumChars)
                return false;

            unit = draft[..boundary].Trim();
            remainder = draft[boundary..].TrimStart();
            return unit.Length > 0 && remainder.Length > 0;
        }

        private static int FindNaturalReadingBoundary(string text)
        {
            int hardLimit = Math.Min(
                LiveCaptionSegmentationThresholds.LongDraftHardLimitChars,
                text.Length - 1);
            int preferred = Math.Min(
                LiveCaptionSegmentationThresholds.LongDraftPreferredChars,
                hardLimit);
            int minimum = LiveCaptionSegmentationThresholds.LongDraftMinimumChars;

            int punctuation = FindBoundaryBackward(
                text,
                preferred,
                minimum,
                static character => character is ',' or ';' or ':' or
                    '，' or '；' or '：');
            if (punctuation < 0)
            {
                punctuation = FindBoundaryBackward(
                    text,
                    hardLimit,
                    preferred + 1,
                    static character => character is ',' or ';' or ':' or
                        '，' or '；' or '：');
            }
            if (punctuation >= 0)
                return punctuation + 1;

            int whitespace = FindBoundaryBackward(
                text,
                preferred,
                minimum,
                char.IsWhiteSpace);
            if (whitespace < 0)
            {
                whitespace = FindBoundaryBackward(
                    text,
                    hardLimit,
                    preferred + 1,
                    char.IsWhiteSpace);
            }
            if (whitespace >= 0)
                return whitespace;

            // CJK usually has no spaces; a deterministic character boundary is
            // safer than an ever-growing draft and does not discard any text.
            return text.Take(hardLimit).Any(TextUtil.isCJChar)
                ? preferred
                : hardLimit;
        }

        private static int FindBoundaryBackward(
            string text,
            int start,
            int minimum,
            Func<char, bool> predicate)
        {
            for (int index = start; index >= minimum; index--)
            {
                if (predicate(text[index]))
                    return index;
            }
            return -1;
        }

        private static bool IsSentenceBoundary(string text, int index)
        {
            char punctuation = text[index];
            if (Array.IndexOf(TextUtil.PUNC_EOS, punctuation) < 0)
                return false;
            if (punctuation != '.')
                return true;

            if (index > 0 && index + 1 < text.Length &&
                char.IsDigit(text[index - 1]) && char.IsDigit(text[index + 1]))
            {
                return false;
            }

            string token = ReadTokenBefore(text, index);
            if (IsKnownAbbreviation(token) || IsInitialism(token) || IsTrailingNumber(token))
                return false;

            if (index + 1 < text.Length && !char.IsWhiteSpace(text[index + 1]))
            {
                return Array.IndexOf(ClosingPunctuation, text[index + 1]) >= 0 ||
                       !char.IsLetterOrDigit(text[index + 1]);
            }

            return true;
        }

        private static string ReadTokenBefore(string text, int periodIndex)
        {
            int start = periodIndex;
            while (start > 0 &&
                   !char.IsWhiteSpace(text[start - 1]) &&
                   !IsTokenBoundary(text[start - 1]))
            {
                start--;
            }

            while (start > 1 && text[start - 1] == '.' && char.IsLetter(text[start - 2]))
            {
                start -= 2;
                while (start > 0 && char.IsLetter(text[start - 1]))
                    start--;
            }

            return text[start..(periodIndex + 1)];
        }

        private static bool IsTokenBoundary(char character)
        {
            return char.IsPunctuation(character) && character != '.';
        }

        private static bool IsTrailingNumber(string token)
        {
            string value = token.TrimEnd('.');
            return value.Length > 0 && value.All(char.IsDigit);
        }

        private static bool IsKnownAbbreviation(string token)
        {
            return token.Equals("Mr.", StringComparison.OrdinalIgnoreCase) ||
                   token.Equals("Mrs.", StringComparison.OrdinalIgnoreCase) ||
                   token.Equals("Ms.", StringComparison.OrdinalIgnoreCase) ||
                   token.Equals("Dr.", StringComparison.OrdinalIgnoreCase) ||
                   token.Equals("Prof.", StringComparison.OrdinalIgnoreCase) ||
                   token.Equals("Sr.", StringComparison.OrdinalIgnoreCase) ||
                   token.Equals("Jr.", StringComparison.OrdinalIgnoreCase) ||
                   token.Equals("vs.", StringComparison.OrdinalIgnoreCase) ||
                   token.Equals("e.g.", StringComparison.OrdinalIgnoreCase) ||
                   token.Equals("i.e.", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInitialism(string token)
        {
            int letterCount = 0;
            bool expectsLetter = true;
            foreach (char character in token)
            {
                if (expectsLetter)
                {
                    if (!char.IsUpper(character))
                        return false;
                    letterCount++;
                }
                else if (character != '.')
                {
                    return false;
                }
                expectsLetter = !expectsLetter;
            }

            return letterCount >= 2 && expectsLetter;
        }

        private static int FindCompletedWindowOverlap(
            IReadOnlyList<LiveCaptionSegment> previous,
            IReadOnlyList<string> current)
        {
            int maximum = Math.Min(previous.Count, current.Count);
            for (int count = maximum; count > 0; count--)
            {
                bool matches = true;
                for (int offset = 0; offset < count; offset++)
                {
                    string left = previous[previous.Count - count + offset].Text;
                    string right = current[offset];
                    if (!AreSameWindowSentence(left, right))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                    return count;
            }

            return 0;
        }

        private static bool AreEquivalentFinals(string left, string right)
        {
            return string.Equals(
                       TextUtil.NormalizeCaptionWhitespace(left),
                       TextUtil.NormalizeCaptionWhitespace(right),
                       StringComparison.OrdinalIgnoreCase) ||
                   CaptionRevisionPolicy.IsRevision(left, right);
        }

        private static bool AreSameWindowSentence(string previous, string current)
        {
            if (AreEquivalentFinals(previous, current))
                return true;

            string left = TextUtil.NormalizeCaptionWhitespace(previous)
                .TrimEnd(TextUtil.PUNC_EOS).TrimEnd(ClosingPunctuation);
            string right = TextUtil.NormalizeCaptionWhitespace(current)
                .TrimEnd(TextUtil.PUNC_EOS).TrimEnd(ClosingPunctuation);
            if (left.Length == 0 || right.Length == 0)
                return false;

            string longer = left.Length >= right.Length ? left : right;
            string shorter = left.Length >= right.Length ? right : left;
            bool containsCjk = shorter.Any(TextUtil.isCJChar);
            int minimumLength = containsCjk ? 4 : 8;
            if (shorter.Length < minimumLength ||
                (double)shorter.Length / longer.Length < 0.25 ||
                !longer.EndsWith(shorter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int suffixStart = longer.Length - shorter.Length;
            return suffixStart == 0 ||
                   char.IsWhiteSpace(longer[suffixStart - 1]) ||
                   char.IsPunctuation(longer[suffixStart - 1]) ||
                   TextUtil.isCJChar(longer[suffixStart - 1]);
        }

        private static bool IsLikelyTruncatedLeadingFinal(
            string previous,
            string currentHead)
        {
            string left = TextUtil.NormalizeCaptionWhitespace(previous)
                .TrimEnd(TextUtil.PUNC_EOS).TrimEnd(ClosingPunctuation).Trim();
            string right = TextUtil.NormalizeCaptionWhitespace(currentHead)
                .TrimEnd(TextUtil.PUNC_EOS).TrimEnd(ClosingPunctuation).Trim();
            if (right.Length < 2 || left.Length < right.Length * 2 ||
                !left.EndsWith(right, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int start = left.Length - right.Length;
            return start == 0 ||
                   char.IsWhiteSpace(left[start - 1]) ||
                   char.IsPunctuation(left[start - 1]) ||
                   TextUtil.isCJChar(left[start - 1]);
        }
    }
}
