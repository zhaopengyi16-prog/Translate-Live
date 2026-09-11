using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.services;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class TypographyServiceTests
    {
        [TestMethod]
        public void MissingFontFallsBackToInstalledWindowsUiFont()
        {
            string normalized = TypographyService.NormalizeFamilyName(
                "__LectureCopilot_Missing_Font__");
            var resolved = TypographyService.ResolveFontFamily(
                "__LectureCopilot_Missing_Font__");

            Assert.IsFalse(string.IsNullOrWhiteSpace(normalized));
            StringAssert.Contains(resolved.Source, TypographyService.FALLBACK_CJK_FAMILY);
            StringAssert.Contains(resolved.Source, TypographyService.FALLBACK_UI_FAMILY);
        }

        [TestMethod]
        public void InstalledFontCatalogContainsSearchableNames()
        {
            var fonts = TypographyService.GetInstalledFonts();

            Assert.IsTrue(fonts.Count > 0);
            Assert.IsTrue(fonts.All(font =>
                !string.IsNullOrWhiteSpace(font.FamilyName) &&
                !string.IsNullOrWhiteSpace(font.DisplayName) &&
                font.SearchText.Contains(
                    font.FamilyName,
                    StringComparison.CurrentCultureIgnoreCase)));
        }

        [TestMethod]
        public void SubtitleFontSizeIsClampedToSafeLayoutRange()
        {
            var appearance = new AppearanceSettings
            {
                SubtitleFontSize = 100
            };
            Assert.AreEqual(
                AppearanceSettings.MAX_SUBTITLE_FONT_SIZE,
                appearance.SubtitleFontSize);

            appearance.SubtitleFontSize = 1;
            Assert.AreEqual(
                AppearanceSettings.MIN_SUBTITLE_FONT_SIZE,
                appearance.SubtitleFontSize);

            appearance.SubtitleFontSize = double.NaN;
            Assert.AreEqual(
                AppearanceSettings.DEFAULT_SUBTITLE_FONT_SIZE,
                appearance.SubtitleFontSize);
        }
    }
}
