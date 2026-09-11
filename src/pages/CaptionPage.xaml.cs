using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class CaptionPage : Page
    {
        public const int CARD_HEIGHT = 110;

        private static CaptionPage instance;
        public static CaptionPage Instance => instance;

        public CaptionPage()
        {
            InitializeComponent();
            DataContext = Translator.Caption;
            instance = this;

            Loaded += (s, e) =>
            {
                AutoHeight();
                (App.Current.MainWindow as MainWindow).CaptionLogButton.Visibility = Visibility.Visible;
                Translator.Caption.PropertyChanged += TranslatedChanged;
            };
            Unloaded += (s, e) =>
            {
                (App.Current.MainWindow as MainWindow).CaptionLogButton.Visibility = Visibility.Collapsed;
                Translator.Caption.PropertyChanged -= TranslatedChanged;
            };

            CollapseTranslatedCaption(Translator.Setting.MainWindow.CaptionLogEnabled);
        }

        private async void TextBlock_MouseLeftButtonDown(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                try
                {
                    Clipboard.SetText(textBlock.Text);
                    SnackbarHost.Show("Copied.", textBlock.Text, SnackbarType.Info, 100);
                }
                catch
                {
                    SnackbarHost.Show("Copy Failed.", string.Empty, SnackbarType.Error, 100);
                }
                await Task.Delay(500);
            }
        }

        private void TranslatedChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Translator.Caption.DisplayTranslatedCaption))
            {
                if (Encoding.UTF8.GetByteCount(Translator.Caption.DisplayTranslatedCaption) >= TextUtil.LONG_THRESHOLD)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        this.TranslatedCaption.FontSize = Math.Max(
                            AppearanceSettings.MIN_SUBTITLE_FONT_SIZE,
                            Translator.Setting.Appearance.SubtitleFontSize - 3);
                    }), DispatcherPriority.Background);
                }
                else
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        this.TranslatedCaption.FontSize =
                            Translator.Setting.Appearance.SubtitleFontSize;
                    }), DispatcherPriority.Background);
                }
            }
        }

        public void CollapseTranslatedCaption(bool isCollapsed)
        {
            var converter = new GridLengthConverter();

            if (isCollapsed)
            {
                TranslatedCaption_Row.Height = (GridLength)converter.ConvertFromString("Auto");
                LogCards.Visibility = Visibility.Visible;
            }
            else
            {
                TranslatedCaption_Row.Height = (GridLength)converter.ConvertFromString("*");
                LogCards.Visibility = Visibility.Collapsed;
            }
        }

        public void AutoHeight()
        {
            if (Translator.Setting.MainWindow.CaptionLogEnabled)
                (App.Current.MainWindow as MainWindow).AutoHeightAdjust(
                    minHeight: CARD_HEIGHT * (Translator.Setting.DisplaySentences + 1),
                    maxHeight: CARD_HEIGHT * (Translator.Setting.DisplaySentences + 1));
            else
                (App.Current.MainWindow as MainWindow).AutoHeightAdjust(
                    minHeight: (int)App.Current.MainWindow.MinHeight,
                    maxHeight: (int)App.Current.MainWindow.MinHeight);
        }
    }
}
