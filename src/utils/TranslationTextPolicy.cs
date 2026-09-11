namespace LiveCaptionsTranslator.utils
{
    public static class TranslationTextPolicy
    {
        public const string EmptyResponseError =
            "[ERROR] Translation Failed: The API returned no translation text.";

        public static bool IsProviderNotice(string? text)
        {
            return !string.IsNullOrWhiteSpace(text) &&
                   (text.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("[WARNING]", StringComparison.OrdinalIgnoreCase));
        }

        public static string EnsureUsable(string? text)
        {
            return string.IsNullOrWhiteSpace(text) ? EmptyResponseError : text;
        }

        public static bool IsCompleteSentence(string? text)
        {
            return !string.IsNullOrWhiteSpace(text) &&
                   Array.IndexOf(TextUtil.PUNC_EOS, text[^1]) != -1;
        }

        public static int GetRealtimeAttemptLimit(
            string? text,
            int completedSentenceAttemptLimit)
        {
            return IsCompleteSentence(text)
                ? Math.Max(1, completedSentenceAttemptLimit)
                : 1;
        }

        public static bool TryExtractTargetSentence(string? text, out string translatedText)
        {
            translatedText = string.Empty;
            if (string.IsNullOrWhiteSpace(text) || IsProviderNotice(text))
                return false;

            var match = RegexPatterns.TargetSentence().Match(text);
            if (!match.Success)
                return false;

            translatedText = match.Groups[1].Value.Trim();
            return !string.IsNullOrWhiteSpace(translatedText);
        }
    }
}
