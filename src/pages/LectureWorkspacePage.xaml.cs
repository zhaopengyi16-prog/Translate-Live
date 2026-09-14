using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.services;
using LiveCaptionsTranslator.services.recognition;
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
        private readonly Dictionary<long, Guid> loadedSegmentIds = [];
        private readonly List<TranslationHistoryChange> pendingHistoryChanges = [];
        private readonly object historyProjectionLock = new();
        private readonly SemaphoreSlim modeTransitionGate = new(1, 1);
        private readonly LectureSummaryService summaryService = new();
        private CancellationTokenSource? demoCancellation;
        private CancellationTokenSource? modeTransitionCancellation;
        private Task? demoTask;
        private DateTimeOffset sessionStartedAt = DateTimeOffset.Now;
        private long legacySequence;
        private long workspaceGeneration;
        private long? displayedSessionId;
        private bool initialized;
        private bool settingSubscribed;
        private bool liveCaptionsConnectionSubscribed;
        private bool localAsrStatusSubscribed;
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
            if (!localAsrStatusSubscribed)
            {
                Translator.LocalAsrStatusChanged += OnLocalAsrStatusChanged;
                localAsrStatusSubscribed = true;
            }
            RefreshEngineSummary();

            if (initialized)
                return;

            initialized = true;
            Translator.TranslationEntryLogged += OnTranslationEntryLogged;
            Translator.TranscriptSegmentChanged += OnTranscriptSegmentChanged;
            Translator.TranscriptDraftChanged += OnTranscriptDraftChanged;

            bool localSource = Translator.Setting?.CaptionSource ==
                CaptionSourceKind.LocalSherpaOnnx;
            viewModel.LiveCaptionsStatus = localSource
                ? "本地 ASR 已选择"
                : Translator.Window == null
                    ? "Live Captions 未连接"
                    : "Live Captions 已就绪";
            viewModel.SessionStatus = localSource
                ? "开始课堂时将加载本地英文识别模型"
                : Translator.Window == null
                    ? "等待 Windows 实时字幕恢复"
                    : "等待课程声音";
            try
            {
                await LoadRecentHistoryAsync(clearTimeline: true);
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
            modeTransitionCancellation?.Cancel();
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
            if (localAsrStatusSubscribed)
            {
                Translator.LocalAsrStatusChanged -= OnLocalAsrStatusChanged;
                localAsrStatusSubscribed = false;
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

            if (Translator.Setting?.CaptionSource == CaptionSourceKind.LocalSherpaOnnx)
                return;

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

        private void OnLocalAsrStatusChanged(string status)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => OnLocalAsrStatusChanged(status));
                return;
            }

            viewModel.LiveCaptionsStatus = status == "running"
                ? "本地 ASR 正在工作"
                : "本地 ASR 需要检查";
            if (status == "audio-queue-overflow")
                viewModel.SessionStatus = "本机识别暂时跟不上音频，已跳过过期缓冲";
            else if (status != "running")
                viewModel.SessionStatus = "本地 ASR 出现问题；课堂原文仍按已接收内容保存";
        }

        private void Setting_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName is nameof(Setting.ApiName) or nameof(Setting.TargetLanguage) or
                    nameof(Setting.CaptionSource) or
                    nameof(Setting.Configs) or nameof(Setting.ActiveEngineDisplayName) or
                    nameof(Setting.Summary) or nameof(Setting.SummaryEngineDisplayName))
            {
                Dispatcher.BeginInvoke(RefreshEngineSummary);
            }
        }

        private void RefreshEngineSummary()
        {
            var setting = Translator.Setting;
            EngineText.Text = $"{setting?.ActiveEngineDisplayName ?? "未选择"} · 实时翻译 · " +
                $"{setting?.CaptionSourceDisplayName ?? "Windows Live Captions"}";
            LanguageText.Text = $"English → {setting?.TargetLanguage ?? "zh-CN"}";
            SummaryEngineText.Text = $"总结模型：{setting?.SummaryEngineDisplayName ?? "未配置"}";
            if (!viewModel.CanEndSession &&
                setting?.CaptionSource == CaptionSourceKind.LocalSherpaOnnx)
            {
                viewModel.LiveCaptionsStatus = "本地 ASR 已选择";
                viewModel.SessionStatus = "开始课堂时将加载本地英文识别模型";
            }
        }

        private async void GenerateSummary_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting == null || viewModel.IsSummaryRunning)
                return;

            long generation = workspaceGeneration;
            long? requestedSessionId = displayedSessionId;
            bool isDemo = viewModel.IsDemoRunning;
            var sessionSegments = viewModel.Segments
                .Select(segment => segment.Snapshot)
                .ToList();
            int eligibleCount = sessionSegments.Count(segment =>
                !string.IsNullOrWhiteSpace(segment.SourceText));
            int includedCount = Math.Min(eligibleCount, LectureSummaryService.MaximumTranscriptSegments);
            string summaryScope = eligibleCount > includedCount
                ? $"最近 {includedCount} 条字幕（本次课堂共 {eligibleCount} 条）"
                : $"{includedCount} 条字幕";

            viewModel.IsSummaryRunning = true;
            viewModel.SummaryStatus = $"正在使用 {Translator.Setting.SummaryEngineDisplayName} 整理{summaryScope}…";
            try
            {
                string summary = await summaryService.GenerateAsync(
                    Translator.Setting.Summary, sessionSegments);
                if (requestedSessionId.HasValue && !isDemo)
                    await SQLiteHistoryLogger.SaveSessionSummaryAsync(requestedSessionId.Value, summary);
                if (generation == workspaceGeneration && requestedSessionId == displayedSessionId)
                {
                    viewModel.SummaryText = summary;
                    viewModel.SummaryStatus = $"总结完成 · 基于{summaryScope}";
                }
            }
            catch (Exception exception)
            {
                ProductDiagnostics.Write("workspace.summary-failed", exception);
                if (generation == workspaceGeneration && requestedSessionId == displayedSessionId)
                    viewModel.SummaryStatus = "课堂总结生成失败，请检查模型配置或网络后重试";
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

        private void OnTranscriptDraftChanged(TranscriptSegment? draft)
        {
            // Null carries no sentence identity. A delayed clear must not erase
            // a newer observation; committed events clear the matching draft.
            if (viewModel.IsDemoRunning || draft == null)
                return;

            long epoch = draft.CaptureEpoch;
            long? sessionId = draft.SessionId;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!CanApplyLiveEvent(sessionId, epoch))
                    return;
                viewModel.SetDraft(draft);
                if (!string.IsNullOrWhiteSpace(draft.TranslatedText))
                {
                    viewModel.SetDraftTranslation(
                        draft.Id,
                        draft.Revision,
                        draft.TranslatedText);
                    viewModel.SessionStatus = "正在接收译文";
                }
                else if (!string.IsNullOrWhiteSpace(draft.SourceText))
                {
                    viewModel.SessionStatus = "正在识别英文";
                }
            }));
        }

        private void OnTranscriptSegmentChanged(TranscriptSegment segment)
        {
            if (viewModel.IsDemoRunning)
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!CanApplyLiveEvent(segment.SessionId, segment.CaptureEpoch))
                    return;
                viewModel.ApplySegment(segment);
                if (segment.State != SegmentState.Draft)
                    viewModel.ClearDraft(segment.Id, segment.Revision);
                viewModel.SessionStatus = segment.State == SegmentState.TranslationFailed
                    ? "部分句子翻译失败；原文已保留"
                    : "正在实时翻译";
            }));
        }

        private void OnTranslationEntryLogged(TranslationHistoryChange change)
        {
            if (viewModel.IsDemoRunning)
                return;
            if (change.SegmentId.HasValue && !change.IsFinal)
                return;

            lock (historyProjectionLock)
            {
                if (historyProjectionInitializing)
                {
                    pendingHistoryChanges.Add(change);
                    return;
                }
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (viewModel.IsDemoRunning)
                    return;
                AddHistoryEntry(
                    change.Entry,
                    change.ReplacedEntryId,
                    change.SegmentId,
                    change.Sequence,
                    change.Revision,
                    change.CaptureEpoch,
                    change.IsIncomplete);
            }));
        }

        private bool CanApplyLiveEvent(long? sessionId, long epoch) =>
            !viewModel.IsDemoRunning &&
            sessionId.HasValue &&
            sessionId == displayedSessionId &&
            sessionId == LectureSessionTracker.CurrentSessionId &&
            epoch == Translator.CaptureEpoch;

        private async Task LoadRecentHistoryAsync(bool clearTimeline)
        {
            long generation = ++workspaceGeneration;
            long? sessionId = LectureSessionTracker.CurrentSessionId;
            displayedSessionId = sessionId;
            lock (historyProjectionLock)
            {
                historyProjectionInitializing = true;
                pendingHistoryChanges.Clear();
            }

            bool completed = false;
            try
            {
                if (clearTimeline)
                {
                    viewModel.ResetTimeline();
                    loadedEntryIds.Clear();
                    loadedSegmentIds.Clear();
                    legacySequence = 0;
                }

                var history = sessionId.HasValue
                    ? await SQLiteHistoryLogger.LoadSessionHistoryAsync(sessionId.Value)
                    : [];
                if (generation != workspaceGeneration || displayedSessionId != sessionId)
                    return;

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
                        AddHistoryEntry(
                            change.Entry,
                            change.ReplacedEntryId,
                            change.SegmentId,
                            change.Sequence,
                            change.Revision,
                            change.CaptureEpoch,
                            change.IsIncomplete);
                }

                completed = true;
            }
            finally
            {
                if (!completed && generation == workspaceGeneration)
                {
                    lock (historyProjectionLock)
                        historyProjectionInitializing = false;
                }
            }

            if (viewModel.Segments.Count > 0)
                viewModel.SessionStatus = "已恢复最近字幕，等待新内容";
        }

        private void AddHistoryEntry(
            TranslationHistoryEntry entry,
            long? replacedEntryId = null,
            Guid? stableSegmentId = null,
            long? stableSequence = null,
            int revision = 0,
            long captureEpoch = 0,
            bool isIncomplete = false)
        {
            if (!displayedSessionId.HasValue || entry.SessionId != displayedSessionId)
                return;
            if (entry.Id > 0 && loadedEntryIds.Contains(entry.Id) && !stableSegmentId.HasValue)
                return;

            DateTimeOffset capturedAt = DateTimeOffset.Now;
            if (DateTime.TryParse(entry.TimestampFull, out var parsed))
                capturedAt = new DateTimeOffset(parsed);

            SegmentState state;
            string? translatedText;
            if (string.IsNullOrWhiteSpace(entry.TranslatedText) ||
                string.Equals(entry.TranslatedText, "N/A", StringComparison.Ordinal))
            {
                state = SegmentState.Committed;
                translatedText = null;
            }
            else if (entry.TranslatedText.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase))
            {
                state = SegmentState.TranslationFailed;
                translatedText = null;
            }
            else
            {
                state = SegmentState.Translated;
                translatedText = entry.TranslatedText;
            }

            Guid? projectedSegmentId = entry.Id > 0 &&
                loadedSegmentIds.TryGetValue(entry.Id, out Guid projected)
                ? projected
                : null;
            Guid segmentId = stableSegmentId ??
                projectedSegmentId ??
                ResolveHistorySegmentId(entry.Id);
            var segment = new TranscriptSegment(
                segmentId,
                stableSequence ?? (entry.Id > 0 ? entry.Id : ++legacySequence),
                revision,
                entry.SourceText,
                translatedText,
                state,
                capturedAt,
                SessionId: entry.SessionId,
                CaptureEpoch: captureEpoch,
                IsIncomplete: isIncomplete);

            bool replaced = false;
            if (replacedEntryId is > 0)
            {
                loadedEntryIds.Remove(replacedEntryId.Value);
                replaced = viewModel.ReplaceSegmentBySequence(replacedEntryId.Value, segment);
            }

            bool rebound = false;
            if (!replaced &&
                projectedSegmentId.HasValue &&
                stableSegmentId.HasValue &&
                projectedSegmentId.Value != stableSegmentId.Value)
            {
                rebound = viewModel.RebindSegmentIdentity(
                    projectedSegmentId.Value,
                    segment);
            }

            if (entry.Id > 0)
            {
                loadedEntryIds.Add(entry.Id);
                loadedSegmentIds[entry.Id] = segment.Id;
            }
            if (!replaced && !rebound)
                viewModel.ApplySegment(segment, updateLive: false);
            if (stableSegmentId.HasValue)
                viewModel.ClearDraft(stableSegmentId.Value, revision);
            viewModel.SessionStatus = viewModel.CanEndSession
                ? state == SegmentState.TranslationFailed
                    ? "部分句子翻译失败；原文已保留"
                    : "正在实时翻译"
                : "本次课堂已保存，可查看记录或开始新课堂";
        }

        private Guid ResolveHistorySegmentId(long entryId)
        {
            if (entryId > 0 && loadedSegmentIds.TryGetValue(entryId, out Guid existing))
                return existing;

            return Guid.NewGuid();
        }

        private async void DemoTimeline_Click(object sender, RoutedEventArgs e)
        {
            MorePopup.IsOpen = false;
            if (viewModel.IsDemoRunning)
            {
                demoCancellation?.Cancel();
                return;
            }

            if (LectureSessionTracker.CurrentSessionId.HasValue)
            {
                viewModel.SessionStatus = "请先结束当前课堂，再播放界面演示";
                return;
            }

            demoTask = RunDemoAsync();
            await demoTask;
        }

        private async Task RunDemoAsync()
        {
            demoCancellation?.Dispose();
            demoCancellation = new CancellationTokenSource();
            long generation = ++workspaceGeneration;
            var coordinator = new TranscriptCoordinator(new DemoTranslationService());
            coordinator.DraftChanged += draft => Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (viewModel.IsDemoRunning && generation == workspaceGeneration)
                        viewModel.SetDraft(draft);
                }));
            coordinator.SegmentChanged += segment => Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (viewModel.IsDemoRunning && generation == workspaceGeneration)
                        viewModel.ApplySegment(segment);
                }));

            displayedSessionId = null;
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
            await RunModeTransitionAsync(token => EnterCaptureModeAsync(
                microphoneMode: false,
                token));
        }

        private async void ReconnectLiveCaptions_Click(object sender, RoutedEventArgs e)
        {
            MorePopup.IsOpen = false;
            await RunModeTransitionAsync(ReconnectLiveCaptionsAsync);
        }

        private async Task ReconnectLiveCaptionsAsync(CancellationToken token)
        {
            viewModel.LiveCaptionsStatus = "正在连接 Live Captions";
            viewModel.SessionStatus = "正在重新连接 Windows 实时字幕";

            bool connected = await Translator.ConnectLiveCaptionsAsync(
                userInitiated: true,
                token);
            token.ThrowIfCancellationRequested();
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

        private async void ClassroomMode_Click(object sender, RoutedEventArgs e)
        {
            MorePopup.IsOpen = false;
            await RunModeTransitionAsync(token => EnterCaptureModeAsync(
                microphoneMode: true,
                token));
        }

        private async Task EnterCaptureModeAsync(
            bool microphoneMode,
            CancellationToken token)
        {
            await StopDemoAsync();
            token.ThrowIfCancellationRequested();
            if (Translator.Setting == null)
            {
                viewModel.SessionStatus = "设置尚未加载，请稍后重试";
                return;
            }

            if (Translator.Setting.CaptionSource == CaptionSourceKind.LocalSherpaOnnx)
            {
                await EnterLocalAsrCaptureModeAsync(microphoneMode, token);
                return;
            }

            var liveCaptionsWindow = Translator.Window;
            if (liveCaptionsWindow == null)
            {
                viewModel.LiveCaptionsStatus = "正在连接 Live Captions";
                bool connected = await Translator.ConnectLiveCaptionsAsync(
                    userInitiated: true,
                    token);
                token.ThrowIfCancellationRequested();
                liveCaptionsWindow = connected ? Translator.Window : null;
            }
            if (liveCaptionsWindow == null)
            {
                viewModel.LiveCaptionsStatus = "Live Captions 未连接";
                viewModel.SessionStatus = "请先恢复 Windows 实时字幕";
                return;
            }

            string mode = microphoneMode
                ? "线下课堂 · 麦克风"
                : "在线课程 · 电脑声音";
            string engine = Translator.Setting.ActiveEngineDisplayName;
            string targetLanguage = Translator.Setting.TargetLanguage;
            viewModel.CaptureMode = mode;
            viewModel.MicrophoneStatus = microphoneMode
                ? "正在请求本次麦克风…"
                : "未使用麦克风";
            viewModel.SessionStatus = microphoneMode
                ? "正在准备线下课堂"
                : "正在启动电脑声音捕捉";

            var operations = new CaptureStartupOperations
            {
                EndPreviousSessionAsync = async cancellationToken =>
                {
                    await Translator.StopCaptureAndFlushAsync(cancellationToken);
                    await LectureSessionTracker.EndCurrentAsync(
                        viewModel.SummaryText,
                        cancellationToken);
                    viewModel.CanEndSession = false;
                },
                PrepareCaptionSourceAsync = async cancellationToken =>
                {
                    await RestoreLiveCaptionsForMicrophoneControlAsync(
                        liveCaptionsWindow,
                        cancellationToken);
                    bool prepared = await Task.Run(() =>
                        LiveCaptionsMicrophoneProbe.EnsureCaptureStartedAfterUserAction(
                            liveCaptionsWindow,
                            cancellationToken));
                    return new CaptureStepResult(
                        prepared,
                        prepared ? null : "livecaptions-preparation-timeout");
                },
                ReadBaselineAsync = cancellationToken => ReadCaptionSnapshotAsync(
                    liveCaptionsWindow,
                    refreshNode: true,
                    cancellationToken),
                PrepareCaptureAsync = (baseline, cancellationToken) =>
                    Translator.PrepareSuspendedCaptureFromBaselineAsync(
                        liveCaptionsWindow,
                        baseline,
                        cancellationToken),
                CreateSessionAsync = async cancellationToken =>
                {
                    LectureSessionEntry session = await LectureSessionTracker.BeginAsync(
                        mode,
                        engine,
                        targetLanguage,
                        cancellationToken);
                    BindNewCaptureSession(session);
                    return session;
                },
                ActivateInputAsync = cancellationToken => ActivateCaptureInputAsync(
                    liveCaptionsWindow,
                    microphoneMode,
                    cancellationToken),
                RebindCaptionSourceAsync = cancellationToken => ReadCaptionSnapshotAsync(
                    liveCaptionsWindow,
                    refreshNode: true,
                    cancellationToken),
                ResumeCapture = (preparation, sessionId) =>
                    Translator.ResumePreparedCapture(
                        preparation,
                        sessionId,
                        liveCaptionsWindow),
                AbortAsync = (preparation, sessionId, inputChanged, cancellationToken) =>
                    AbortCaptureStartupAsync(
                        liveCaptionsWindow,
                        microphoneMode,
                        preparation,
                        sessionId,
                        inputChanged,
                        cancellationToken),
                ReportStage = (stage, state, epoch, duration) =>
                    ProductDiagnostics.WriteCaptureStartup(
                        stage.ToString(),
                        state,
                        epoch,
                        duration)
            };

            CaptureStartupResult result = await CaptureStartupSequence.RunAsync(
                operations,
                token);
            if (!result.IsStarted)
            {
                if (result.Stage == CaptureStartupStage.Cancelled)
                    return;

                viewModel.CanEndSession = false;
                viewModel.LiveCaptionsStatus = result.Stage is
                    CaptureStartupStage.PreparingCaptionSource or
                    CaptureStartupStage.ReadingBaseline or
                    CaptureStartupStage.RebindingCaptionSource
                        ? "Live Captions 等待确认"
                        : "Live Captions 已就绪";
                viewModel.MicrophoneStatus = microphoneMode
                    ? result.Stage == CaptureStartupStage.ActivatingInput
                        ? "麦克风未能开启，可重试"
                        : "麦克风启动未完成"
                    : "未使用麦克风";
                viewModel.SessionStatus = DescribeCaptureStartupFailure(result);
                ProductDiagnostics.Write(
                    $"capture.startup.failed.{result.Stage}",
                    result.Exception);
                try
                {
                    LiveCaptionsHandler.RestoreLiveCaptions(liveCaptionsWindow);
                }
                catch (Exception exception) when (
                    exception is System.Windows.Automation.ElementNotAvailableException or
                        InvalidOperationException)
                {
                    ProductDiagnostics.Write(
                        "capture.startup.restore-window-failed",
                        exception);
                }
                return;
            }

            token.ThrowIfCancellationRequested();
            viewModel.CanEndSession = true;
            viewModel.LiveCaptionsStatus = "Live Captions 正在工作";
            viewModel.MicrophoneStatus = microphoneMode
                ? "麦克风已开启"
                : "未使用麦克风";
            viewModel.SessionStatus = microphoneMode
                ? "正在监听线下课堂"
                : "正在监听电脑声音";
            LiveCaptionsHandler.HideLiveCaptions(liveCaptionsWindow);
        }

        private async Task EnterLocalAsrCaptureModeAsync(
            bool microphoneMode,
            CancellationToken token)
        {
            Setting setting = Translator.Setting
                ?? throw new InvalidOperationException("Settings are not loaded.");
            string mode = microphoneMode
                ? "线下课堂 · 麦克风 · 本地 ASR"
                : "在线课程 · 电脑声音 · 本地 ASR";
            viewModel.CaptureMode = mode;
            viewModel.MicrophoneStatus = microphoneMode
                ? "正在打开本地麦克风…"
                : "未使用麦克风";
            viewModel.LiveCaptionsStatus = "正在加载本地 ASR";
            viewModel.SessionStatus = "正在准备本地英文识别模型";

            await Translator.StopCaptureAndFlushAsync(token);
            await LectureSessionTracker.EndCurrentAsync(
                viewModel.SummaryText,
                token);
            viewModel.CanEndSession = false;
            long epoch = await Translator.PrepareLocalCaptureEpochAsync(token);
            LectureSessionEntry? session = null;

            try
            {
                session = await LectureSessionTracker.BeginAsync(
                    mode,
                    setting.ActiveEngineDisplayName,
                    setting.TargetLanguage,
                    token);
                BindNewCaptureSession(session);
                await Translator.StartLocalAsrAsync(
                    epoch,
                    session.Id,
                    microphoneMode,
                    setting.LocalAsrModelDirectory,
                    token);
                token.ThrowIfCancellationRequested();

                viewModel.CanEndSession = true;
                viewModel.LiveCaptionsStatus = "本地 ASR 正在工作";
                viewModel.MicrophoneStatus = microphoneMode
                    ? "本地麦克风已开启"
                    : "正在捕捉电脑声音";
                viewModel.SessionStatus = microphoneMode
                    ? "正在本机识别线下课堂"
                    : "正在本机识别电脑声音";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                await AbortLocalAsrStartupAsync(epoch, session);
                throw;
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException)
            {
                await AbortLocalAsrStartupAsync(epoch, session);
                ProductDiagnostics.Write(
                    $"local-asr.startup-failed.{exception.GetType().Name}");
                viewModel.LiveCaptionsStatus = "本地 ASR 启动失败";
                viewModel.MicrophoneStatus = microphoneMode
                    ? "麦克风未开启"
                    : "电脑声音未开始捕捉";
                viewModel.SessionStatus = exception is LocalAsrModelValidationException or
                    FileNotFoundException
                        ? "本地模型文件不完整，请在设置中检查模型目录"
                        : "本地识别启动失败，可切回 Windows Live Captions";
            }
        }

        private async Task AbortLocalAsrStartupAsync(
            long epoch,
            LectureSessionEntry? session)
        {
            Translator.SuspendLocalCapture(epoch);
            try
            {
                using var stopTimeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(5));
                await Translator.StopCaptureAndFlushAsync(stopTimeout.Token);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or TimeoutException)
            {
                ProductDiagnostics.Write("local-asr.startup-stop-timeout", exception);
            }

            if (session == null)
                return;
            try
            {
                using var sessionTimeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(5));
                await LectureSessionTracker.AbortCurrentIfEmptyAsync(
                    session.Id,
                    sessionTimeout.Token);
            }
            finally
            {
                UnbindCaptureSession(session.Id);
            }
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

        private async Task EndLectureSessionAsync(CancellationToken token)
        {
            if (Translator.ActiveCaptionSource == CaptionSourceKind.WindowsLiveCaptions &&
                Translator.Window != null &&
                LiveCaptionsMicrophoneProbe.WasEnabledByCurrentApp)
            {
                await RestoreLiveCaptionsForMicrophoneControlAsync(
                    Translator.Window,
                    token);
                var result = await Task.Run(() =>
                    LiveCaptionsMicrophoneProbe.DisableAfterUserAction(Translator.Window));
                token.ThrowIfCancellationRequested();
                if (result.State != MicrophoneCaptionState.Off)
                {
                    viewModel.MicrophoneStatus = "未能确认麦克风已停止，请检查系统字幕";
                    LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);
                    return;
                }

                LiveCaptionsHandler.HideLiveCaptions(Translator.Window);
                viewModel.MicrophoneStatus = "本次麦克风已停止";
            }

            await Translator.StopCaptureAndFlushAsync(token);
            await LectureSessionTracker.EndCurrentAsync(viewModel.SummaryText, token);
            token.ThrowIfCancellationRequested();
            viewModel.CanEndSession = false;
            viewModel.LiveCaptionsStatus =
                Translator.Setting?.CaptionSource == CaptionSourceKind.LocalSherpaOnnx
                    ? "本地 ASR 已选择"
                    : Translator.Window == null
                        ? "Live Captions 未连接"
                        : "Live Captions 已就绪";
            viewModel.SessionStatus = "课堂已结束，记录已按本次课堂保存";
            viewModel.ElapsedText = "00:00:00";
            if (ownerWindow != null)
                await ownerWindow.RefreshRecentSessionsAsync();
        }

        private static async Task<CaptionSourceSnapshot> ReadCaptionSnapshotAsync(
            System.Windows.Automation.AutomationElement liveCaptionsWindow,
            bool refreshNode,
            CancellationToken token)
        {
            LiveCaptionsSnapshotReadResult result = await Task.Run(() =>
                LiveCaptionsHandler.ReadCaptionSnapshot(
                    liveCaptionsWindow,
                    refreshNode,
                    TimeSpan.FromSeconds(3),
                    token));
            return result.IsReadable
                ? CaptionSourceSnapshot.Readable(result.Text)
                : CaptionSourceSnapshot.Unavailable(
                    result.ErrorCode ?? "captions-node-unavailable");
        }

        private static async Task<CaptureInputActivation> ActivateCaptureInputAsync(
            System.Windows.Automation.AutomationElement liveCaptionsWindow,
            bool microphoneMode,
            CancellationToken token)
        {
            if (!microphoneMode && !LiveCaptionsMicrophoneProbe.WasEnabledByCurrentApp)
                return new(true, false);

            MicrophoneProbeResult result = await Task.Run(() => microphoneMode
                ? LiveCaptionsMicrophoneProbe.EnableAfterUserAction(liveCaptionsWindow)
                : LiveCaptionsMicrophoneProbe.DisableAfterUserAction(liveCaptionsWindow));
            bool active = microphoneMode
                ? result.State == MicrophoneCaptionState.On
                : result.State == MicrophoneCaptionState.Off;
            return new CaptureInputActivation(
                active,
                microphoneMode && result.ChangedByRequest,
                result.ErrorCode);
        }

        private async Task AbortCaptureStartupAsync(
            System.Windows.Automation.AutomationElement liveCaptionsWindow,
            bool microphoneMode,
            PreparedCaptureSession? preparation,
            long? sessionId,
            bool inputChangedByRequest,
            CancellationToken token)
        {
            Translator.CancelPreparedCapture(preparation);
            if (microphoneMode && inputChangedByRequest &&
                LiveCaptionsMicrophoneProbe.WasEnabledByCurrentApp)
            {
                await RestoreLiveCaptionsForMicrophoneControlAsync(
                    liveCaptionsWindow,
                    token);
                await Task.Run(() =>
                    LiveCaptionsMicrophoneProbe.DisableAfterUserAction(
                        liveCaptionsWindow));
            }

            if (sessionId.HasValue)
            {
                EmptySessionAbortResult abortResult =
                    await LectureSessionTracker.AbortCurrentIfEmptyAsync(
                        sessionId.Value,
                        token);
                ProductDiagnostics.WriteCaptureStartup(
                    CaptureStartupStage.Cancelled.ToString(),
                    $"session-{abortResult}",
                    preparation?.CaptureEpoch,
                    0);
                UnbindCaptureSession(sessionId.Value);
            }
        }

        private void BindNewCaptureSession(LectureSessionEntry session)
        {
            workspaceGeneration++;
            displayedSessionId = session.Id;
            sessionStartedAt = session.StartedAt;
            loadedEntryIds.Clear();
            loadedSegmentIds.Clear();
            legacySequence = 0;
            lock (historyProjectionLock)
            {
                historyProjectionInitializing = false;
                pendingHistoryChanges.Clear();
            }
            viewModel.ResetTimeline();
            viewModel.SummaryText = string.Empty;
            viewModel.SummaryStatus = "尚未生成课堂总结";
        }

        private void UnbindCaptureSession(long sessionId)
        {
            if (displayedSessionId != sessionId)
                return;

            workspaceGeneration++;
            displayedSessionId = null;
            loadedEntryIds.Clear();
            loadedSegmentIds.Clear();
            viewModel.ResetTimeline();
        }

        private static string DescribeCaptureStartupFailure(CaptureStartupResult result)
        {
            string message = result.Stage switch
            {
                CaptureStartupStage.PreparingCaptionSource =>
                    "Windows 实时字幕尚未准备好，请完成首次语言确认后重试",
                CaptureStartupStage.ReadingBaseline =>
                    "无法读取开麦前的字幕窗口，未使用空基线，请重新连接后重试",
                CaptureStartupStage.CreatingSession =>
                    "无法创建本次课堂，现有课堂数据未被清除",
                CaptureStartupStage.ActivatingInput =>
                    "麦克风未成功开启，请检查系统麦克风状态后重试",
                CaptureStartupStage.RebindingCaptionSource =>
                    "麦克风已切换，但字幕节点尚不可读取，请重试",
                CaptureStartupStage.ResumingCapture =>
                    "本次启动已被新的课堂操作替代，请重试",
                _ => "课堂启动失败，可安全重试"
            };
            return result.RollbackSucceeded
                ? message
                : $"{message}；空课堂清理未完成，请在课堂回顾中检查";
        }

        private static async Task RestoreLiveCaptionsForMicrophoneControlAsync(
            System.Windows.Automation.AutomationElement liveCaptionsWindow,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            LiveCaptionsHandler.RestoreLiveCaptions(liveCaptionsWindow);
            await Task.Delay(400, token);
        }

        private async Task RunModeTransitionAsync(
            Func<CancellationToken, Task> transition)
        {
            var request = new CancellationTokenSource();
            CancellationTokenSource? previous = Interlocked.Exchange(
                ref modeTransitionCancellation,
                request);
            previous?.Cancel();
            bool entered = false;

            try
            {
                await modeTransitionGate.WaitAsync(request.Token);
                entered = true;
                if (!ReferenceEquals(modeTransitionCancellation, request))
                    return;

                viewModel.IsModeTransitioning = true;
                await transition(request.Token);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ProductDiagnostics.Write("workspace.mode-transition-failed", exception);
                viewModel.SessionStatus = "课程模式切换失败，可重试或打开旧版字幕";
            }
            finally
            {
                if (entered)
                    modeTransitionGate.Release();
                if (ReferenceEquals(modeTransitionCancellation, request))
                {
                    modeTransitionCancellation = null;
                    viewModel.IsModeTransitioning = false;
                }
                request.Dispose();
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
