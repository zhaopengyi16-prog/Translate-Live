using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.services
{
    internal sealed record LiveCaptionUpdate(
        string NormalizedText,
        IReadOnlyList<string> FinalizedSentences,
        string DraftText,
        string CurrentText,
        bool DraftIsEligible);

    /// <summary>
    /// Converts the rolling text exposed by Windows Live Captions into stable
    /// sentence updates. It remembers the prior window so old lines are not
    /// emitted again when the OS scrolls, and it reconciles a shifted suffix with
    /// the draft that preceded it.
    /// </summary>
    internal sealed class LiveCaptionSegmenter
    {
        private readonly object stateLock = new();
        private List<string> previousCompleted = [];
        private string activeDraft = string.Empty;
        private string lastFinal = string.Empty;
        private string previousSnapshot = string.Empty;
        private bool suppressNextSnapshot;
        private bool activeDraftEligible;

        public LiveCaptionUpdate Process(string? rawText)
        {
            lock (stateLock)
            {
                string normalized = TextUtil.NormalizeCaptionWhitespace(rawText);
                if (normalized.Length == 0)
                {
                    return new LiveCaptionUpdate(
                        string.Empty,
                        [],
                        activeDraft,
                        activeDraft.Length > 0 ? activeDraft : lastFinal,
                        activeDraftEligible);
                }

                var (completed, observedDraft) = SplitSentences(normalized);
                if (suppressNextSnapshot)
                {
                    suppressNextSnapshot = false;
                    previousCompleted = completed;
                    activeDraft = observedDraft;
                    activeDraftEligible = false;
                    lastFinal = completed.LastOrDefault() ?? string.Empty;
                    previousSnapshot = normalized;
                    string seededCurrent = activeDraft.Length > 0
                        ? activeDraft
                        : lastFinal;
                    return new LiveCaptionUpdate(
                        normalized,
                        [],
                        activeDraft,
                        seededCurrent,
                        false);
                }

                if (string.Equals(normalized, previousSnapshot, StringComparison.Ordinal))
                {
                    return new LiveCaptionUpdate(
                        normalized,
                        [],
                        activeDraft,
                        activeDraft.Length > 0
                            ? activeDraft
                            : completed.LastOrDefault() ?? lastFinal,
                        activeDraftEligible);
                }

                int overlap = FindCompletedWindowOverlap(previousCompleted, completed);
                int firstNewIndex = overlap;

                // A transient all-draft frame can temporarily remove completed
                // sentences from the accessibility text. Do not re-emit the last
                // final when it returns on the next frame.
                if (previousCompleted.Count == 0 &&
                    lastFinal.Length > 0 &&
                    completed.Count > 0 &&
                    AreSameWindowSentence(lastFinal, completed[0]))
                {
                    firstNewIndex = 1;
                }

                var finalized = new List<string>();

                // Live Captions sometimes corrects the most recent sentence after
                // punctuation appears. Emit that correction so persistence can
                // replace the final row instead of leaving stale text.
                if (overlap > 0)
                {
                    string previousLast = previousCompleted[^1];
                    string currentLast = completed[overlap - 1];
                    if (!string.Equals(previousLast, currentLast, StringComparison.Ordinal) &&
                        CaptionRevisionPolicy.IsRevision(previousLast, currentLast) &&
                        AreEquivalentFinals(lastFinal, previousLast))
                    {
                        finalized.Add(currentLast);
                        lastFinal = currentLast;
                    }
                }

                for (int index = firstNewIndex; index < completed.Count; index++)
                {
                    string candidate = completed[index];
                    if (activeDraft.Length > 0 &&
                        CaptionRevisionPolicy.TryReconcile(activeDraft, candidate, out string reconciled))
                    {
                        candidate = reconciled;
                        activeDraft = string.Empty;
                        activeDraftEligible = false;
                    }

                    finalized.Add(candidate);
                    lastFinal = candidate;
                }

                if (observedDraft.Length > 0)
                {
                    if (activeDraft.Length > 0 &&
                        CaptionRevisionPolicy.TryReconcile(activeDraft, observedDraft, out string reconciled))
                    {
                        if (!string.Equals(activeDraft, reconciled, StringComparison.Ordinal))
                            activeDraftEligible = true;
                        activeDraft = reconciled;
                    }
                    else
                    {
                        activeDraft = observedDraft;
                        activeDraftEligible = true;
                    }
                }
                else if (completed.Count > 0)
                {
                    activeDraft = string.Empty;
                    activeDraftEligible = false;
                }

                previousCompleted = completed;
                previousSnapshot = normalized;

                string currentText = activeDraft.Length > 0
                    ? activeDraft
                    : completed.LastOrDefault() ?? lastFinal;
                return new LiveCaptionUpdate(
                    normalized,
                    finalized,
                    activeDraft,
                    currentText,
                    activeDraftEligible);
            }
        }

        public void Reset()
        {
            lock (stateLock)
            {
                ClearState(suppressFirstSnapshot: true);
            }
        }

        /// <summary>
        /// Starts a connection from the text that is already visible in an
        /// existing Live Captions window. An empty baseline means the window is
        /// newly started, so the first spoken sentence must not be suppressed.
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
                previousCompleted = completed;
                activeDraft = draft;
                activeDraftEligible = false;
                lastFinal = completed.LastOrDefault() ?? string.Empty;
                previousSnapshot = normalized;
            }
        }

        private void ClearState(bool suppressFirstSnapshot)
        {
            previousCompleted = [];
            activeDraft = string.Empty;
            lastFinal = string.Empty;
            previousSnapshot = string.Empty;
            suppressNextSnapshot = suppressFirstSnapshot;
            activeDraftEligible = false;
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
                       Array.IndexOf(TextUtil.PUNC_EOS, text[sentenceEnd]) >= 0)
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
            return (completed, draft);
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

            if (index + 1 < text.Length && !char.IsWhiteSpace(text[index + 1]))
                return !char.IsLetterOrDigit(text[index + 1]);

            int next = index + 1;
            while (next < text.Length && char.IsWhiteSpace(text[next]))
                next++;
            if (next >= text.Length)
                return true;

            string token = ReadTokenBefore(text, index);
            if (IsKnownAbbreviation(token) || IsInitialism(token))
                return false;

            return true;
        }

        private static string ReadTokenBefore(string text, int periodIndex)
        {
            int start = periodIndex;
            while (start > 0 &&
                   !char.IsWhiteSpace(text[start - 1]) &&
                   !char.IsPunctuation(text[start - 1]))
            {
                start--;
            }

            // Include dots inside an initialism such as U.S.
            while (start > 1 && text[start - 1] == '.' && char.IsLetter(text[start - 2]))
            {
                start -= 2;
                while (start > 0 && char.IsLetter(text[start - 1]))
                    start--;
            }

            return text[start..(periodIndex + 1)];
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
            IReadOnlyList<string> previous,
            IReadOnlyList<string> current)
        {
            int maximum = Math.Min(previous.Count, current.Count);
            for (int count = maximum; count > 0; count--)
            {
                bool matches = true;
                for (int offset = 0; offset < count; offset++)
                {
                    string left = previous[previous.Count - count + offset];
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
                .TrimEnd(TextUtil.PUNC_EOS);
            string right = TextUtil.NormalizeCaptionWhitespace(current)
                .TrimEnd(TextUtil.PUNC_EOS);
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
    }
}
