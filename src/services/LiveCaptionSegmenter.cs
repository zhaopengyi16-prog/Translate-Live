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

    internal sealed record LiveCaptionUpdate(
        string NormalizedText,
        IReadOnlyList<LiveCaptionSegment> FinalizedSegments,
        LiveCaptionSegment? DraftSegment,
        LiveCaptionSegment? CurrentSegment,
        string CurrentText,
        bool DraftIsEligible,
        TimeSpan DraftStableFor)
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
        public const int RecentLedgerCapacity = 24;
        public const int RevisionTextCapacity = 4;
        public static readonly TimeSpan RecentLedgerRetention =
            TimeSpan.FromMinutes(2);

        // Live Captions can temporarily punctuate a short phrase and then
        // continue or shorten it. Position, lexical boundaries, and this small
        // evidence window allow correction without delaying translation.
        public static readonly TimeSpan ShortTailRevisionWindow =
            TimeSpan.FromSeconds(4);
        public const int ShortTailMinimumLatinWords = 3;
        public const int ShortTailMinimumCjkCharacters = 4;
        public const double ShortTailRollbackMinimumLengthRatio = 0.60;

        // During rapid layout recycling, the Live Captions accessibility window
        // can append the same completed tail row more than once. Suppress only an
        // adjacent exact repeat with no draft evidence inside this short burst.
        public static readonly TimeSpan AccessibilityDuplicateBurstWindow =
            TimeSpan.FromSeconds(3);
        public const int FinalAdmissionAliasCapacity = RecentLedgerCapacity * 4;
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

        private static readonly char[] ClosingPunctuation =
            ['"', '\'', '”', '’', '»', '）', ')', '】', ']', '》', '〉', '}', '｝'];

        private readonly object stateLock = new();
        private readonly List<RecentSegmentEntry> recentSegments = [];
        private List<LiveCaptionSegment> previousCompleted = [];
        private LiveCaptionSegment? activeDraft;
        private LiveCaptionSegment? lastFinal;
        private string previousSnapshot = string.Empty;
        private bool suppressNextSnapshot;
        private bool activeDraftEligible;
        private DateTimeOffset activeDraftChangedAt;
        private long nextSequence;

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

                var (completed, observedDraft) = SplitSentences(normalized);
                if (suppressNextSnapshot)
                {
                    suppressNextSnapshot = false;
                    SeedWindow(completed, observedDraft, observedAt);
                    previousSnapshot = normalized;
                    return BuildUpdate(normalized, [], observedAt);
                }

                if (string.Equals(normalized, previousSnapshot, StringComparison.Ordinal))
                    return BuildUpdate(normalized, [], observedAt);

                Dictionary<Guid, DateTimeOffset> previousLastSeen = recentSegments
                    .ToDictionary(entry => entry.Segment.Id, entry => entry.LastSeenAt);

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
                int alignmentLimit = draftCompletionIndex < 0
                    ? completed.Count
                    : draftCompletionIndex;
                IReadOnlyList<RecentWindowMatch> recentMatches =
                    FindRecentWindowMatches(completed, alignmentLimit);
                int firstNewIndex = recentMatches.Count;

                foreach (RecentWindowMatch match in recentMatches)
                {
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
                    currentCompleted.Add(segment);
                }

                if (recentMatches.Count == 0)
                {
                    int overlap = draftCompletionIndex == 0
                        ? 0
                        : FindCompletedWindowOverlap(previousCompleted, completed);
                    int previousOverlapStart = previousCompleted.Count - overlap;
                    firstNewIndex = overlap;

                    for (int index = 0; index < overlap; index++)
                    {
                        LiveCaptionSegment previous =
                            previousCompleted[previousOverlapStart + index];
                        string currentText = completed[index];
                        if (string.Equals(previous.Text, currentText, StringComparison.Ordinal))
                        {
                            currentCompleted.Add(previous);
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
                            currentCompleted.Add(previous);
                            continue;
                        }

                        LiveCaptionSegment corrected = ApplyRecentRevision(
                            previous,
                            currentText,
                            observedAt);
                        currentCompleted.Add(corrected);

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
                        firstNewIndex = 1;
                        currentCompleted.Add(lastFinal);
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
                        firstNewIndex = 1;
                        currentCompleted.Add(previousCompleted[^1]);
                    }
                }

                for (int index = firstNewIndex; index < completed.Count; index++)
                {
                    string candidate = completed[index];
                    LiveCaptionSegment segment;
                    if (activeDraft != null &&
                        TryReconcileDraftCompletion(
                            activeDraft.Text,
                            candidate,
                            out string reconciled))
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
                    else if (IsRapidAccessibilityTailDuplicate(
                                 candidate,
                                 currentCompleted,
                                 previousLastSeen,
                                 observedAt))
                    {
                        continue;
                    }
                    else
                    {
                        segment = CreateSegment(candidate, isFinal: true, observedAt);
                    }

                    currentCompleted.Add(segment);
                    finalized.Add(segment);
                    lastFinal = segment;
                    RegisterRecentSegment(segment, observedAt);
                }

                UpdateDraft(
                    observedDraft,
                    observedAt,
                    canContinueFinal: completed.Count == 0,
                    hasCompletedSentences:
                        finalized.Count > 0 ||
                        (activeDraft == null && currentCompleted.Count > 0));
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
            lock (stateLock)
            {
                ClearState(suppressFirstSnapshot: false);
                string normalized = TextUtil.NormalizeCaptionWhitespace(rawText);
                if (normalized.Length == 0)
                    return;

                var (completed, draft) = SplitSentences(normalized);
                SeedWindow(completed, draft, DateTimeOffset.UtcNow);
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

        private bool IsRapidAccessibilityTailDuplicate(
            string candidate,
            IReadOnlyList<LiveCaptionSegment> currentCompleted,
            IReadOnlyDictionary<Guid, DateTimeOffset> previousLastSeen,
            DateTimeOffset observedAt)
        {
            if (currentCompleted.Count == 0 ||
                !HasExplicitSentenceEnding(candidate))
                return false;

            LiveCaptionSegment currentTail = currentCompleted[^1];
            if (!string.Equals(
                    NormalizeIdentityText(currentTail.Text),
                    NormalizeIdentityText(candidate),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            bool existedBeforeSnapshot = previousLastSeen.TryGetValue(
                currentTail.Id,
                out DateTimeOffset lastSeen);
            bool createdInCurrentSnapshot =
                !existedBeforeSnapshot && currentTail.CapturedAt == observedAt;
            if (!createdInCurrentSnapshot &&
                (!existedBeforeSnapshot ||
                 observedAt < lastSeen ||
                 observedAt - lastSeen >
                    LiveCaptionSegmentationThresholds.AccessibilityDuplicateBurstWindow))
            {
                return false;
            }

            recentSegments.FirstOrDefault(
                entry => entry.Segment.Id == currentTail.Id)?.MarkSeen(observedAt);
            return true;
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

        private IReadOnlyList<RecentWindowMatch> FindRecentWindowMatches(
            IReadOnlyList<string> completed,
            int matchLimit)
        {
            if (matchLimit <= 0 || recentSegments.Count == 0)
                return [];

            List<RecentWindowMatch> best = [];
            for (int start = recentSegments.Count - 1; start >= 0; start--)
            {
                var candidateMatches = new List<RecentWindowMatch>();
                int ledgerIndex = start;
                for (int currentIndex = 0;
                     currentIndex < matchLimit && ledgerIndex < recentSegments.Count;
                     currentIndex++, ledgerIndex++)
                {
                    RecentSegmentEntry entry = recentSegments[ledgerIndex];
                    string observedText = completed[currentIndex];
                    RecentMatchKind? kind = ClassifyRecentMatch(
                        entry,
                        ledgerIndex,
                        observedText,
                        completed,
                        currentIndex,
                        matchLimit);
                    if (!kind.HasValue)
                        break;

                    candidateMatches.Add(new RecentWindowMatch(
                        entry,
                        kind.Value,
                        observedText));
                }

                if (candidateMatches.Count > best.Count)
                    best = candidateMatches;
            }
            return best;
        }

        private RecentMatchKind? ClassifyRecentMatch(
            RecentSegmentEntry entry,
            int ledgerIndex,
            string observedText,
            IReadOnlyList<string> completed,
            int currentIndex,
            int matchLimit)
        {
            if (entry.IsCurrentText(observedText))
                return RecentMatchKind.Current;
            if (entry.IsHistoricalRevision(observedText))
                return RecentMatchKind.HistoricalRevision;

            if (currentIndex == 0 && completed.Count > 1 &&
                IsLikelyTruncatedLeadingFinal(entry.Segment.Text, observedText))
            {
                return RecentMatchKind.TruncatedWindowHead;
            }

            bool hasLeftContext = currentIndex > 0;
            bool hasRightContext =
                currentIndex + 1 < matchLimit &&
                ledgerIndex + 1 < recentSegments.Count &&
                IsKnownRevisionText(
                    recentSegments[ledgerIndex + 1],
                    completed[currentIndex + 1]);
            bool isCurrentTailSlot =
                matchLimit == 1 && ledgerIndex == recentSegments.Count - 1;
            if (!(hasLeftContext || hasRightContext || isCurrentTailSlot))
                return null;

            return CaptionRevisionPolicy.IsRevision(entry.Segment.Text, observedText) ||
                   IsStrongPositionSupportedRevision(entry.Segment.Text, observedText)
                ? RecentMatchKind.Revision
                : null;
        }

        private static bool IsKnownRevisionText(
            RecentSegmentEntry entry,
            string observedText)
        {
            return entry.IsCurrentText(observedText) ||
                   entry.IsHistoricalRevision(observedText);
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
            bool hasCompletedSentences)
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
                // Temporary terminal punctuation can disappear as the same
                // tail continues. Explicit appended drafts and new short-prefix
                // utterances must still receive their own identity.
                RecentSegmentEntry? continuationEntry = null;
                if (canContinueFinal &&
                    lastFinal != null &&
                    previousCompleted.LastOrDefault()?.Id == lastFinal.Id)
                {
                    continuationEntry = recentSegments.FirstOrDefault(
                        entry => entry.Segment.Id == lastFinal.Id);
                }
                bool continuesFinal = canContinueFinal &&
                    lastFinal != null &&
                    previousCompleted.LastOrDefault()?.Id == lastFinal.Id &&
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

            activeDraft = CreateSegment(observedDraft, isFinal: false, observedAt);
            activeDraftChangedAt = observedAt;
            activeDraftEligible = true;
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
            return new LiveCaptionUpdate(
                normalized,
                finalized,
                activeDraft,
                currentSegment,
                currentText,
                activeDraftEligible,
                stableFor < TimeSpan.Zero ? TimeSpan.Zero : stableFor);
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

        private static (List<string> Completed, string Draft) SplitSentences(string text)
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
            while (TryTakeLongReadingUnit(draft, out string unit, out string remainder))
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
