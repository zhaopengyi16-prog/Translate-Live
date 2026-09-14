using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;
using Wpf.Ui.Controls;

namespace LiveCaptionsTranslator
{
    public partial class SettingPage : Page
    {
        private sealed record CaptionSourceOption(
            CaptionSourceKind Kind,
            string DisplayName);

        private static SettingWindow? SettingWindow;
        private readonly CaptionSourceOption[] captionSourceOptions =
        [
            new(CaptionSourceKind.WindowsLiveCaptions, "Windows Live Captions"),
            new(CaptionSourceKind.LocalSherpaOnnx, "本地 ASR · sherpa-onnx（实验）")
        ];

        public SettingPage()
        {
            InitializeComponent();
            DataContext = Translator.Setting;

            Loaded += (s, e) =>
            {
                (App.Current.MainWindow as MainWindow)?.EnsureWorkspaceSize();
                CheckForFirstUse();
            };

            TranslateAPIBox.ItemsSource = Translator.Setting?.Configs.Keys;
            TranslateAPIBox.SelectedItem = Translator.Setting?.ApiName;
            CaptionSourceBox.ItemsSource = captionSourceOptions;
            CaptionSourceBox.SelectedItem = captionSourceOptions.First(option =>
                option.Kind == (Translator.Setting?.CaptionSource ??
                    CaptionSourceKind.WindowsLiveCaptions));
            RefreshCaptionSourceControls();

            LoadAPISetting();
        }

        private void CaptionSourceBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (CaptionSourceBox.SelectedItem is CaptionSourceOption option &&
                Translator.Setting != null)
            {
                Translator.Setting.CaptionSource = option.Kind;
            }
            RefreshCaptionSourceControls();
        }

        private void RefreshCaptionSourceControls()
        {
            bool local = (CaptionSourceBox.SelectedItem as CaptionSourceOption)?.Kind ==
                CaptionSourceKind.LocalSherpaOnnx;
            LocalAsrSettingsPanel.Visibility = local
                ? Visibility.Visible
                : Visibility.Collapsed;
            LiveCaptionsButton.Visibility = local
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private async void LiveCaptionsButton_click(object sender, RoutedEventArgs e)
        {
            if (Translator.Window == null)
            {
                ButtonText.Text = "正在重新连接…";
                bool connected = await Translator.ConnectLiveCaptionsAsync(userInitiated: true);
                ButtonText.Text = connected
                    ? LiveCaptionsHandler.IsHiddenByCurrentApp
                        ? "显示系统字幕"
                        : "隐藏系统字幕"
                    : "重新连接系统字幕";
                return;
            }

            bool isHide = LiveCaptionsHandler.IsHiddenByCurrentApp;
            if (isHide)
            {
                LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);
                ButtonText.Text = "隐藏系统字幕";
            }
            else
            {
                LiveCaptionsHandler.HideLiveCaptions(Translator.Window);
                ButtonText.Text = "显示系统字幕";
            }
        }

        private void TranslateAPIBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TranslateAPIBox.SelectedItem is string selectedApi &&
                Translator.Setting != null &&
                !string.Equals(Translator.Setting.ApiName, selectedApi, StringComparison.Ordinal))
            {
                // SelectionChanged may run before the SelectedItem binding updates its source.
                // Commit the selected provider explicitly so the settings page and workspace agree.
                Translator.Setting.ApiName = selectedApi;
            }
            LoadAPISetting();
        }

        private void TargetLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TargetLangBox.SelectedItem != null)
                Translator.Setting.TargetLanguage = TargetLangBox.SelectedItem.ToString();
        }

        private void TargetLangBox_LostFocus(object sender, RoutedEventArgs e)
        {
            Translator.Setting.TargetLanguage = TargetLangBox.Text;
        }

        private void APISettingButton_click(object sender, RoutedEventArgs e)
        {
            if (SettingWindow != null && SettingWindow.IsLoaded)
                SettingWindow.Activate();
            else
            {
                SettingWindow = new SettingWindow
                {
                    Owner = Application.Current.MainWindow
                };
                SettingWindow.Closed += (sender, args) => SettingWindow = null;
                SettingWindow.Show();
            }
        }

        private void Contexts_ValueChanged(object sender, NumberBoxValueChangedEventArgs args)
        {
            if (Translator.Setting.DisplaySentences > Translator.Setting.NumContexts)
                Translator.Setting.DisplaySentences = Translator.Setting.NumContexts;
        }

        private void DisplaySentences_ValueChanged(object sender, NumberBoxValueChangedEventArgs args)
        {
            if (Translator.Setting.DisplaySentences > Translator.Setting.NumContexts)
                Translator.Setting.NumContexts = Translator.Setting.DisplaySentences;
            Translator.Caption.OnPropertyChanged("DisplayLogCards");
            Translator.Caption.OnPropertyChanged("OverlayPreviousTranslation");
        }

        private void LiveCaptionsInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            LiveCaptionsInfoFlyout.Show();
        }

        private void LiveCaptionsInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            LiveCaptionsInfoFlyout.Hide();
        }

        private void FrequencyInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            FrequencyInfoFlyout.Show();
        }

        private void FrequencyInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            FrequencyInfoFlyout.Hide();
        }

        private void TranslateAPIInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            TranslateAPIInfoFlyout.Show();
        }

        private void TranslateAPIInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            TranslateAPIInfoFlyout.Hide();
        }

        private void TargetLangInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            TargetLangInfoFlyout.Show();
        }

        private void TargetLangInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            TargetLangInfoFlyout.Hide();
        }

        private void CaptionLogMaxInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            CaptionLogMaxInfoFlyout.Show();
        }

        private void CaptionLogMaxInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            CaptionLogMaxInfoFlyout.Hide();
        }

        private void ContextAwareInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            ContextAwareInfoFlyout.Show();
        }

        private void ContextAwareInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            ContextAwareInfoFlyout.Hide();
        }

        private void CheckForFirstUse()
        {
            if (Translator.FirstUseFlag)
                ButtonText.Text = "隐藏系统字幕";
        }

        public void LoadAPISetting()
        {
            var configType = Translator.Setting[Translator.Setting.ApiName].GetType();
            var languagesProp = configType.GetProperty(
                "SupportedLanguages", BindingFlags.Public | BindingFlags.Static);

            // Traverse base classes to find `SupportedLanguages`
            while (configType != null && languagesProp == null)
            {
                configType = configType.BaseType;
                languagesProp = configType.GetProperty(
                    "SupportedLanguages", BindingFlags.Public | BindingFlags.Static);
            }
            if (languagesProp == null)
                languagesProp = typeof(TranslateAPIConfig).GetProperty(
                    "SupportedLanguages", BindingFlags.Public | BindingFlags.Static);

            var supportedLanguages = (Dictionary<string, string>)languagesProp.GetValue(null);
            TargetLangBox.ItemsSource = supportedLanguages.Keys;

            string targetLang = Translator.Setting.TargetLanguage;
            if (!supportedLanguages.ContainsKey(targetLang))
                supportedLanguages[targetLang] = targetLang;    // add custom language to supported languages
            TargetLangBox.SelectedItem = targetLang;
        }
    }
}
