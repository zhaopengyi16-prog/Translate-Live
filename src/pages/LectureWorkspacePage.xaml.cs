using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.services;
using LiveCaptionsTranslator.utils;
using LiveCaptionsTranslator.viewmodels;

namespace LiveCaptionsTranslator
{
    public partial class LectureWorkspacePage : Page
    {
        private readonly TranscriptSessionViewModel viewModel = new();
        private readonly DispatcherTimer elapsedTimer = new()
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        private readonly HashSet<long> loadedEntryIds = [];
        private readonly List<TranslationHistoryChange> pendingHistoryChanges = [];
        private readonly object historyProjectionLock = new();
        private readonly SemaphoreSlim modeTransitionGate = new(1, 1);
        private readonly LectureSummaryService summaryService = new();
        private CancellationTokenSource? demoCancellation;
        private Task? demoTask;
        private DateTimeOffset sessionStartedAt = DateTimeOffset.Now;
        private long legacySequence;
        private bool initialized;
        private bool settingSubscribed;
        private bool liveCaptionsConnectionSubscribed;
        private bool historyProjectionInitializing;
        private MainWindow? ownerWindow;

        public LectureWorkspacePage()
        {
            InitializeComponent();
            DataContext = viewModel;

            Loaded += LectureWorkspacePage_Loaded;
            Unloaded += LectureWorkspacePage_Unloaded;
            elapsedTimer.Tick += (_, _) => viewModel.ElapsedText = viewModel.CanEndSession
                ? (DateTimeOffset.Now - sessionStartedAt).ToString(@"hh\:mm\:ss")
                : "00:00:00";
        }

        private async void LectureWorkspacePage_Loaded(object sender, RoutedEventArgs e)
        {
            ownerWindow = Application.Current.MainWindow as MainWindow;
            ownerWindow?.EnsureWorkspaceSize();
            if (ownerWindow != null)
            {
                ownerWindow.SizeChanged -= OwnerWindow_SizeChanged;
                ownerWindow.SizeChanged += OwnerWindow_SizeChanged;
                UpdateViewportHeight();
            }
            elapsedTimer.Start();
            ShowWorkspaceTab("Live");

            if (!settingSubscribed && Translator.Setting != null)
            {
                Translator.Setting.PropertyChanged += Setting_PropertyChanged;
                settingSubscribed = true;
            }
            if (!liveCaptionsConnectionSubscribed)
            {
                Translator.LiveCaptionsConnectionChanged += OnLiveCaptionsConnectionChanged;
                liveCaptionsConnectionSubscribed = true;
            }
            RefreshEngineSummary();

            if (initialized)
                return;

            initialized = true;
            Translator.TranslationEntryLogged += OnTranslationEntryLogged;
            if (Translator.Caption != null)
                Translator.Caption.PropertyChanged += Caption_PropertyChanged;

            viewModel.LiveCaptionsStatus = Translator.Window == null
                ? "Live Captions 未连接"
                : "Live Captions 已就绪";
            viewModel.SessionStatus = Translator.Window == null
                ? "等待 Windows 实时字幕恢复"
                : "等待课程声音";
            try
            {
                await LoadRecentHistoryAsync(clearTimeline: true);
                viewModel.SetDraft(Translator.Caption?.DisplayOriginalCaption);
            }
            catch (Exception exception)
            {
                ProductDiagnostics.Write("workspace.history-load-failed", exception);
                viewModel.SessionStatus = "历史载入失败；实时字幕仍可继续";
            }
        }

        private void LectureWorkspacePage_Unloaded(object sender, RoutedEventArgs e)
        {
            elapsedTimer.Stop();
            if (settingSubscribed && Translator.Setting != null)
            {
                Translator.Setting.PropertyChanged -= Setting_PropertyChanged;
                settingSubscribed = false;
            }
            if (liveCaptionsConnectionSubscribed)
            {
                Translator.LiveCaptionsConnectionChanged -= OnLiveCaptionsConnectionChanged;
                liveCaptionsConnectionSubscribed = false;
            }
            if (ownerWindow != null)
                ownerWindow.SizeChanged -= OwnerWindow_SizeChanged;
        }

        private void OnLiveCaptionsConnectionChanged(bool connected)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => OnLiveCaptionsConnectionChanged(connected));
                return;
            }

            viewModel.LiveCaptionsStatus = connected
                ? "Live Captions 已就绪"
                : "Live Captions 未连接";
            if (!connected)
            {
                viewModel.SessionStatus = viewModel.CanEndSession
                    ? "系统字幕已关闭；课堂记录保持，可重新连接"
                    : "等待手动重新连接 Windows 实时字幕";
            }
            else if (!viewModel.CanEndSession)
            {
                viewModel.SessionStatus = "等待课程声音";
            }
        }

        private void Setting_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName is nameof(Setting.ApiName) or nameof(Setting.TargetLanguage) or
                    nameof(Setting.Configs) or nameof(Setting.ActiveEngineDisplayName) or
                    nameof(Setting.Summary) or nameof(Setting.SummaryEngineDisplayName))
            {
                Dispatcher.BeginInvoke(RefreshEngineSummary);
            }
        }

        private void RefreshEngineSummary()
        {
            var setting = Translator.Setting;
            EngineText.Text = $"{setting?.ActiveEngineDisplayName ?? "未选择"} · 实时翻译";
            LanguageText.Text = $"English → {setting?.TargetLanguage ?? "zh-CN"}";
            SummaryEngineText.Text = $"总结模型：{setting?.SummaryEngineDisplayName ?? "未配置"}";
        }

        private async void GenerateSummary_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting == null || viewModel.IsSummaryRunning)
                return;

            var sessionSegments = viewModel.Segments
                .Select(segment => segment.Snapshot)
                .Where(segment => segment.CapturedAt >= sessionStartedAt)
                .ToList();
            if (sessionSegments.Count == 0)
            {
                sessionSegments = viewModel.Segments
                    .Select(segment => segment.Snapshot)
                    .ToList();
            }

            viewModel.IsSummaryRunning = true;
            viewModel.SummaryStatus = $"正在使用 {Translator.Setting.SummaryEngineDisplayName} 整理课堂内容…";
            try
            {
                viewModel.SummaryText = await summaryService.GenerateAsync(
                    Translator.Setting.Summary, sessionSegments);
                await LectureSessionTracker.SaveSummaryAsync(viewModel.SummaryText);
                viewModel.SummaryStatus = $"总结完成 · 基于 {sessionSegments.Count} 条字幕";
            }
            catch (Exception exception)
            {
                ProductDiagnostics.Write("workspace.summary-failed", exception);
                viewModel.SummaryStatus = exception.Message;
            }
            finally
            {
                viewModel.IsSummaryRunning = false;
            }
        }

        private void CopySummary_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(viewModel.SummaryText))
                return;

            Clipboard.SetText(viewModel.SummaryText);
            viewModel.SummaryStatus = "课堂总结已复制到剪贴板";
        }

        private void OwnerWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(UpdateViewportHeight, DispatcherPriority.Loaded);
        }

        private void UpdateViewportHeight()
        {
            if (ownerWindow == null || ownerWindow.WorkspaceViewportHeight <= 0)
                return;

            Height = ownerWindow.WorkspaceViewportHeight;
        }

        private void WorkspaceTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string tabName)
                ShowWorkspaceTab(tabName);
        }

        private void ShowWorkspaceTab(string tabName)
        {
            bool showSummary = string.Equals(tabName, "Summary", StringComparison.Ordinal);
            LivePanel.Visibility = showSummary ? Visibility.Collapsed : Visibility.Visible;
            SummaryPanel.Visibility = showSummary ? Visibility.Visible : Visibility.Collapsed;

            LiveTabButton.BorderBrush = showSummary
                ? Brushes.Transparent
                : (Brush)FindResource("LectureActionBrush");
            LiveTabButton.Foreground = showSummary
                ? (Brush)FindResource("LectureTextSecondaryBrush")
                : (Brush)FindResource("LectureTextPrimaryBrush");
            LiveTabButton.FontWeight = showSummary ? FontWeights.Normal : FontWeights.SemiBold;

            SummaryTabButton.BorderBrush = showSummary
                ? (Brush)FindResource("LectureActionBrush")
                : Brushes.Transparent;
            SummaryTabButton.Foreground = showSummary
                ? (Brush)FindResource("LectureTextPrimaryBrush")
                : (Brush)FindResource("LectureTextSecondaryBrush");
            SummaryTabButton.FontWeight = showSummary ? FontWeights.SemiBold : FontWeights.Normal;

            FrameworkElement visiblePanel = showSummary ? SummaryPanel : LivePanel;
            visiblePanel.BeginAnimation(OpacityProperty, null);
            if (Translator.Setting?.Appearance.ReduceMotion == true)
            {
                visiblePanel.Opacity = 1;
            }
            else
            {
                visiblePanel.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(0.78, 1, TimeSpan.FromMilliseconds(180))
                    {
                        EasingFunction = new QuadraticEase
                        {
                            EasingMode = EasingMode.EaseOut
                        }
                    });
            }
        }

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            MorePopup.IsOpen = !MorePopup.IsOpen;
        }

        private void OverlayCaption_Click(object sender, RoutedEventArgs e)
        {
            ownerWindow?.ToggleOverlay();
        }

        private void Caption_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (viewModel.IsDemoRunning)
                return;

            if (e.PropertyName == nameof(Caption.DisplayOriginalCaption))
            {
                string draft = Translator.Caption?.DisplayOriginalCaption ?? string.Empty;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    viewModel.SetDraft(draft);
                    viewModel.SetDraftTranslation(string.Empty);
                    if (!string.IsNullOrWhiteSpace(draft))
                        viewModel.SessionStatus = "正在识别英文";
                }));
            }
            else if (e.PropertyName == nameof(Caption.DisplayTranslatedCaption))
            {
                string partialTranslation =
                    Translator.Caption?.DisplayTranslatedCaption ?? string.Empty;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    viewModel.SetDraftTranslation(partialTranslation);
                    if (!string.IsNullOrWhiteSpace(partialTranslation) &&
                        !TranslationTextPolicy.IsProviderNotice(partialTranslation))
                    {
                        viewModel.SessionStatus = "正在接收译文";
                    }
                }));
            }
        }

        private void OnTranslationEntryLogged(TranslationHistoryChange change)
        {
            if (viewModel.IsDemoRunning)
                return;

            lock (historyProjectionLock)
            {
                if (historyProjectionInitializing)
                {
                    pendingHistoryChanges.Add(change);
                    return;
                }
            }

            Dispatcher.BeginInvoke(new Action(() => AddHistoryEntry(
                change.Entry, change.ReplacedEntryId)));
        }

        private async Task LoadRecentHistoryAsync(bool clearTimeline)
        {
            lock (historyProjectionLock)
            {
                historyProjectionInitializing = true;
                pendingHistoryChanges.Clear();
            }

            bool completed = false;
            try
            {
                long? sessionId = LectureSessionTracker.CurrentSessionId;
                var history = sessionId.HasValue
                    ? await SQLiteHistoryLogger.LoadSessionHistoryAsync(sessionId.Value)
                    : [];
                if (clearTimeline)
                {
                    viewModel.ResetTimeline();
                    loadedEntryIds.Clear();
                    legacySequence = 0;
                }

                foreach (var entry in history.AsEnumerable().Reverse())
                    AddHistoryEntry(entry);

                while (true)
                {
                    TranslationHistoryChange[] pending;
                    lock (historyProjectionLock)
                    {
                        if (pendingHistoryChanges.Count == 0)
                        {
                            historyProjectionInitializing = false;
                            break;
                        }

                        pending = pendingHistoryChanges
                            .OrderBy(change => change.Entry.Id)
                            .ToArray();
                        pendingHistoryChanges.Clear();
                    }

                    foreach (var change in pending)
                        AddHistoryEntry(change.Entry, change.ReplacedEntryId);
                }

                completed = true;
            }
            finally
            {
                if (!completed)
                {
                    lock (historyProjectionLock)
                        historyProjectionInitializing = false;
                }
            }

            if (viewModel.Segments.Count > 0)
                viewModel.SessionStatus = "已恢复最近字幕，等待新内容";
        }

        private void AddHistoryEntry(TranslationHistoryEntry entry, long? replacedEntryId = null)
        {
            if (entry.Id > 0 && loadedEntryIds.Contains(entry.Id))
                return;

            DateTimeOffset capturedAt = DateTimeOffset.Now;
            if (DateTime.TryParse(entry.TimestampFull, out var parsed))
                capturedAt = new DateTimeOffset(parsed);

            SegmentState state;
            string? translatedText;
            if (string.Equals(entry.TranslatedText, "N/A", StringComparison.Ordinal))
            {
                state = SegmentState.Committed;
                translatedText = null;
            }
            else if (string.IsNullOrWhiteSpace(entry.TranslatedText) ||
                     entry.TranslatedText.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase))
            {
                state = SegmentState.TranslationFailed;
                translatedText = null;
            }
            else
            {
                state = SegmentState.Translated;
                translatedText = entry.TranslatedText;
            }

            var segment = new TranscriptSegment(
                Guid.NewGuid(),
                entry.Id > 0 ? entry.Id : ++legacySequence,
                0,
                entry.SourceText,
                translatedText,
                state,
                capturedAt);

            bool replaced = false;
            if (replacedEntryId is > 0)
            {
                loadedEntryIds.Remove(replacedEntryId.Value);
                replaced = viewModel.ReplaceSegmentBySequence(replacedEntryId.Value, segment);
            }

            if (entry.Id > 0)
                loadedEntryIds.Add(entry.Id);
            if (!replaced)
                viewModel.ApplySegment(segment);
            viewModel.SetDraft(string.Empty);
            viewModel.SessionStatus = state == SegmentState.TranslationFailed
                ? "部分句子翻译失败；原文已保留"
                : "正在实时翻译";
        }

        private async void DemoTimeline_Click(object sender, RoutedEventArgs e)
        {
            MorePopup.IsOpen = false;
            if (viewModel.IsDemoRunning)
            {
                demoCancellation?.Cancel();
                return;
            }

            demoTask = RunDemoAsync();
            await demoTask;
        }

        private async Task RunDemoAsync()
        {
            demoCancellation?.Dispose();
            demoCancellation = new CancellationTokenSource();
            var coordinator = new TranscriptCoordinator(new DemoTranslationService());
            coordinator.DraftChanged += draft => Dispatcher.BeginInvoke(
                new Action(() => viewModel.SetDraft(draft)));
            coordinator.SegmentChanged += segment => Dispatcher.BeginInvoke(
                new Action(() => viewModel.ApplySegment(segment)));

            viewModel.ResetTimeline();
            viewModel.IsDemoRunning = true;
            viewModel.CaptureMode = "离线界面演示";
            viewModel.SessionStatus = "正在播放逐句字幕演示";

            try
            {
                await coordinator.RunAsync(DemoCaptionScenario.Create(), demoCancellation.Token);
                viewModel.SessionStatus = "界面演示完成 · 6 条双语字幕";
            }
            catch (OperationCanceledException)
            {
                viewModel.SessionStatus = "界面演示已停止";
            }
            finally
            {
                viewModel.SetDraft(string.Empty);
                viewModel.IsDemoRunning = false;
            }
        }

        private async void OnlineCourse_Click(object sender, RoutedEventArgs e)
        {
            MorePopup.IsOpen = false;
            await RunModeTransitionAsync(EnterOnlineCourseAsync);
        }

        private async void ReconnectLiveCaptions_Click(object sender, RoutedEventArgs e)
        {
            MorePopup.IsOpen = false;
            viewModel.LiveCaptionsStatus = "正在连接 Live Captions";
            viewModel.SessionStatus = "正在重新连接 Windows 实时字幕";

            bool connected = await Translator.ConnectLiveCaptionsAsync(userInitiated: true);
            viewModel.LiveCaptionsStatus = connected
                ? "Live Captions 已就绪"
                : "Live Captions 未连接";
            if (!viewModel.CanEndSession)
            {
                viewModel.SessionStatus = connected
                    ? "等待课程声音"
                    : "重新连接失败，可稍后重试";
            }
            else if (connected)
            {
                viewModel.SessionStatus = "系统字幕已重新连接，正在继续课堂";
            }
        }

        private async Task EnterOnlineCourseAsync()
        {
            await StopDemoAsync();
            await LectureSessionTracker.EndCurrentAsync(viewModel.SummaryText);
            viewModel.CanEndSession = false;
            var liveCaptionsWindow = Translator.Window;
            if (liveCaptionsWindow == null)
            {
                viewModel.LiveCaptionsStatus = "Live Captions 未连接";
                viewModel.SessionStatus = "请先恢复 Windows 实时字幕";
                return;
            }

            if (LiveCaptionsMicrophoneProbe.WasEnabledByCurrentApp)
            {
                await RestoreLiveCaptionsForMicrophoneControlAsync(liveCaptionsWindow);
                var result = await Task.Run(() =>
                    LiveCaptionsMicrophoneProbe.DisableAfterUserAction(liveCaptionsWindow));
                if (result.State == MicrophoneCaptionState.Off)
                {
                    viewModel.CanEndSession = false;
                    LiveCaptionsHandler.HideLiveCaptions(liveCaptionsWindow);
                }
            }

            viewModel.CaptureMode = "在线课程 · 电脑声音";
            viewModel.MicrophoneStatus = "未使用麦克风";
            viewModel.SessionStatus = "正在启动电脑声音捕捉";
            bool captureStarted = await Task.Run(() =>
                LiveCaptionsMicrophoneProbe.EnsureCaptureStartedAfterUserAction(
                    liveCaptionsWindow));
            if (!captureStarted)
            {
                viewModel.LiveCaptionsStatus = "Live Captions 等待确认";
                viewModel.SessionStatus = "请在 Windows 实时字幕中点击继续后重试";
                LiveCaptionsHandler.RestoreLiveCaptions(liveCaptionsWindow);
                return;
            }

            viewModel.LiveCaptionsStatus = "Live Captions 正在工作";
            await BeginLectureSessionAsync(viewModel.CaptureMode);
            viewModel.SessionStatus = "正在监听电脑声音";
        }

        private async void ClassroomMode_Click(object sender, RoutedEventArgs e)
        {
            MorePopup.IsOpen = false;
            await RunModeTransitionAsync(EnterClassroomModeAsync);
        }

        private async Task EnterClassroomModeAsync()
        {
            await StopDemoAsync();
            await LectureSessionTracker.EndCurrentAsync(viewModel.SummaryText);
            viewModel.CanEndSession = false;
            if (Translator.Window == null)
            {
                viewModel.LiveCaptionsStatus = "Live Captions 未连接";
                viewModel.SessionStatus = "请先恢复 Windows 实时字幕";
                return;
            }

            viewModel.CaptureMode = "线下课堂 · 麦克风";
            viewModel.MicrophoneStatus = "正在请求本次麦克风…";
            viewModel.SessionStatus = "正在准备线下课堂";

            await RestoreLiveCaptionsForMicrophoneControlAsync(Translator.Window);
            var result = await Task.Run(() =>
                LiveCaptionsMicrophoneProbe.EnableAfterUserAction(Translator.Window));
            if (result.State == MicrophoneCaptionState.On)
            {
                LiveCaptionsHandler.HideLiveCaptions(Translator.Window);
                viewModel.LiveCaptionsStatus = "Live Captions 正在工作";
                viewModel.MicrophoneStatus = "麦克风已开启";
                viewModel.SessionStatus = "正在监听线下课堂";
                await BeginLectureSessionAsync(viewModel.CaptureMode);
                viewModel.SessionStatus = "正在监听线下课堂";
                return;
            }

            viewModel.MicrophoneStatus = result.State switch
            {
                MicrophoneCaptionState.Blocked => "麦克风被系统隐私设置阻止",
                MicrophoneCaptionState.Off => "麦克风仍为关闭状态",
                _ => "无法自动确认麦克风状态"
            };
            viewModel.SessionStatus = "已打开 Windows 实时字幕，请完成最后一步";
            LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);
        }

        private async Task StopDemoAsync()
        {
            if (!viewModel.IsDemoRunning)
                return;

            demoCancellation?.Cancel();
            if (demoTask != null)
                await demoTask;
        }

        private async void EndLectureSession_Click(object sender, RoutedEventArgs e)
        {
            MorePopup.IsOpen = false;
            await RunModeTransitionAsync(EndLectureSessionAsync);
        }

        private async Task EndLectureSessionAsync()
        {
            if (Translator.Window != null && LiveCaptionsMicrophoneProbe.WasEnabledByCurrentApp)
            {
                await RestoreLiveCaptionsForMicrophoneControlAsync(Translator.Window);
                var result = await Task.Run(() =>
                    LiveCaptionsMicrophoneProbe.DisableAfterUserAction(Translator.Window));
                if (result.State != MicrophoneCaptionState.Off)
                {
                    viewModel.MicrophoneStatus = "未能确认麦克风已停止，请检查系统字幕";
                    LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);
                    return;
                }

                LiveCaptionsHandler.HideLiveCaptions(Translator.Window);
                viewModel.MicrophoneStatus = "本次麦克风已停止";
            }

            await LectureSessionTracker.EndCurrentAsync(viewModel.SummaryText);
            viewModel.CanEndSession = false;
            viewModel.LiveCaptionsStatus = Translator.Window == null
                ? "Live Captions 未连接"
                : "Live Captions 已就绪";
            viewModel.SessionStatus = "课堂已结束，记录已按本次课堂保存";
            viewModel.ElapsedText = "00:00:00";
            if (ownerWindow != null)
                await ownerWindow.RefreshRecentSessionsAsync();
        }

        private async Task BeginLectureSessionAsync(string mode)
        {
            if (Translator.Setting == null)
                return;

            await Translator.SuspendAndResetCaptureAsync();
            LectureSessionEntry session;
            try
            {
                session = await LectureSessionTracker.BeginAsync(
                    mode,
                    Translator.Setting.ActiveEngineDisplayName,
                    Translator.Setting.TargetLanguage);
            }
            finally
            {
                Translator.ResumeCapture();
            }
            sessionStartedAt = session.StartedAt;
            viewModel.CanEndSession = true;
            viewModel.SummaryText = string.Empty;
            viewModel.SummaryStatus = "尚未生成课堂总结";
            await LoadRecentHistoryAsync(clearTimeline: true);
        }

        private static async Task RestoreLiveCaptionsForMicrophoneControlAsync(
            System.Windows.Automation.AutomationElement liveCaptionsWindow)
        {
            LiveCaptionsHandler.RestoreLiveCaptions(liveCaptionsWindow);
            await Task.Delay(400);
        }

        private async Task RunModeTransitionAsync(Func<Task> transition)
        {
            if (!await modeTransitionGate.WaitAsync(0))
                return;

            viewModel.IsModeTransitioning = true;
            try
            {
                await transition();
            }
            catch (Exception exception)
            {
                ProductDiagnostics.Write("workspace.mode-transition-failed", exception);
                viewModel.SessionStatus = "课程模式切换失败，可重试或打开旧版字幕";
            }
            finally
            {
                viewModel.IsModeTransitioning = false;
                modeTransitionGate.Release();
            }
        }

        private void PauseTranslation_Click(object sender, RoutedEventArgs e)
        {
            Translator.LogOnlyFlag = !Translator.LogOnlyFlag;
            PauseButton.Content = Translator.LogOnlyFlag ? "继续" : "暂停";
            viewModel.SessionStatus = Translator.LogOnlyFlag
                ? "翻译已暂停；原文仍会保留"
                : "正在实时翻译";
            Translator.ClearContexts();
        }

        private void ReturnToLive_Click(object sender, RoutedEventArgs e)
        {
            MorePopup.IsOpen = false;
            Timeline.ReturnToLive();
        }

        private void OpenLegacy_Click(object sender, RoutedEventArgs e)
        {
            MorePopup.IsOpen = false;
            (Application.Current.MainWindow as MainWindow)?.NavigateTo(typeof(CaptionPage));
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            MorePopup.IsOpen = false;
            (Application.Current.MainWindow as MainWindow)?.NavigateTo(typeof(SettingPage));
        }

        private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.End && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                Timeline.ReturnToLive();
                e.Handled = true;
            }
        }
    }
}
