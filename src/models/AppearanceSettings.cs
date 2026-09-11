using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

using LiveCaptionsTranslator.services;

namespace LiveCaptionsTranslator.models
{
    public sealed class AppearanceSettings : INotifyPropertyChanged
    {
        public const double DEFAULT_SUBTITLE_FONT_SIZE = 20;
        public const double MIN_SUBTITLE_FONT_SIZE = 14;
        public const double MAX_SUBTITLE_FONT_SIZE = 32;

        private string uiFontFamily = TypographyService.GetDefaultFamilyName();
        private double subtitleFontSize = DEFAULT_SUBTITLE_FONT_SIZE;
        private bool reduceMotion;

        [JsonIgnore]
        internal Setting? Owner { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string UiFontFamily
        {
            get => uiFontFamily;
            set
            {
                string normalized = TypographyService.NormalizeFamilyName(value);
                if (string.Equals(uiFontFamily, normalized, StringComparison.OrdinalIgnoreCase))
                    return;
                uiFontFamily = normalized;
                OnPropertyChanged();
            }
        }

        public double SubtitleFontSize
        {
            get => subtitleFontSize;
            set
            {
                double normalized = double.IsFinite(value)
                    ? Math.Clamp(value, MIN_SUBTITLE_FONT_SIZE, MAX_SUBTITLE_FONT_SIZE)
                    : DEFAULT_SUBTITLE_FONT_SIZE;
                if (Math.Abs(subtitleFontSize - normalized) < 0.01)
                    return;
                subtitleFontSize = normalized;
                OnPropertyChanged();
            }
        }

        public bool ReduceMotion
        {
            get => reduceMotion;
            set
            {
                if (reduceMotion == value)
                    return;
                reduceMotion = value;
                OnPropertyChanged();
            }
        }

        public void Reset()
        {
            UiFontFamily = TypographyService.GetDefaultFamilyName();
            SubtitleFontSize = DEFAULT_SUBTITLE_FONT_SIZE;
            ReduceMotion = false;
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            TypographyService.Apply(this);
            Owner?.OnPropertyChanged(nameof(Setting.Appearance));
        }
    }
}
