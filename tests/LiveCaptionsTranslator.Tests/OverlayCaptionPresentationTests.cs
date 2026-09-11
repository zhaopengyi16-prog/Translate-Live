using LiveCaptionsTranslator.services;
using LiveCaptionsTranslator.Utils;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class OverlayCaptionPresentationTests
    {
        [TestMethod]
        public void EmptyOrWhitespaceCaptionsUseTheEmptyState()
        {
            bool hasContent = OverlayCaptionPresentation.HasVisibleContent(
                CaptionVisible.Both,
                " ",
                string.Empty,
                null,
                "  ");

            Assert.IsFalse(hasContent);
        }

        [TestMethod]
        public void VisibilityModeOnlyCountsTheDisplayedCaption()
        {
            Assert.IsFalse(OverlayCaptionPresentation.HasVisibleContent(
                CaptionVisible.TranslationOnly,
                "Original",
                string.Empty,
                string.Empty,
                string.Empty));
            Assert.IsTrue(OverlayCaptionPresentation.HasVisibleContent(
                CaptionVisible.SubtitleOnly,
                "Original",
                string.Empty,
                string.Empty,
                string.Empty));
        }

        [TestMethod]
        public void NoticeOrTranslationCountsAsVisibleTranslation()
        {
            Assert.IsTrue(OverlayCaptionPresentation.HasVisibleContent(
                CaptionVisible.TranslationOnly,
                string.Empty,
                "[Paused]",
                string.Empty,
                string.Empty));
        }
    }
}
