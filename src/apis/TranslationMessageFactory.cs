using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.apis
{
    internal static class TranslationMessageFactory
    {
        public static List<BaseLLMConfig.Message> Create(
            string prompt,
            string targetLanguage,
            string currentText,
            IEnumerable<TranslationHistoryEntry>? contexts)
        {
            var messages = new List<BaseLLMConfig.Message>
            {
                new()
                {
                    role = "system",
                    content = string.Format(prompt, targetLanguage)
                }
            };

            if (contexts != null)
            {
                foreach (TranslationHistoryEntry entry in contexts)
                {
                    if (string.IsNullOrWhiteSpace(entry.SourceText) ||
                        string.IsNullOrWhiteSpace(entry.TranslatedText) ||
                        string.Equals(entry.TranslatedText, "N/A", StringComparison.Ordinal) ||
                        TranslationTextPolicy.IsProviderNotice(entry.TranslatedText))
                    {
                        continue;
                    }

                    string translatedText = RegexPatterns.NoticePrefix()
                        .Replace(entry.TranslatedText, string.Empty)
                        .Trim();
                    if (translatedText.Length == 0)
                        continue;

                    messages.Add(new BaseLLMConfig.Message
                    {
                        role = "user",
                        content = $"🔤 {entry.SourceText} 🔤"
                    });
                    messages.Add(new BaseLLMConfig.Message
                    {
                        role = "assistant",
                        content = translatedText
                    });
                }
            }

            messages.Add(new BaseLLMConfig.Message
            {
                role = "user",
                content = $"🔤 {currentText} 🔤"
            });
            return messages;
        }
    }
}
