namespace LiveCaptionsTranslator.utils
{
    /// <summary>
    /// Decides whether two snapshots describe revisions of one spoken sentence.
    /// The policy is directional: a completed sentence followed by a new draft is
    /// treated as a new utterance, while a draft followed by its completed form is
    /// treated as a revision.
    /// </summary>
    internal static class CaptionRevisionPolicy
    {
        private const double DraftSimilarityThreshold = 0.76;
        private const double FinalCorrectionSimilarityThreshold = 0.90;

        public static bool IsRevision(string? previous, string? current)
        {
            string left = Normalize(previous);
            string right = Normalize(current);
            if (left.Length == 0 || right.Length == 0)
                return false;
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
                return true;

            bool previousIsComplete = TranslationTextPolicy.IsCompleteSentence(left);
            bool currentIsComplete = TranslationTextPolicy.IsCompleteSentence(right);

            // Once a sentence is final, the following unfinished text is normally
            // the beginning of a new sentence, even when it repeats a few words.
            if (previousIsComplete && !currentIsComplete)
                return false;

            if (previousIsComplete && currentIsComplete)
            {
                if (IsGrowingFinalRevision(left, right))
                    return true;

                double lengthRatio = (double)Math.Min(left.Length, right.Length) /
                                     Math.Max(left.Length, right.Length);
                return lengthRatio >= 0.72 &&
                       TextUtil.Similarity(left, right) >= FinalCorrectionSimilarityThreshold;
            }

            if (StartsWithAtWordBoundary(left, right) || StartsWithAtWordBoundary(right, left))
                return true;

            if (FindSubstantialSuffixPrefixOverlap(left, right) > 0)
                return true;

            double draftLengthRatio = (double)Math.Min(left.Length, right.Length) /
                                      Math.Max(left.Length, right.Length);
            return Math.Min(left.Length, right.Length) >= 8 &&
                   draftLengthRatio >= 0.55 &&
                   TextUtil.Similarity(left, right) >= DraftSimilarityThreshold;
        }

        public static bool IsGrowingFinalRevision(string? previous, string? current)
        {
            string left = Normalize(previous).TrimEnd(TextUtil.PUNC_EOS).TrimEnd();
            string right = Normalize(current).TrimEnd(TextUtil.PUNC_EOS).TrimEnd();
            if (left.Length == 0 || right.Length <= left.Length)
                return false;

            return StartsWithAtWordBoundary(right, left);
        }

        public static bool TryReconcile(
            string? previous,
            string? current,
            out string reconciled)
        {
            string left = Normalize(previous);
            string right = Normalize(current);
            reconciled = right;

            if (!IsRevision(left, right))
                return false;

            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
                return true;

            if (StartsWithAtWordBoundary(right, left))
            {
                reconciled = right;
                return true;
            }

            // Trust the most recent recognizer revision when it shortened or
            // corrected the same prefix.
            if (StartsWithAtWordBoundary(left, right))
            {
                reconciled = right;
                return true;
            }

            int overlap = FindSubstantialSuffixPrefixOverlap(left, right);
            if (overlap > 0)
            {
                string prefix = left[..^overlap].TrimEnd();
                reconciled = JoinCaptionParts(prefix, right);
                return true;
            }

            string rightStem = right.TrimEnd(TextUtil.PUNC_EOS);
            if (TranslationTextPolicy.IsCompleteSentence(right) &&
                rightStem.Length > 0 &&
                left.EndsWith(rightStem, StringComparison.OrdinalIgnoreCase))
            {
                string ending = right[rightStem.Length..];
                reconciled = left.TrimEnd(TextUtil.PUNC_EOS) + ending;
                return true;
            }

            // A high-similarity revision usually contains substitutions rather
            // than a shifted rolling window. Prefer the latest recognizer text.
            reconciled = right;
            return true;
        }

        private static string Normalize(string? text)
        {
            return TextUtil.NormalizeCaptionWhitespace(text);
        }

        private static bool StartsWithAtWordBoundary(string text, string prefix)
        {
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;
            if (text.Length == prefix.Length)
                return true;

            char prefixLast = prefix[^1];
            char next = text[prefix.Length];
            return !char.IsLetterOrDigit(prefixLast) ||
                   !char.IsLetterOrDigit(next) ||
                   TextUtil.isCJChar(prefixLast) ||
                   TextUtil.isCJChar(next);
        }

        private static int FindSubstantialSuffixPrefixOverlap(string left, string right)
        {
            int maxLength = Math.Min(left.Length, right.Length);
            for (int length = maxLength; length > 0; length--)
            {
                if (!left.AsSpan(left.Length - length, length)
                        .Equals(right.AsSpan(0, length), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool hasCjk = ContainsCjk(left.AsSpan(left.Length - length, length));
                int absoluteMinimum = hasCjk ? 4 : 8;
                int proportionalMinimum = (int)Math.Ceiling(
                    Math.Min(left.Length, right.Length) * 0.35);
                if (length < Math.Max(absoluteMinimum, proportionalMinimum))
                    continue;

                if (!IsBoundaryBefore(left, left.Length - length) ||
                    !IsBoundaryAfter(right, length))
                {
                    continue;
                }

                return length;
            }

            return 0;
        }

        private static bool ContainsCjk(ReadOnlySpan<char> text)
        {
            foreach (char character in text)
            {
                if (TextUtil.isCJChar(character))
                    return true;
            }
            return false;
        }

        private static bool IsBoundaryBefore(string text, int index)
        {
            if (index <= 0)
                return true;
            return IsWordBoundary(text[index - 1], text[index]);
        }

        private static bool IsBoundaryAfter(string text, int index)
        {
            if (index >= text.Length)
                return true;
            return IsWordBoundary(text[index - 1], text[index]);
        }

        private static bool IsWordBoundary(char left, char right)
        {
            return char.IsWhiteSpace(left) || char.IsWhiteSpace(right) ||
                   char.IsPunctuation(left) || char.IsPunctuation(right) ||
                   TextUtil.isCJChar(left) || TextUtil.isCJChar(right);
        }

        private static string JoinCaptionParts(string prefix, string suffix)
        {
            if (prefix.Length == 0)
                return suffix;
            if (suffix.Length == 0)
                return prefix;

            bool needsSpace = !char.IsWhiteSpace(prefix[^1]) &&
                              !char.IsWhiteSpace(suffix[0]) &&
                              !TextUtil.isCJChar(prefix[^1]) &&
                              !TextUtil.isCJChar(suffix[0]) &&
                              !char.IsPunctuation(suffix[0]);
            return needsSpace ? $"{prefix} {suffix}" : prefix + suffix;
        }
    }
}
