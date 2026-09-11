using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Globalization;
using Wpf.Ui.Controls;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.controls;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.services;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace LiveCaptionsTranslator
{
    public partial class SettingWindow : FluentWindow
    {
        private static readonly HashSet<string> CredentialProviders =
        [
            "Summary",
            "OpenAI",
            "OpenRouter",
            "DeepL",
            "Youdao",
            "MTranServer",
            "Baidu",
            "LibreTranslate"
        ];

        private readonly CredentialConnectionTester credentialConnectionTester = new();
        private readonly List<FontOption> installedFonts = [];
        private System.Windows.Controls.Button? currentSelected;
        private Dictionary<string, FrameworkElement> sectionReferences = [];
        private string? currentCredentialProvider;
        private bool updatingFontSelection;

        public SettingWindow()
        {
            InitializeComponent();
            DataContext = Translator.Setting;

            Loaded += (sender, args) =>
            {
                Initialize();
                InitializeFontSettings();
                if (Translator.Setting?.Appearance != null)
                    Translator.Setting.Appearance.PropertyChanged += Appearance_PropertyChanged;
                SelectButton(AppearanceButton);
            };
            Closing += (_, _) =>
            {
                if (Translator.Setting?.Appearance != null)
                    Translator.Setting.Appearance.PropertyChanged -= Appearance_PropertyChanged;
                CommitPendingBindings(this);
                Translator.Setting?.Save();
                string? warning = Translator.Setting?.CredentialStorageWarning;
                if (!string.IsNullOrWhiteSpace(warning))
                {
                    System.Windows.MessageBox.Show(
                        this,
                        warning,
                        "Translate Live · API 凭据需要处理",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }
            };
        }

        private static void CommitPendingBindings(DependencyObject root)
        {
            if (root is System.Windows.Controls.TextBox textBox)
                textBox.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            else if (root is Wpf.Ui.Controls.NumberBox numberBox)
                numberBox.GetBindingExpression(Wpf.Ui.Controls.NumberBox.ValueProperty)?.UpdateSource();
            else if (root is CredentialBox credentialBox)
                credentialBox.GetBindingExpression(CredentialBox.ValueProperty)?.UpdateSource();

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                CommitPendingBindings(VisualTreeHelper.GetChild(root, i));
        }

        private void Initialize()
        {
            sectionReferences = new Dictionary<string, FrameworkElement>
            {
                { "General", ContentPanel },
                { "Appearance", AppearanceSection },
                { "Prompt", PromptSection },
                { "Summary", SummarySection }
            };
            
            foreach (var apiName in TranslateAPI.TRANSLATE_FUNCTIONS.Keys.Where(apiName =>
                         !TranslateAPI.NO_CONFIG_APIS.Contains(apiName)))
            {
                sectionReferences[apiName] = FindName($"{apiName}Section") as StackPanel;
                SwitchConfig(apiName, Translator.Setting.ConfigIndices[apiName]);
            }
        }
        
        private void NewButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                string apiName = button.Tag as string;
                var configs = Translator.Setting.Configs[apiName];
                var configIndex = Translator.Setting.ConfigIndices[apiName];
                
                var type = Type.GetType($"LiveCaptionsTranslator.models.{apiName}Config");
                var config = Activator.CreateInstance(type) as TranslateAPIConfig;
                if (config == null)
                    return;
                Translator.Setting.AttachConfigOwner(config);
                configs.Insert(configIndex + 1, config);
                SwitchConfig(apiName, configIndex + 1);
                
                Translator.Setting.OnPropertyChanged("Configs");
            }
        }
        
        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                string apiName = button.Tag as string;
                var configs = Translator.Setting.Configs[apiName];
                var configIndex = Translator.Setting.ConfigIndices[apiName];

                if (configs.Count <= 1)
                {
                    (FindName($"{apiName}DeleteFlyout") as Flyout)?.Show();
                    return;
                }
                configs.RemoveAt(configIndex);
                SwitchConfig(apiName, Math.Max(0, Math.Min(configs.Count - 1, configIndex)));
                
                Translator.Setting.OnPropertyChanged("Configs");
            }
        }

        private void PriorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                string apiName = button.Tag as string;
                var configIndex = Translator.Setting.ConfigIndices[apiName];
                SwitchConfig(apiName, configIndex - 1);
            }
        }
        
        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                string apiName = button.Tag as string;
                var configIndex = Translator.Setting.ConfigIndices[apiName];
                SwitchConfig(apiName, configIndex + 1);
            }
        }

        private void NavigationButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button)
            {
                SelectButton(button);
                string targetSection = button.Tag?.ToString() ?? string.Empty;
                if (sectionReferences.TryGetValue(targetSection, out FrameworkElement element))
                    element.BringIntoView();
            }
        }

        private async void TestCredential_Click(object sender, RoutedEventArgs e)
        {
            string? provider = currentCredentialProvider;
            if (provider == null || Translator.Setting == null)
                return;

            CommitPendingBindings(this);
            TestCredentialButton.IsEnabled = false;
            ClearCredentialButton.IsEnabled = false;
            CredentialActionStatus.Text = $"正在测试 {provider}，最长等待 15 秒……";
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                CredentialConnectionResult result = await credentialConnectionTester.TestAsync(
                    provider,
                    Translator.Setting,
                    timeout.Token);
                CredentialActionStatus.Text = result.Message;
            }
            finally
            {
                TestCredentialButton.IsEnabled = true;
                ClearCredentialButton.IsEnabled = true;
            }
        }

        private void ClearCredential_Click(object sender, RoutedEventArgs e)
        {
            string? provider = currentCredentialProvider;
            if (provider == null || Translator.Setting == null)
                return;

            CommitPendingBindings(this);
            if (!Translator.Setting.HasCredentials(provider))
            {
                CredentialActionStatus.Text = $"{provider} 当前没有已保存凭据，可直接重新输入。";
                FocusCredentialEditor(provider);
                return;
            }

            System.Windows.MessageBoxResult answer = System.Windows.MessageBox.Show(
                this,
                $"确认清除当前 {provider} 配置的凭据吗？普通设置和课堂记录不会被删除。",
                "Translate Live · 清除 API 凭据",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);
            if (answer != System.Windows.MessageBoxResult.Yes)
                return;

            bool cleared = Translator.Setting.ClearCredentials(provider);
            if (!cleared)
            {
                CredentialActionStatus.Text = $"{provider} 当前没有可清除的凭据。";
            }
            else if (Translator.Setting.CredentialStorageIssue == CredentialStorageIssue.None)
            {
                CredentialActionStatus.Text = $"{provider} 凭据已清除，请重新输入后测试连接。";
            }
            else
            {
                CredentialActionStatus.Text = "凭据清除未能安全落盘，请根据关闭窗口时的提示处理。";
            }

            RevealCredentialsToggle.IsChecked = false;
            FocusCredentialEditor(provider);
        }

        private void OpenAIAPIUrlInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            OpenAIAPIUrlInfoFlyout.Show();
        }

        private void OpenAIAPIUrlInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            OpenAIAPIUrlInfoFlyout.Hide();
        }
        
        private void OllamaAPIUrlInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            OllamaAPIUrlInfoFlyout.Show();
        }

        private void OllamaAPIUrlInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            OllamaAPIUrlInfoFlyout.Hide();
        }
        
        private void SwitchConfig(string apiName, int index)
        {
            if (index < 0 || index >= Translator.Setting.Configs[apiName].Count)
                return;
            
            if (Translator.Setting.ConfigIndices[apiName] != index)
                Translator.Setting.ConfigIndices[apiName] = index;
            
            if (FindName($"{apiName}Index") is TextBlock indexTextBlock)
            {
                int total = Translator.Setting.Configs[apiName].Count;
                indexTextBlock.Text = $"{index + 1}/{total}";
            }
            Translator.Setting.OnPropertyChanged(null);
        }
        
        private void SelectButton(System.Windows.Controls.Button button)
        {
            if (currentSelected != null)
                currentSelected.Background = new SolidColorBrush(Colors.Transparent);
            button.Background = (Brush)FindResource("LectureSelectedBrush");
            currentSelected = button;
            UpdateCredentialActions(button.Tag?.ToString());
        }

        private void InitializeFontSettings()
        {
            installedFonts.Clear();
            foreach (FontFamily family in Fonts.SystemFontFamilies.OrderBy(font => font.Source))
            {
                string[] localizedNames = family.FamilyNames.Values
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                string localized = family.FamilyNames.TryGetValue(
                    XmlLanguage.GetLanguage(CultureInfo.CurrentUICulture.IetfLanguageTag),
                    out string? currentName)
                        ? currentName
                        : localizedNames.FirstOrDefault() ?? family.Source;
                string displayName = string.Equals(
                    localized,
                    family.Source,
                    StringComparison.CurrentCultureIgnoreCase)
                        ? family.Source
                        : $"{localized} ({family.Source})";
                installedFonts.Add(new FontOption(
                    family.Source,
                    displayName,
                    string.Join(' ', localizedNames.Append(family.Source))));
            }

            RefreshFontList(FontSearchBox.Text);
            SelectConfiguredFont();
            UpdateFontSupportText();
        }

        private void FontSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshFontList(FontSearchBox.Text);
        }

        private void RefreshFontList(string? query)
        {
            string normalized = query?.Trim() ?? string.Empty;
            IEnumerable<FontOption> matches = string.IsNullOrWhiteSpace(normalized)
                ? installedFonts
                : installedFonts.Where(font =>
                    font.SearchText.Contains(normalized, StringComparison.CurrentCultureIgnoreCase));

            updatingFontSelection = true;
            UiFontFamilyBox.ItemsSource = matches.ToList();
            updatingFontSelection = false;
            SelectConfiguredFont();
        }

        private void SelectConfiguredFont()
        {
            string configured = Translator.Setting?.Appearance.UiFontFamily ?? string.Empty;
            var match = (UiFontFamilyBox.ItemsSource as IEnumerable<FontOption>)?
                .FirstOrDefault(font => string.Equals(
                    font.FamilyName,
                    configured,
                    StringComparison.CurrentCultureIgnoreCase));
            if (match == null)
                return;

            updatingFontSelection = true;
            UiFontFamilyBox.SelectedItem = match;
            updatingFontSelection = false;
        }

        private void UiFontFamilyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (updatingFontSelection ||
                UiFontFamilyBox.SelectedItem is not FontOption selected ||
                Translator.Setting?.Appearance == null)
            {
                return;
            }

            Translator.Setting.Appearance.UiFontFamily = selected.FamilyName;
            ApplyAppearanceResources();
            UpdateFontSupportText();
        }

        private void ResetAppearance_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting?.Appearance == null)
                return;

            string defaultFamily = installedFonts.Any(font => string.Equals(
                font.FamilyName,
                "Segoe UI Variable",
                StringComparison.OrdinalIgnoreCase))
                    ? "Segoe UI Variable"
                    : "Segoe UI";
            Translator.Setting.Appearance.UiFontFamily = defaultFamily;
            Translator.Setting.Appearance.SubtitleFontSize = 20;
            Translator.Setting.Appearance.ReduceMotion = false;
            FontSearchBox.Clear();
            ApplyAppearanceResources();
            SelectConfiguredFont();
            UpdateFontSupportText();
        }

        private void Appearance_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            ApplyAppearanceResources();
            if (e.PropertyName == nameof(AppearanceSettings.UiFontFamily))
            {
                SelectConfiguredFont();
                UpdateFontSupportText();
            }
        }

        private static void ApplyAppearanceResources()
        {
            var appearance = Translator.Setting?.Appearance;
            TypographyService.Apply(appearance);
        }

        private void UpdateFontSupportText()
        {
            string familyName = Translator.Setting?.Appearance.UiFontFamily ?? "Segoe UI";
            FontSupportText.Text = HasCjkGlyphs(familyName)
                ? $"{familyName} 包含常用中文字符；缺失字符仍会自动回退。"
                : $"{familyName} 缺少完整中文字形；中文将由 Microsoft YaHei UI 自动回退。";
        }

        private static bool HasCjkGlyphs(string familyName)
        {
            try
            {
                var family = Fonts.SystemFontFamilies.FirstOrDefault(font => string.Equals(
                    font.Source,
                    familyName,
                    StringComparison.OrdinalIgnoreCase));
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
                // A broken or remotely supplied font must never destabilize settings.
            }

            return false;
        }

        private sealed record FontOption(
            string FamilyName,
            string DisplayName,
            string SearchText);

        private void UpdateCredentialActions(string? provider)
        {
            bool supported = provider != null && CredentialProviders.Contains(provider);
            currentCredentialProvider = supported ? provider : null;
            TestCredentialButton.IsEnabled = supported;
            ClearCredentialButton.IsEnabled = supported;
            CredentialTargetText.Text = supported
                ? $"凭据安全 · {provider}"
                : "凭据安全 · 当前分区没有 API 凭据";
            CredentialActionStatus.Text = supported
                ? "默认遮罩显示；可测试当前配置，或清除后重新录入。"
                : "请选择 Summary 或一个需要凭据的翻译服务。";
        }

        private void FocusCredentialEditor(string provider)
        {
            CredentialBox? editor = provider switch
            {
                "Summary" => SummaryCredential,
                "OpenAI" => OpenAICredential,
                "OpenRouter" => OpenRouterCredential,
                "DeepL" => DeepLCredential,
                "Youdao" => YoudaoCredential,
                "MTranServer" => MTranServerCredential,
                "Baidu" => BaiduCredential,
                "LibreTranslate" => LibreTranslateCredential,
                _ => null
            };
            editor?.FocusEditor();
        }
    }
}
