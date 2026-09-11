using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.services
{
    public sealed record SystemFontChoice(
        string FamilyName,
        string DisplayName,
        string SearchText,
        bool SupportsCjk);

    public static class TypographyService
    {
        public const string FALLBACK_CJK_FAMILY = "Microsoft YaHei UI";
        public const string FALLBACK_UI_FAMILY = "Segoe UI";
        public const string PREFERRED_UI_FAMILY = "Segoe UI Variable";

        private static IReadOnlyList<SystemFontChoice>? cachedFonts;

        public static string GetDefaultFamilyName()
        {
            return IsInstalled(PREFERRED_UI_FAMILY)
                ? PREFERRED_UI_FAMILY
                : FALLBACK_UI_FAMILY;
        }

        public static string NormalizeFamilyName(string? familyName)
        {
            string candidate = familyName?.Trim() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(candidate) && IsInstalled(candidate)
                ? Fonts.SystemFontFamilies.First(font => string.Equals(
                    font.Source,
                    candidate,
                    StringComparison.OrdinalIgnoreCase)).Source
                : GetDefaultFamilyName();
        }

        public static bool IsInstalled(string? familyName)
        {
            if (string.IsNullOrWhiteSpace(familyName))
                return false;

            try
            {
                return Fonts.SystemFontFamilies.Any(font => string.Equals(
                    font.Source,
                    familyName.Trim(),
                    StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return false;
            }
        }

        public static FontFamily ResolveFontFamily(string? familyName)
        {
            string selected = NormalizeFamilyName(familyName);
            return new FontFamily(
                $"{selected}, {FALLBACK_CJK_FAMILY}, {FALLBACK_UI_FAMILY}");
        }

        public static IReadOnlyList<SystemFontChoice> GetInstalledFonts()
        {
            if (cachedFonts != null)
                return cachedFonts;

            try
            {
                XmlLanguage language = XmlLanguage.GetLanguage(
                    CultureInfo.CurrentUICulture.IetfLanguageTag);
                cachedFonts = Fonts.SystemFontFamilies
                    .OrderBy(font => font.Source, StringComparer.CurrentCultureIgnoreCase)
                    .Select(font => CreateChoice(font, language))
                    .ToArray();
            }
            catch
            {
                cachedFonts =
                [
                    new SystemFontChoice(
                        FALLBACK_UI_FAMILY,
                        FALLBACK_UI_FAMILY,
                        FALLBACK_UI_FAMILY,
                        false)
                ];
            }

            return cachedFonts;
        }

        public static bool SupportsCjk(string? familyName)
        {
            if (string.IsNullOrWhiteSpace(familyName))
                return false;

            try
            {
                FontFamily? family = Fonts.SystemFontFamilies.FirstOrDefault(font =>
                    string.Equals(font.Source, familyName, StringComparison.OrdinalIgnoreCase));
                if (family == null)
                    return false;

                foreach (FamilyTypeface typeface in family.FamilyTypefaces)
                {
                    var concreteTypeface = new Typeface(
                        family,
                        typeface.Style,
                        typeface.Weight,
                        typeface.Stretch);
                    if (concreteTypeface.TryGetGlyphTypeface(out GlyphTypeface glyphs) &&
                        glyphs.CharacterToGlyphMap.ContainsKey('中') &&
                        glyphs.CharacterToGlyphMap.ContainsKey('文'))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        public static void Apply(AppearanceSettings? appearance)
        {
            if (appearance == null || Application.Current == null)
                return;

            Application.Current.Resources["LectureUiFontFamily"] =
                ResolveFontFamily(appearance.UiFontFamily);
            Application.Current.Resources["LectureSubtitleFontSize"] =
                Math.Clamp(
                    appearance.SubtitleFontSize,
                    AppearanceSettings.MIN_SUBTITLE_FONT_SIZE,
                    AppearanceSettings.MAX_SUBTITLE_FONT_SIZE);
            Application.Current.Resources["LectureSourceFontSize"] =
                Math.Max(12, appearance.SubtitleFontSize - 5);
            Application.Current.Resources["LectureMotionDuration"] = new Duration(
                appearance.ReduceMotion
                    ? TimeSpan.Zero
                    : TimeSpan.FromMilliseconds(140));
        }

        private static SystemFontChoice CreateChoice(
            FontFamily family,
            XmlLanguage currentLanguage)
        {
            string[] names = family.FamilyNames.Values
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            string localizedName = family.FamilyNames.TryGetValue(
                currentLanguage,
                out string? value)
                    ? value
                    : names.FirstOrDefault() ?? family.Source;
            string displayName = string.Equals(
                localizedName,
                family.Source,
                StringComparison.CurrentCultureIgnoreCase)
                    ? family.Source
                    : $"{localizedName} ({family.Source})";
            string searchText = string.Join(' ', names.Append(family.Source));
            return new SystemFontChoice(
                family.Source,
                displayName,
                searchText,
                SupportsCjk(family.Source));
        }
    }
}
