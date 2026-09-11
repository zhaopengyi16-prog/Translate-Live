using Microsoft.Win32;

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using Wpf.Ui.Controls;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.services;
using LiveCaptionsTranslator.utils;

using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace LiveCaptionsTranslator
{
    public partial class HistoryPage : Page
    {
        public const int MIN_HEIGHT = 620;

        private string searchText = string.Empty;
        private bool isLoadingSessions;
        private bool refreshRequested;
        private bool isPageActive;
        private long? pendingPreferredSessionId;
        private bool pendingPreserveSelection = true;
        private int sessionDataVersion;
        private CancellationTokenSource pageCancellation = new();
        private CancellationTokenSource? selectionLoadCancellation;

        public HistoryPage()
        {
            InitializeComponent();
            Loaded += HistoryPage_Loaded;
            Unloaded += HistoryPage_Unloaded;
        }

        private async void HistoryPage_Loaded(object sender, RoutedEventArgs e)
        {
            pageCancellation.Dispose();
            pageCancellation = new CancellationTokenSource();
            isPageActive = true;
            (App.Current.MainWindow as MainWindow)?.EnsureWorkspaceSize();
            Translator.TranslationLogged -= OnTranslationLogged;
            Translator.TranslationLogged += OnTranslationLogged;
            await LoadSessionsAsync();
        }

        private void HistoryPage_Unloaded(object sender, RoutedEventArgs e)
        {
            isPageActive = false;
            Translator.TranslationLogged -= OnTranslationLogged;
            pageCancellation.Cancel();
            selectionLoadCancellation?.Cancel();
            selectionLoadCancellation?.Dispose();
            selectionLoadCancellation = null;
            SessionList.ItemsSource = null;
            HistoryDataGrid.ItemsSource = null;
        }

        private async void OnTranslationLogged()
        {
            if (pageCancellation.IsCancellationRequested)
                return;

            await Dispatcher.InvokeAsync(() => LoadSessionsAsync()).Task.Unwrap();
        }

        private async Task LoadSessionsAsync(
            long? preferredSessionId = null,
            bool preserveCurrentSelection = true)
        {
            if (isLoadingSessions)
            {
                refreshRequested = true;
                if (preferredSessionId.HasValue || !preserveCurrentSelection)
                {
                    pendingPreferredSessionId = preferredSessionId;
                    pendingPreserveSelection = preserveCurrentSelection;
                }
                return;
            }

            isLoadingSessions = true;
            try
            {
                int requestedVersion = sessionDataVersion;
                long? selectedId = preferredSessionId ??
                    (preserveCurrentSelection
                        ? (SessionList.SelectedItem as LectureSessionEntry)?.Id
                        : null);
                var sessions = await SQLiteHistoryLogger.LoadSessionsAsync(
                    searchText, pageCancellation.Token);
                if (pageCancellation.IsCancellationRequested)
                    return;
                if (requestedVersion != sessionDataVersion)
                {
                    refreshRequested = true;
                }
                else
                {
                    SessionList.ItemsSource = sessions;
                    if (sessions.Count == 0)
                    {
                        SessionList.SelectedItem = null;
                        ClearSelectionDisplay();
                    }
                    else
                    {
                        SessionList.SelectedItem =
                            sessions.FirstOrDefault(item => item.Id == selectedId)
                            ?? sessions[0];
                    }

                }
            }
            catch (OperationCanceledException) when (pageCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ProductDiagnostics.Write("history.sessions-load-failed", exception);
                SnackbarHost.Show(
                    "课堂记录加载失败",
                    exception.Message,
                    SnackbarType.Error,
                    timeout: 3);
            }
            finally
            {
                isLoadingSessions = false;
            }

            if (refreshRequested && !pageCancellation.IsCancellationRequested)
            {
                refreshRequested = false;
                long? preferred = pendingPreferredSessionId;
                bool preserveSelection = pendingPreserveSelection;
                pendingPreferredSessionId = null;
                pendingPreserveSelection = true;
                await LoadSessionsAsync(preferred, preserveSelection);
            }
        }

        private void ClearSelectionDisplay()
        {
            selectionLoadCancellation?.Cancel();
            HistoryDataGrid.ItemsSource = null;
            SelectedSessionTitle.Text = "暂无课堂记录";
            SelectedSessionMeta.Text = string.Empty;
            SessionSummaryText.Text = "新建课程，或开始并结束一节课堂后，会在这里按次保存。";
            UpdateActionState();
        }

        private async void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!isPageActive)
                return;

            selectionLoadCancellation?.Cancel();
            selectionLoadCancellation?.Dispose();
            selectionLoadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                pageCancellation.Token);
            CancellationToken token = selectionLoadCancellation.Token;

            if (SessionList.SelectedItem is not LectureSessionEntry session)
            {
                ClearSelectionDisplay();
                return;
            }

            SelectedSessionTitle.Text = session.Title;
            SelectedSessionMeta.Text = session.Detail;
            SessionSummaryText.Text = string.IsNullOrWhiteSpace(session.SummaryText)
                ? "本次课堂尚未保存总结。"
                : session.SummaryText;
            HistoryDataGrid.ItemsSource = null;
            UpdateActionState();

            string entrySearch = SessionMetadataMatchesSearch(session)
                ? string.Empty
                : searchText;
            try
            {
                var history = await SQLiteHistoryLogger.LoadSessionHistoryAsync(
                    session.IsLegacy ? null : session.Id,
                    entrySearch,
                    token);
                if (!token.IsCancellationRequested &&
                    SessionList.SelectedItem is LectureSessionEntry selected &&
                    selected.Id == session.Id)
                {
                    HistoryDataGrid.ItemsSource = history;
                    UpdateActionState();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ProductDiagnostics.Write("history.entries-load-failed", exception);
                SnackbarHost.Show(
                    "逐句记录加载失败",
                    exception.Message,
                    SnackbarType.Error,
                    timeout: 3);
            }
        }

        private bool SessionMetadataMatchesSearch(LectureSessionEntry session)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return true;

            return session.Mode.Contains(searchText, StringComparison.CurrentCultureIgnoreCase) ||
                session.ApiUsed.Contains(searchText, StringComparison.CurrentCultureIgnoreCase) ||
                session.TargetLanguage.Contains(searchText, StringComparison.CurrentCultureIgnoreCase) ||
                session.SummaryText.Contains(searchText, StringComparison.CurrentCultureIgnoreCase);
        }

        private void HistoryDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateActionState();
        }

        private void UpdateActionState()
        {
            var session = SessionList.SelectedItem as LectureSessionEntry;
            bool hasSession = session != null;
            bool canMutate = hasSession && !IsCurrentSession(session!);
            bool hasEntry = HistoryDataGrid.SelectedItem is TranslationHistoryEntry;

            EditSessionButton.IsEnabled = canMutate && !session!.IsLegacy;
            DeleteSessionButton.IsEnabled = canMutate;
            CreateEntryButton.IsEnabled = canMutate;
            EditEntryButton.IsEnabled = canMutate && hasEntry;
            DeleteEntryButton.IsEnabled = canMutate && hasEntry;
            ExportSessionButton.IsEnabled = hasSession;
        }

        private static bool IsCurrentSession(LectureSessionEntry session)
        {
            return !session.IsLegacy &&
                LectureSessionTracker.CurrentSessionId == session.Id;
        }

        private bool EnsureSessionCanBeChanged(LectureSessionEntry session)
        {
            if (!IsCurrentSession(session))
                return true;

            SnackbarHost.Show(
                "课堂正在进行",
                "请先结束本次课堂，再修改或删除它。",
                SnackbarType.Warning,
                timeout: 3);
            return false;
        }

        private async void CreateSession_click(object sender, RoutedEventArgs e)
        {
            var nameBox = CreateEditorTextBox("自定义课堂");
            var summaryBox = CreateEditorTextBox(string.Empty, multiline: true);
            var result = await ShowEditorDialogAsync(
                "新建课程",
                "创建",
                BuildEditorPanel(
                    ("课程名称", nameBox),
                    ("课堂总结（可选）", summaryBox)));

            if (result != ContentDialogResult.Primary)
                return;

            string name = nameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                ShowRequiredTextWarning("课程名称不能为空。");
                return;
            }

            try
            {
                var setting = Translator.Setting;
                LectureSessionEntry created = await SQLiteHistoryLogger.CreateReviewSessionAsync(
                    name,
                    summaryBox.Text,
                    setting?.ActiveEngineDisplayName ?? "手动整理",
                    setting?.TargetLanguage ?? "zh-CN",
                    token: pageCancellation.Token);
                await LoadSessionsAsync(created.Id);
                await RefreshRecentSessionsNavigationAsync();
                SnackbarHost.Show("已新建课程", created.Title, SnackbarType.Success);
            }
            catch (OperationCanceledException) when (pageCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ShowMutationError("history.session-create-failed", "新建课程失败", exception);
            }
        }

        private async void EditSession_click(object sender, RoutedEventArgs e)
        {
            if (SessionList.SelectedItem is not LectureSessionEntry session ||
                session.IsLegacy ||
                !EnsureSessionCanBeChanged(session))
            {
                return;
            }

            var nameBox = CreateEditorTextBox(session.Mode);
            var summaryBox = CreateEditorTextBox(session.SummaryText, multiline: true);
            var result = await ShowEditorDialogAsync(
                "编辑课程",
                "保存",
                BuildEditorPanel(
                    ("课程名称", nameBox),
                    ("课堂总结", summaryBox)));

            if (result != ContentDialogResult.Primary)
                return;

            string name = nameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                ShowRequiredTextWarning("课程名称不能为空。");
                return;
            }

            if (!EnsureSessionCanBeChanged(session))
                return;

            try
            {
                bool updated = await SQLiteHistoryLogger.UpdateSessionAsync(
                    session.Id,
                    name,
                    summaryBox.Text,
                    pageCancellation.Token);
                if (!updated)
                {
                    await LoadSessionsAsync();
                    SnackbarHost.Show(
                        "课程已变化",
                        "该课程可能已被删除，请刷新后重试。",
                        SnackbarType.Warning,
                        timeout: 3);
                    return;
                }

                await LoadSessionsAsync(session.Id);
                await RefreshRecentSessionsNavigationAsync();
                SnackbarHost.Show("课程已更新", "名称和总结已保存。", SnackbarType.Success);
            }
            catch (OperationCanceledException) when (pageCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ShowMutationError("history.session-update-failed", "编辑课程失败", exception);
            }
        }

        private async void DeleteSession_click(object sender, RoutedEventArgs e)
        {
            if (SessionList.SelectedItem is not LectureSessionEntry session ||
                !EnsureSessionCanBeChanged(session))
            {
                return;
            }

            string description = session.IsLegacy
                ? $"将删除“历史未分组”中的 {session.EntryCount} 条旧记录。"
                : $"将删除“{session.Title}”及其中 {session.EntryCount} 条逐句记录和课堂总结。";
            var result = await ShowConfirmationDialogAsync(
                "删除这节课程？",
                $"{description}\n\n此操作不可撤销。",
                "确认删除");
            if (result != ContentDialogResult.Primary ||
                !EnsureSessionCanBeChanged(session))
            {
                return;
            }

            try
            {
                var visibleSessions = (SessionList.ItemsSource as
                    IEnumerable<LectureSessionEntry>)?.ToList() ?? [];
                long? preferredSessionId = HistorySelectionPolicy.ChooseAfterDeletion(
                    visibleSessions,
                    session.Id);
                bool deleted = await SQLiteHistoryLogger.DeleteSessionAsync(
                    session.IsLegacy ? null : session.Id,
                    pageCancellation.Token);
                if (deleted)
                {
                    sessionDataVersion++;
                    selectionLoadCancellation?.Cancel();
                    SessionList.SelectedItem = null;
                    HistoryDataGrid.ItemsSource = null;

                    var remainingSessions = visibleSessions
                        .Where(item => item.Id != session.Id)
                        .ToList();
                    SessionList.ItemsSource = remainingSessions;
                    if (preferredSessionId.HasValue)
                    {
                        SessionList.SelectedItem = remainingSessions.FirstOrDefault(
                            item => item.Id == preferredSessionId.Value);
                    }
                    else
                    {
                        ClearSelectionDisplay();
                    }

                    if (Application.Current.MainWindow is MainWindow mainWindow)
                        mainWindow.RemoveRecentSession(session.Id);
                }

                await Task.WhenAll(
                    LoadSessionsAsync(
                        deleted ? preferredSessionId : session.Id,
                        preserveCurrentSelection: false),
                    RefreshRecentSessionsNavigationAsync());
                SnackbarHost.Show(
                    deleted ? "课程已删除" : "没有可删除的记录",
                    deleted ? "其他课程未受影响。" : "记录可能已被其他操作移除。",
                    deleted ? SnackbarType.Success : SnackbarType.Warning);
            }
            catch (OperationCanceledException) when (pageCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ShowMutationError("history.session-delete-failed", "删除课程失败", exception);
            }
        }

        private async void CreateEntry_click(object sender, RoutedEventArgs e)
        {
            if (SessionList.SelectedItem is not LectureSessionEntry session ||
                !EnsureSessionCanBeChanged(session))
            {
                return;
            }

            var sourceBox = CreateEditorTextBox(string.Empty, multiline: true);
            var translationBox = CreateEditorTextBox(string.Empty, multiline: true);
            var result = await ShowEditorDialogAsync(
                "新增逐句记录",
                "添加",
                BuildEditorPanel(
                    ("原文", sourceBox),
                    ("译文", translationBox)));

            if (result != ContentDialogResult.Primary ||
                !ValidateEntryText(sourceBox.Text, translationBox.Text) ||
                !EnsureSessionCanBeChanged(session))
            {
                return;
            }

            try
            {
                string targetLanguage = string.IsNullOrWhiteSpace(session.TargetLanguage) ||
                    session.TargetLanguage == "多种语言"
                        ? Translator.Setting?.TargetLanguage ?? "zh-CN"
                        : session.TargetLanguage;
                await SQLiteHistoryLogger.CreateSessionTranslationAsync(
                    session.IsLegacy ? null : session.Id,
                    sourceBox.Text,
                    translationBox.Text,
                    targetLanguage,
                    "手动编辑",
                    pageCancellation.Token);
                await LoadSessionsAsync(session.Id);
                await RefreshRecentSessionsNavigationAsync();
                SnackbarHost.Show("条目已添加", "已保存到当前课程。", SnackbarType.Success);
            }
            catch (OperationCanceledException) when (pageCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ShowMutationError("history.entry-create-failed", "新增条目失败", exception);
            }
        }

        private async void EditEntry_click(object sender, RoutedEventArgs e)
        {
            if (SessionList.SelectedItem is not LectureSessionEntry session ||
                HistoryDataGrid.SelectedItem is not TranslationHistoryEntry entry ||
                !EnsureSessionCanBeChanged(session))
            {
                return;
            }

            var sourceBox = CreateEditorTextBox(entry.SourceText, multiline: true);
            var translationBox = CreateEditorTextBox(entry.TranslatedText, multiline: true);
            var result = await ShowEditorDialogAsync(
                "编辑逐句记录",
                "保存",
                BuildEditorPanel(
                    ("原文", sourceBox),
                    ("译文", translationBox)));

            if (result != ContentDialogResult.Primary ||
                !ValidateEntryText(sourceBox.Text, translationBox.Text) ||
                !EnsureSessionCanBeChanged(session))
            {
                return;
            }

            try
            {
                bool updated = await SQLiteHistoryLogger.UpdateTranslationAsync(
                    entry.Id,
                    session.IsLegacy ? null : session.Id,
                    sourceBox.Text,
                    translationBox.Text,
                    pageCancellation.Token);
                if (!updated)
                {
                    await LoadSessionsAsync(session.Id);
                    SnackbarHost.Show(
                        "条目已变化",
                        "该条目可能已被删除或移动，请重试。",
                        SnackbarType.Warning,
                        timeout: 3);
                    return;
                }

                await LoadSessionsAsync(session.Id);
                await RefreshRecentSessionsNavigationAsync();
                SnackbarHost.Show("条目已更新", "原文和译文已保存。", SnackbarType.Success);
            }
            catch (OperationCanceledException) when (pageCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ShowMutationError("history.entry-update-failed", "编辑条目失败", exception);
            }
        }

        private async void DeleteEntry_click(object sender, RoutedEventArgs e)
        {
            if (SessionList.SelectedItem is not LectureSessionEntry session ||
                HistoryDataGrid.SelectedItem is not TranslationHistoryEntry entry ||
                !EnsureSessionCanBeChanged(session))
            {
                return;
            }

            string preview = entry.SourceText.Length > 80
                ? $"{entry.SourceText[..80]}…"
                : entry.SourceText;
            var result = await ShowConfirmationDialogAsync(
                "删除这条记录？",
                $"原文：{preview}\n\n只会删除当前这一条记录，此操作不可撤销。",
                "确认删除");
            if (result != ContentDialogResult.Primary ||
                !EnsureSessionCanBeChanged(session))
            {
                return;
            }

            try
            {
                bool deleted = await SQLiteHistoryLogger.DeleteTranslationAsync(
                    entry.Id,
                    session.IsLegacy ? null : session.Id,
                    pageCancellation.Token);
                await LoadSessionsAsync(session.Id);
                await RefreshRecentSessionsNavigationAsync();
                SnackbarHost.Show(
                    deleted ? "条目已删除" : "条目不存在",
                    deleted ? "当前课程的其他内容未受影响。" : "它可能已被其他操作移除。",
                    deleted ? SnackbarType.Success : SnackbarType.Warning);
            }
            catch (OperationCanceledException) when (pageCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ShowMutationError("history.entry-delete-failed", "删除条目失败", exception);
            }
        }

        private async void Refresh_click(object sender, RoutedEventArgs e)
        {
            await LoadSessionsAsync();
        }

        private static async Task RefreshRecentSessionsNavigationAsync()
        {
            if (Application.Current.MainWindow is MainWindow mainWindow)
                await mainWindow.RefreshRecentSessionsAsync();
        }

        private async void ExportAll_click(object sender, RoutedEventArgs e)
        {
            await ExportAsync(
                $"lecture_history_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv",
                filePath => SQLiteHistoryLogger.ExportToCSV(
                    filePath, pageCancellation.Token));
        }

        private async void ExportSession_click(object sender, RoutedEventArgs e)
        {
            if (SessionList.SelectedItem is not LectureSessionEntry session)
                return;

            string safeName = SanitizeFileName(session.Mode);
            await ExportAsync(
                $"{safeName}_{session.StartedAt.LocalDateTime:yyyy-MM-dd_HH-mm}.csv",
                filePath => SQLiteHistoryLogger.ExportSessionToCSV(
                    filePath,
                    session.IsLegacy ? null : session.Id,
                    pageCancellation.Token));
        }

        private static string SanitizeFileName(string value)
        {
            string result = value;
            foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
                result = result.Replace(invalidCharacter, '_');
            return string.IsNullOrWhiteSpace(result) ? "lecture" : result;
        }

        private static async Task ExportAsync(
            string defaultFileName,
            Func<string, Task> exportAction)
        {
            var saveFileDialog = new SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv|All file (*.*)|*.*",
                DefaultExt = ".csv",
                FileName = defaultFileName,
                InitialDirectory = Environment.GetFolderPath(
                    Environment.SpecialFolder.MyDocuments)
            };

            if (saveFileDialog.ShowDialog() != true)
                return;

            try
            {
                await exportAction(saveFileDialog.FileName);
                SnackbarHost.Show(
                    "导出成功",
                    $"已保存到：{saveFileDialog.FileName}",
                    SnackbarType.Success,
                    timeout: 3);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                ProductDiagnostics.Write("history.export-failed", exception);
                SnackbarHost.Show(
                    "导出失败",
                    exception.Message,
                    SnackbarType.Error,
                    timeout: 3);
            }
        }

        private async void HistorySearchBox_QuerySubmitted(
            AutoSuggestBox sender,
            AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            searchText = sender.Text?.Trim() ?? string.Empty;
            await LoadSessionsAsync();
        }

        private async void HistorySearchBox_TextChanged(
            AutoSuggestBox sender,
            AutoSuggestBoxTextChangedEventArgs args)
        {
            if (string.IsNullOrWhiteSpace(sender.Text) &&
                !string.IsNullOrEmpty(searchText))
            {
                searchText = string.Empty;
                await LoadSessionsAsync();
            }
        }

        private async Task<ContentDialogResult?> ShowEditorDialogAsync(
            string title,
            string primaryButtonText,
            FrameworkElement content)
        {
            return await ShowDialogAsync(new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = primaryButtonText,
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                Padding = new Thickness(8, 4, 8, 8)
            });
        }

        private async Task<ContentDialogResult?> ShowConfirmationDialogAsync(
            string title,
            string content,
            string primaryButtonText)
        {
            return await ShowDialogAsync(new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = primaryButtonText,
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                Padding = new Thickness(8, 4, 8, 8)
            });
        }

        private static async Task<ContentDialogResult?> ShowDialogAsync(ContentDialog dialog)
        {
            var dialogHostContainer =
                (Application.Current.MainWindow as MainWindow)?.DialogHostContainer;
            if (dialogHostContainer == null)
            {
                SnackbarHost.Show(
                    "无法打开编辑窗口",
                    "主窗口尚未准备完成，请稍后重试。",
                    SnackbarType.Error,
                    timeout: 3);
                return null;
            }

            dialog.DialogHost = dialogHostContainer;
            dialogHostContainer.Visibility = Visibility.Visible;
            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                dialogHostContainer.Visibility = Visibility.Collapsed;
            }
        }

        private static StackPanel BuildEditorPanel(
            params (string Label, TextBox Editor)[] fields)
        {
            var panel = new StackPanel { Width = 520 };
            foreach ((string label, TextBox editor) in fields)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = label,
                    Margin = new Thickness(0, 10, 0, 5),
                    FontFamily = (FontFamily)Application.Current.FindResource("LectureUiFontFamily"),
                    Foreground = new SolidColorBrush(Color.FromRgb(96, 96, 92)),
                    FontSize = 12
                });
                panel.Children.Add(editor);
            }
            return panel;
        }

        private static TextBox CreateEditorTextBox(
            string text,
            bool multiline = false)
        {
            return new TextBox
            {
                Text = text,
                MinHeight = multiline ? 96 : 34,
                MaxHeight = multiline ? 180 : 34,
                AcceptsReturn = multiline,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                VerticalScrollBarVisibility = multiline
                    ? ScrollBarVisibility.Auto
                    : ScrollBarVisibility.Hidden,
                Padding = new Thickness(9, 6, 9, 6),
                FontFamily = (FontFamily)Application.Current.FindResource("LectureUiFontFamily"),
                Background = Brushes.White,
                Foreground = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(206, 206, 202)),
                BorderThickness = new Thickness(1)
            };
        }

        private static bool ValidateEntryText(string sourceText, string translatedText)
        {
            if (!string.IsNullOrWhiteSpace(sourceText) &&
                !string.IsNullOrWhiteSpace(translatedText))
            {
                return true;
            }

            ShowRequiredTextWarning("原文和译文都不能为空。");
            return false;
        }

        private static void ShowRequiredTextWarning(string message)
        {
            SnackbarHost.Show(
                "内容未填写完整",
                message,
                SnackbarType.Warning,
                timeout: 3);
        }

        private static void ShowMutationError(
            string diagnosticEvent,
            string title,
            Exception exception)
        {
            ProductDiagnostics.Write(diagnosticEvent, exception);
            SnackbarHost.Show(
                title,
                exception.Message,
                SnackbarType.Error,
                timeout: 3);
        }
    }
}
