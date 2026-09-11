using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.services;
using LiveCaptionsTranslator.utils;
using LiveCaptionsTranslator.Utils;

using StandardButton = System.Windows.Controls.Button;
using StandardTextBlock = System.Windows.Controls.TextBlock;
using UiButton = Wpf.Ui.Controls.Button;

namespace LiveCaptionsTranslator
{
    public partial class MainWindow : FluentWindow
    {
        private readonly Dictionary<Type, Page> pageCache = [];
        private StandardButton? selectedNavigationButton;
        private IReadOnlyList<LectureSessionEntry> recentSessions = [];
        private int recentSessionsRefreshVersion;

        public OverlayWindow? OverlayWindow { get; set; }
        public bool IsAutoHeight { get; set; } = true;
        public double WorkspaceViewportHeight => ContentFrame.ActualHeight;
        public bool IsOverlayVisible => OverlayWindow != null;

        public MainWindow()
        {
            InitializeComponent();
            ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.None, true);

            Loaded += async (_, _) =>
            {
                NavigateTo(typeof(LectureWorkspacePage));
                IsAutoHeight = false;
                await RefreshRecentSessionsAsync();
                await CheckForUpdates();
            };

            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;

            var windowState = WindowHandler.LoadState(this, Translator.Setting);
            if (windowState.Width < MinWidth || windowState.Height < MinHeight)
            {
                WindowHandler.RestoreState(this, new Rect(
                    Math.Max(0, (screenWidth - 1360) / 2),
                    Math.Max(0, (screenHeight - 840) / 2),
                    Math.Min(1360, screenWidth),
                    Math.Min(840, screenHeight)));
            }
            else
            {
                WindowHandler.RestoreState(this, windowState);
            }

            ToggleTopmost(Translator.Setting.MainWindow.Topmost);
            ShowLogCard(Translator.Setting.MainWindow.CaptionLogEnabled);
        }

        private void NavButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is StandardButton button && button.Tag is Type pageType)
                NavigateTo(pageType);
        }

        public async Task RefreshRecentSessionsAsync()
        {
            int requestVersion = Interlocked.Increment(ref recentSessionsRefreshVersion);
            try
            {
                var sessions = await SQLiteHistoryLogger.LoadSessionsAsync(
                    string.Empty,
                    CancellationToken.None);
                if (requestVersion != Volatile.Read(ref recentSessionsRefreshVersion))
                    return;

                recentSessions = RecentSessionProjection.Take(sessions);
                RenderRecentSessions();
            }
            catch (Exception exception)
            {
                if (requestVersion != Volatile.Read(ref recentSessionsRefreshVersion))
                    return;

                ProductDiagnostics.Write("navigation.recent-sessions-failed", exception);
                if (recentSessions.Count == 0)
                    RenderRecentSessions("最近课堂暂时无法载入");
            }
        }

        public void RemoveRecentSession(long sessionId)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => RemoveRecentSession(sessionId));
                return;
            }

            Interlocked.Increment(ref recentSessionsRefreshVersion);
            recentSessions = RecentSessionProjection.Remove(recentSessions, sessionId);
            RenderRecentSessions(
                recentSessions.Count == 0
                    ? "正在同步最近课堂…"
                    : "结束一节课堂后会显示在这里");
        }

        private void RenderRecentSessions(
            string emptyText = "结束一节课堂后会显示在这里")
        {
            RecentSessionsPanel.Children.Clear();
            foreach (LectureSessionEntry session in recentSessions)
            {
                var title = new StandardTextBlock
                {
                    Text = session.Title,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontSize = 12.5,
                    Foreground = (Brush)FindResource("LectureTextPrimaryBrush")
                };
                var detail = new StandardTextBlock
                {
                    Margin = new Thickness(0, 3, 0, 0),
                    Text = session.Detail,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontSize = 10.5,
                    Foreground = (Brush)FindResource("LectureTextTertiaryBrush")
                };
                var recentButton = new StandardButton
                {
                    Tag = typeof(HistoryPage),
                    CommandParameter = session.Id,
                    ToolTip = session.Title,
                    Content = new StackPanel { Children = { title, detail } },
                    Style = (Style)FindResource("LectureNavButtonStyle"),
                    Margin = new Thickness(10, 1, 10, 1),
                    Padding = new Thickness(12, 7, 10, 7),
                    MinHeight = 50
                };
                recentButton.Click += NavButton_Click;
                RecentSessionsPanel.Children.Add(recentButton);
            }

            if (RecentSessionsPanel.Children.Count != 0)
                return;

            RecentSessionsPanel.Children.Add(new StandardTextBlock
            {
                Margin = new Thickness(22, 8, 14, 8),
                Text = emptyText,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5,
                Foreground = (Brush)FindResource("LectureTextTertiaryBrush")
            });
        }

        private void TopmostButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleTopmost(!Topmost);
        }

        private void OverlayModeButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleOverlay();
        }

        public void ToggleOverlay()
        {
            var symbolIcon = OverlayModeButton.Icon as SymbolIcon;
            if (OverlayWindow == null)
            {
                if (symbolIcon != null)
                {
                    symbolIcon.Symbol = SymbolRegular.ClosedCaption24;
                    symbolIcon.Filled = true;
                }

                OverlayWindow = new OverlayWindow();
                OverlayWindow.Closed += (_, _) =>
                {
                    OverlayWindow = null;
                    if (OverlayModeButton.Icon is SymbolIcon icon)
                    {
                        icon.Symbol = SymbolRegular.ClosedCaptionOff24;
                        icon.Filled = false;
                    }
                };
                OverlayWindow.SizeChanged +=
                    (_, _) => WindowHandler.SaveState(OverlayWindow, Translator.Setting);
                OverlayWindow.LocationChanged +=
                    (_, _) => WindowHandler.SaveState(OverlayWindow, Translator.Setting);

                double screenWidth = SystemParameters.PrimaryScreenWidth;
                double screenHeight = SystemParameters.PrimaryScreenHeight;
                var windowState = WindowHandler.LoadState(OverlayWindow, Translator.Setting);
                if (windowState.Left <= 0 || windowState.Left >= screenWidth ||
                    windowState.Top <= 0 || windowState.Top >= screenHeight)
                {
                    WindowHandler.RestoreState(OverlayWindow, new Rect(
                        (screenWidth - 650) / 2,
                        screenHeight * 5 / 6 - 135,
                        650,
                        135));
                }
                else
                {
                    WindowHandler.RestoreState(OverlayWindow, windowState);
                }

                OverlayWindow.Show();
                return;
            }

            switch (OverlayWindow.OnlyMode)
            {
                case CaptionVisible.TranslationOnly:
                    OverlayWindow.OnlyMode = CaptionVisible.SubtitleOnly;
                    OverlayWindow.OnlyMode = CaptionVisible.Both;
                    break;
                case CaptionVisible.SubtitleOnly:
                    OverlayWindow.OnlyMode = CaptionVisible.Both;
                    break;
            }

            OverlayWindow.Close();
        }

        private void LogOnlyButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as UiButton;
            var symbolIcon = button?.Icon as SymbolIcon;
            Translator.LogOnlyFlag = !Translator.LogOnlyFlag;
            if (symbolIcon != null)
                symbolIcon.Filled = Translator.LogOnlyFlag;
            Translator.ClearContexts();
        }

        private void CaptionLogButton_Click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.MainWindow.CaptionLogEnabled =
                !Translator.Setting.MainWindow.CaptionLogEnabled;
            ShowLogCard(Translator.Setting.MainWindow.CaptionLogEnabled);
            CaptionPage.Instance?.AutoHeight();
        }

        private void MainWindow_LocationChanged(object sender, EventArgs e)
        {
            if (sender is Window window)
                WindowHandler.SaveState(window, Translator.Setting);
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            MainWindow_LocationChanged(sender, e);
            IsAutoHeight = false;
        }

        public void ToggleTopmost(bool enabled)
        {
            if (TopmostButton.Icon is SymbolIcon symbolIcon)
                symbolIcon.Filled = enabled;
            Topmost = enabled;
            Translator.Setting.MainWindow.Topmost = enabled;
        }

        private void CheckForFirstUse()
        {
            if (!Translator.FirstUseFlag)
                return;

            NavigateTo(typeof(SettingPage));
            if (Translator.Window != null)
                LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);

            Dispatcher.InvokeAsync(() =>
            {
                var welcomeWindow = new WelcomeWindow { Owner = this };
                welcomeWindow.Show();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private async Task CheckForUpdates()
        {
            if (Translator.FirstUseFlag)
            {
                CheckForFirstUse();
                return;
            }

            string latestVersion = string.Empty;
            try
            {
                latestVersion = await UpdateUtil.GetLatestVersion();
            }
            catch (Exception exception)
            {
                ProductDiagnostics.Write("update.check-failed", exception);
                return;
            }

            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            var ignoredVersion = Translator.Setting.IgnoredUpdateVersion;
            if (!string.IsNullOrEmpty(ignoredVersion) && ignoredVersion == latestVersion)
                return;
            if (string.IsNullOrEmpty(latestVersion) || latestVersion == currentVersion)
                return;

            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = "发现 Translate Live 新版本",
                Content = $"最新版本：{latestVersion}\n当前版本：{currentVersion}",
                PrimaryButtonText = "查看发布页",
                CloseButtonText = "忽略此版本"
            };
            var result = await dialog.ShowDialogAsync();
            if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = UpdateUtil.GitHubReleasesUrl,
                        UseShellExecute = true
                    });
                }
                catch (Exception exception)
                {
                    ProductDiagnostics.Write("update.open-browser-failed", exception);
                }
            }
            else
            {
                Translator.Setting.IgnoredUpdateVersion = latestVersion;
            }
        }

        public void ShowLogCard(bool enabled)
        {
            if (CaptionLogButton.Icon is SymbolIcon icon)
            {
                icon.Symbol = enabled
                    ? SymbolRegular.History24
                    : SymbolRegular.HistoryDismiss24;
                CaptionPage.Instance?.CollapseTranslatedCaption(enabled);
            }
        }

        public void AutoHeightAdjust(int minHeight = -1, int maxHeight = -1)
        {
            if (minHeight > 0 && Height < minHeight)
            {
                Height = minHeight;
                IsAutoHeight = true;
            }

            if (IsAutoHeight && maxHeight > 0 && Height > maxHeight)
                Height = maxHeight;
        }

        public void EnsureWorkspaceSize()
        {
            IsAutoHeight = false;
            if (Width < MinWidth)
                Width = Math.Min(1240, SystemParameters.WorkArea.Width);
            if (Height < MinHeight)
                Height = Math.Min(760, SystemParameters.WorkArea.Height);
        }

        public void NavigateTo(Type pageType)
        {
            if (!typeof(Page).IsAssignableFrom(pageType))
                return;

            if (!pageCache.TryGetValue(pageType, out Page? page))
            {
                page = (Page?)Activator.CreateInstance(pageType);
                if (page == null)
                    return;
                pageCache[pageType] = page;
            }

            ContentFrame.Content = page;
            ContentFrame.BeginAnimation(OpacityProperty, null);
            if (Translator.Setting?.Appearance.ReduceMotion == true)
            {
                ContentFrame.Opacity = 1;
            }
            else
            {
                ContentFrame.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(180))
                    {
                        EasingFunction = new QuadraticEase
                        {
                            EasingMode = EasingMode.EaseOut
                        }
                    });
            }
            StandardButton? button = pageType == typeof(LectureWorkspacePage)
                ? WorkspaceNavButton
                : pageType == typeof(HistoryPage)
                    ? HistoryNavButton
                    : pageType == typeof(SettingPage)
                        ? SettingsNavButton
                        : pageType == typeof(InfoPage)
                            ? InfoNavButton
                            : null;
            SelectNavigationButton(button);
        }

        private void SelectNavigationButton(StandardButton? button)
        {
            if (selectedNavigationButton != null)
                selectedNavigationButton.Background = Brushes.Transparent;
            if (button != null)
                button.Background = (Brush)FindResource("LectureSelectedBrush");
            selectedNavigationButton = button;
        }
    }
}
