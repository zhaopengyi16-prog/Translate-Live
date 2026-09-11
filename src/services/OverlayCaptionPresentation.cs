using LiveCaptionsTranslator.Utils;

namespace LiveCaptionsTranslator.services
{
    internal static class OverlayCaptionPresentation
    {
        internal static bool HasVisibleContent(
            CaptionVisible mode,
            string? original,
            string? noticePrefix,
            string? previousTranslation,
            string? currentTranslation)
        {
            bool hasOriginal = !string.IsNullOrWhiteSpace(original);
            bool hasTranslation =
                !string.IsNullOrWhiteSpace(noticePrefix) ||
                !string.IsNullOrWhiteSpace(previousTranslation) ||
                !string.IsNullOrWhiteSpace(currentTranslation);

            return mode switch
            {
                CaptionVisible.TranslationOnly => hasTranslation,
                CaptionVisible.SubtitleOnly => hasOriginal,
                _ => hasOriginal || hasTranslation
            };
        }
    }
}
