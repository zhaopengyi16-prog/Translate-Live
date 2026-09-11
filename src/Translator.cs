using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Automation;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.services;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public static class Translator
    {
        private sealed record CaptionTranslationSubmission(
            TranslationTaskIdentity Identity,
            string Text);

        private static AutomationElement? window = null;
        private static Caption? caption = null;
        private static Setting? setting = null;

        private static readonly System.Collections.Concurrent.ConcurrentQueue<CaptionTranslationSubmission>
            pendingTextQueue = new();
        private static readonly TranslationTaskQueue translationTaskQueue =
            new(acceptedResultHandler: ApplyAcceptedContext);
        private static readonly LiveCaptionSegmenter captionSegmenter = new();
        private static readonly LiveCaptionsConnectionPolicy liveCaptionsConnectionPolicy = new();
        private static readonly SemaphoreSlim liveCaptionsConnectionGate = new(1, 1);
        private static readonly TranslationSegmentPersistence segmentPersistence = new(
            CreatePersistedSegmentAsync,
            UpdatePersistedSegmentAsync);
        private static bool captureSuspended;

        public static AutomationElement? Window
        {
            get => Volatile.Read(ref window);
            set
            {
                AutomationElement? previous = Interlocked.Exchange(ref window, value);
                if ((previous == null) != (value == null))
                    LiveCaptionsConnectionChanged?.Invoke(value != null);
            }
        }
        public static Caption? Caption => caption;
        public static Setting? Setting => setting;

        private static bool logOnlyFlag;
        public static bool LogOnlyFlag
        {
            get => Volatile.Read(ref logOnlyFlag);
            set
            {
                if (value == Volatile.Read(ref logOnlyFlag))
                    return;

                Volatile.Write(ref logOnlyFlag, value);
                translationTaskQueue.SignalOutput();
            }
        }
        public static bool FirstUseFlag { get; set; } = false;

        public static event Action? TranslationLogged;
        public static event Action<TranslationHistoryChange>? TranslationEntryLogged;
        public static event Action<TranscriptSegment>? TranscriptSegmentChanged;
        public static event Action<TranscriptSegment?>? TranscriptDraftChanged;
        public static event Action<bool>? LiveCaptionsConnectionChanged;

        static Translator()
        {
            if (!File.Exists(AppPaths.Current.SettingsFile))
                FirstUseFlag = true;

            caption = Caption.GetInstance();
            setting = Setting.Load();
        }

        public static async Task<bool> ConnectLiveCaptionsAsync(
            bool userInitiated,
            CancellationToken token = default)
        {
            if (!userInitiated &&
                !liveCaptionsConnectionPolicy.TryClaimAutomaticConnection())
            {
                return Window != null;
            }

            try
            {
                await liveCaptionsConnectionGate.WaitAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return false;
            }

            Caption? captionState = Caption;
            try
            {
                if (Window != null)
                    return true;
                if (captionState == null)
                    return false;

                captionState.DisplayTranslatedCaption =
                    "[状态] 正在连接 Windows 实时字幕…";
                bool hideOnConnect = !FirstUseFlag;
                AutomationElement connectedWindow = await Task.Run(
                    () => LiveCaptionsHandler.LaunchLiveCaptions(
                        hideImmediately: hideOnConnect,
                        token),
                    token);

                if (hideOnConnect)
                {
                    if (!LiveCaptionsHandler.HideLiveCaptions(connectedWindow))
                        ProductDiagnostics.Write("livecaptions.hide-failed");
                }
                else
                {
                    LiveCaptionsHandler.FixLiveCaptions(connectedWindow);
                }

                string existingSnapshot = string.Empty;
                if (!LiveCaptionsHandler.WasStartedByCurrentApp)
                {
                    try
                    {
                        existingSnapshot = LiveCaptionsHandler.GetCaptions(connectedWindow);
                    }
                    catch (Exception exception) when (
                        exception is ElementNotAvailableException or InvalidOperationException or
                            System.Runtime.InteropServices.COMException)
                    {
                        ProductDiagnostics.Write(
                            "livecaptions.initial-snapshot-unavailable",
                            exception);
                    }
                }
                captionSegmenter.StartFromCurrentSnapshot(existingSnapshot);
                captionState.DisplayTranslatedCaption = string.Empty;
                Window = connectedWindow;
                ProductDiagnostics.Write(userInitiated
                    ? "livecaptions.user-connected"
                    : "livecaptions.connected");
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception)
            {
                LiveCaptionsHandler.NotifyWindowUnavailable();
                Window = null;
                if (captionState != null)
                {
                    captionState.DisplayTranslatedCaption = userInitiated
                        ? "[状态] Windows 实时字幕重新连接失败，可稍后重试。"
                        : "[状态] Windows 实时字幕连接失败，请在菜单中重新连接。";
                }
                ProductDiagnostics.Write(userInitiated
                    ? "livecaptions.user-connect-failed"
                    : "livecaptions.initial-connect-failed", exception);
                return false;
            }
            finally
            {
                liveCaptionsConnectionGate.Release();
            }
        }

        public static void SyncLoop(CancellationToken token = default)
        {
            int idleCount = 0;
            int syncCount = 0;
            Guid? lastPublishedDraftId = null;
            int lastPublishedDraftRevision = -1;
            Guid? lastQueuedDraftId = null;
            int lastQueuedDraftRevision = -1;

            while (!token.IsCancellationRequested)
            {
                if (Volatile.Read(ref captureSuspended))
                {
                    if (token.WaitHandle.WaitOne(25))
                        return;
                    continue;
                }

                if (Window == null)
                {
                    if (token.WaitHandle.WaitOne(2000))
                        return;
                    continue;
                }

                string fullText = string.Empty;
                try
                {
                    // Check LiveCaptions.exe still alive
                    var info = Window.Current;
                    var name = info.Name;
                    // Get the text recognized by LiveCaptions (10-20ms)
                    fullText = LiveCaptionsHandler.GetCaptions(Window);
                }
                catch (Exception exception) when (
                    exception is ElementNotAvailableException or InvalidOperationException or
                        System.Runtime.InteropServices.COMException)
                {
                    LiveCaptionsHandler.NotifyWindowUnavailable();
                    Window = null;
                    captionSegmenter.Reset();
                    Caption?.DisplayTranslatedCaption =
                        "[状态] Windows 实时字幕已关闭，请在菜单中重新连接。";
                    ProductDiagnostics.Write("livecaptions.disconnected", exception);
                    continue;
                }
                if (string.IsNullOrEmpty(fullText))
                {
                    if (token.WaitHandle.WaitOne(25))
                        return;
                    continue;
                }

                // Preserve the recognizer's words and punctuation. The segmenter
                // normalizes only visual whitespace so decimals, acronyms, and
                // technical identifiers are not rewritten before translation.
                LiveCaptionUpdate update = captionSegmenter.Process(fullText);

                string currentCaption = update.CurrentText;
                LiveCaptionSegment? currentSegment = update.CurrentSegment;
                if (currentCaption.Length > 0 && currentSegment != null)
                {
                    var currentIdentity = ToIdentity(currentSegment);
                    Caption.BeginCurrentSegment(currentIdentity);
                    Caption.OverlayOriginalCaption = TextUtil.ShortenDisplaySentence(
                        currentCaption,
                        TextUtil.VERYLONG_THRESHOLD);
                    if (!string.Equals(
                            Caption.DisplayOriginalCaption,
                            currentCaption,
                            StringComparison.Ordinal))
                    {
                        Caption.DisplayOriginalCaption = TextUtil.ShortenDisplaySentence(
                            currentCaption,
                            TextUtil.VERYLONG_THRESHOLD);
                    }
                }

                // A final sentence is queued exactly once. In particular, an idle
                // timer must not submit the same completed sentence again.
                foreach (LiveCaptionSegment finalizedSegment in update.FinalizedSegments)
                {
                    Caption.OriginalCaption = finalizedSegment.Text;
                    var identity = ToIdentity(finalizedSegment);
                    pendingTextQueue.Enqueue(new CaptionTranslationSubmission(
                        identity,
                        finalizedSegment.Text));
                    TranscriptSegmentChanged?.Invoke(ToTranscriptSegment(
                        finalizedSegment,
                        translatedText: null,
                        SegmentState.Committed));
                    syncCount = 0;
                    idleCount = 0;
                }

                LiveCaptionSegment? draftSegment = update.DraftSegment;
                string draft = draftSegment?.Text ?? string.Empty;
                if (draftSegment != null)
                {
                    if (lastPublishedDraftId != draftSegment.Id ||
                        lastPublishedDraftRevision != draftSegment.Revision)
                    {
                        TranscriptDraftChanged?.Invoke(ToTranscriptSegment(
                            draftSegment,
                            translatedText: null,
                            SegmentState.Draft));
                        lastPublishedDraftId = draftSegment.Id;
                        lastPublishedDraftRevision = draftSegment.Revision;
                    }
                }
                else if (lastPublishedDraftId.HasValue)
                {
                    TranscriptDraftChanged?.Invoke(null);
                    lastPublishedDraftId = null;
                    lastPublishedDraftRevision = -1;
                }

                if (draftSegment != null && update.DraftIsEligible)
                {
                    bool draftIsLongEnough =
                        Encoding.UTF8.GetByteCount(draft) >= TextUtil.SHORT_THRESHOLD;
                    if (!string.Equals(
                            Caption.OriginalCaption,
                            draft,
                            StringComparison.Ordinal))
                    {
                        Caption.OriginalCaption = draft;
                        idleCount = 0;
                        if (draftIsLongEnough)
                            syncCount++;
                    }
                    else
                    {
                        idleCount++;
                    }

                    // Translate a sufficiently useful draft after several
                    // revisions or after recognition pauses. Very short partials
                    // wait for more speech or final punctuation.
                    if (draftIsLongEnough &&
                        (syncCount > Setting.MaxSyncInterval ||
                         idleCount == Setting.MaxIdleInterval ||
                         update.DraftStableFor >=
                            LiveCaptionSegmentationThresholds.DraftQuietTranslationDelay) &&
                        (lastQueuedDraftId != draftSegment.Id ||
                         lastQueuedDraftRevision != draftSegment.Revision))
                    {
                        syncCount = 0;
                        var identity = ToIdentity(draftSegment);
                        pendingTextQueue.Enqueue(new CaptionTranslationSubmission(identity, draft));
                        lastQueuedDraftId = draftSegment.Id;
                        lastQueuedDraftRevision = draftSegment.Revision;
                    }
                }
                else
                {
                    syncCount = 0;
                    idleCount = 0;
                }

                if (token.WaitHandle.WaitOne(25))
                    return;
            }
        }

        public static async Task TranslateLoop(CancellationToken token = default)
        {
            await ConnectLiveCaptionsAsync(userInitiated: false, token);

            while (!token.IsCancellationRequested)
            {
                // Drain all captured revisions without adding another polling delay.
                // TranslationTaskQueue keeps distinct sentences and collapses stale revisions.
                bool processedAny = false;
                while (pendingTextQueue.TryDequeue(out CaptionTranslationSubmission? submission))
                {
                    processedAny = true;
                    string queuedText = submission.Text;
                    if (LogOnlyFlag)
                    {
                        long? sessionId = LectureSessionTracker.CurrentSessionId;
                        await LogOnly(
                            queuedText,
                            isOverwrite: false,
                            sessionId: sessionId,
                            token: token,
                            identity: submission.Identity);
                    }
                    else
                    {
                        string apiName = Setting.ApiName;
                        var translateFunction = TranslateAPI.GetFunction(apiName);
                        long? sessionId = LectureSessionTracker.CurrentSessionId;
                        string targetLanguage = Setting.TargetLanguage;
                        translationTaskQueue.Enqueue(
                            (requestToken, partialOutput) => Translate(
                                queuedText,
                                apiName,
                                translateFunction,
                                targetLanguage,
                                partialOutput,
                                requestToken),
                            queuedText,
                            apiName,
                            sessionId,
                            targetLanguage,
                            submission.Identity,
                            token);
                    }
                }

                if (processedAny)
                    await Task.Yield();
                else
                    await Task.Delay(20, token);
            }
        }

        public static async Task DisplayLoop(CancellationToken token = default)
        {
            while (!token.IsCancellationRequested)
            {
                TranslationQueueResult result =
                    await translationTaskQueue.ReadLatestResultAsync(token);
                string translatedText = result.TranslatedText;

                if (LogOnlyFlag)
                {
                    Caption.TranslatedCaption = string.Empty;
                    Caption.DisplayTranslatedCaption = "[Paused]";
                    Caption.OverlayNoticePrefix = "[Paused]";
                    Caption.OverlayCurrentTranslation = string.Empty;
                }
                else if (!string.IsNullOrEmpty(RegexPatterns.NoticePrefix().Replace(
                             translatedText, string.Empty).Trim()))
                {
                    PublishTranscriptResult(result);

                    string noticePrefix = string.Empty;
                    string overlayTranslation;
                    if (translatedText.Contains("[ERROR]") ||
                        translatedText.Contains("[WARNING]"))
                    {
                        overlayTranslation = translatedText;
                    }
                    else
                    {
                        var match = RegexPatterns.NoticePrefixAndTranslation().Match(translatedText);
                        noticePrefix = match.Groups[1].Value.Trim();
                        overlayTranslation = match.Groups[2].Value.Trim();
                    }

                    bool appliesToCurrent = result.Identity == null;
                    if (result.Identity != null)
                    {
                        appliesToCurrent = Caption.TryApplyCurrentTranslation(
                            result.Identity,
                            overlayTranslation);
                    }

                    if (appliesToCurrent)
                    {
                        Caption.TranslatedCaption = translatedText;
                        Caption.DisplayTranslatedCaption = TextUtil.ShortenDisplaySentence(
                            translatedText,
                            TextUtil.VERYLONG_THRESHOLD);
                        Caption.OverlayNoticePrefix = noticePrefix;
                        if (result.Identity == null)
                            Caption.OverlayCurrentTranslation = overlayTranslation;
                    }
                }
                else if (string.Equals(
                             Caption.DisplayTranslatedCaption,
                             "[Paused]",
                             StringComparison.Ordinal))
                {
                    Caption.DisplayTranslatedCaption = Caption.TranslatedCaption;
                    Caption.OverlayNoticePrefix = string.Empty;
                    Caption.OverlayCurrentTranslation = Caption.TranslatedCaption;
                }
            }
        }

        public static async Task<(string, bool)> Translate(string text, CancellationToken token = default)
        {
            string apiName = Setting.ApiName;
            return await Translate(
                text,
                apiName,
                TranslateAPI.GetFunction(apiName),
                Setting.TargetLanguage,
                null,
                token);
        }

        private static async Task<(string, bool)> Translate(
            string text,
            string apiName,
            Func<string, CancellationToken, Task<string>> translateFunction,
            string targetLanguage,
            Action<string>? partialOutput,
            CancellationToken token = default)
        {
            string translatedText;
            bool isChoke = Array.IndexOf(TextUtil.PUNC_EOS, text[^1]) != -1;
            var providerStopwatch = Stopwatch.StartNew();

            try
            {
                var sw = Setting.MainWindow.LatencyShow ? Stopwatch.StartNew() : null;

                Task<string> ExecuteAsync(string input, Action<string>? progress = null) =>
                    TranslateAPI.Execute(
                        apiName,
                        translateFunction,
                        input,
                        targetLanguage,
                        progress,
                        token);

                void PublishPartial(string value)
                {
                    string partial = RegexPatterns.ModelThinking()
                        .Replace(value, string.Empty)
                        .Replace("🔤", string.Empty)
                        .Trim();
                    if (partial.Length > 0 &&
                        !TranslationTextPolicy.IsProviderNotice(partial))
                    {
                        partialOutput?.Invoke(partial);
                    }
                }

                if (Setting.ContextAware &&
                    !TranslateAPI.IsLLMBasedProvider(apiName) &&
                    !TranslateAPI.RequiresPlainTextInput(apiName))
                {
                    string contextualResponse = await ExecuteAsync(
                        $"{Caption.AwareContextsCaption} 🔤 {text} 🔤");
                    if (TranslationTextPolicy.IsProviderNotice(contextualResponse))
                    {
                        translatedText = contextualResponse;
                    }
                    else if (!TranslationTextPolicy.TryExtractTargetSentence(
                                 contextualResponse, out translatedText))
                    {
                        ProductDiagnostics.Write($"translation.context-target-missing.{apiName}");
                        translatedText = await ExecuteAsync(text);
                    }
                }
                else
                {
                    translatedText = await ExecuteAsync(text, PublishPartial);
                    translatedText = translatedText.Replace("🔤", "");
                }

                translatedText = TranslationTextPolicy.EnsureUsable(translatedText);

                if (translatedText.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase))
                    ProductDiagnostics.Write($"translation.failed.{apiName}");

                if (sw != null)
                {
                    sw.Stop();
                    translatedText = $"[{sw.ElapsedMilliseconds,4} ms] " + translatedText;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ($"[ERROR] Translation Failed: {ex.Message}", isChoke);
            }
            finally
            {
                providerStopwatch.Stop();
                if (providerStopwatch.Elapsed >= TimeSpan.FromSeconds(1))
                {
                    ProductDiagnostics.WriteDuration(
                        $"translation.provider.slow.{apiName}",
                        providerStopwatch.ElapsedMilliseconds);
                }
            }

            return (translatedText, isChoke);
        }

        private static void PublishTranscriptResult(TranslationQueueResult result)
        {
            TranslationTaskIdentity? identity = result.Identity;
            if (identity == null)
                return;

            bool failed = result.TranslatedText.Contains(
                "[ERROR]",
                StringComparison.OrdinalIgnoreCase);
            var segment = new TranscriptSegment(
                identity.SegmentId,
                identity.Sequence,
                identity.Revision,
                result.OriginalText,
                failed ? null : result.TranslatedText,
                identity.IsFinal
                    ? result.IsPartial
                        ? SegmentState.Translating
                        : failed
                            ? SegmentState.TranslationFailed
                            : SegmentState.Translated
                    : SegmentState.Draft,
                identity.CapturedAt);

            if (identity.IsFinal)
                TranscriptSegmentChanged?.Invoke(segment);
            else
                TranscriptDraftChanged?.Invoke(segment);
        }

        private static TranslationTaskIdentity ToIdentity(LiveCaptionSegment segment)
        {
            return new TranslationTaskIdentity(
                segment.Id,
                segment.Sequence,
                segment.Revision,
                segment.IsFinal,
                segment.CapturedAt);
        }

        private static TranscriptSegment ToTranscriptSegment(
            LiveCaptionSegment segment,
            string? translatedText,
            SegmentState state)
        {
            return new TranscriptSegment(
                segment.Id,
                segment.Sequence,
                segment.Revision,
                segment.Text,
                translatedText,
                state,
                segment.CapturedAt);
        }

        public static async Task<TranslationHistoryChange?> Log(string originalText, string translatedText,
            bool isOverwrite = false, string? apiName = null, long? sessionId = null,
            CancellationToken token = default, string? targetLanguageSnapshot = null,
            TranslationTaskIdentity? identity = null)
        {
            string targetLanguage, resolvedApiName;
            if (Setting != null)
            {
                targetLanguage = targetLanguageSnapshot ?? Setting.TargetLanguage;
                resolvedApiName = apiName ?? Setting.ApiName;
            }
            else
            {
                targetLanguage = "N/A";
                resolvedApiName = apiName ?? "N/A";
            }

            try
            {
                long? replacedEntryId = null;
                TranslationHistoryEntry? entry = null;
                if (identity != null && sessionId.HasValue)
                {
                    TranslationPersistenceResult persistenceResult =
                        await segmentPersistence.UpsertAsync(
                            new TranslationPersistenceRequest(
                                sessionId.Value,
                                identity,
                                originalText,
                                translatedText,
                                targetLanguage,
                                resolvedApiName),
                            token);
                    if (!persistenceResult.Applied)
                        return null;
                    entry = persistenceResult.Entry;
                }
                else
                {
                    if (isOverwrite)
                    {
                        replacedEntryId = await SQLiteHistoryLogger.DeleteLastTranslation(
                            sessionId,
                            token);
                    }
                    entry = await SQLiteHistoryLogger.LogTranslation(
                        originalText,
                        translatedText,
                        targetLanguage,
                        resolvedApiName,
                        sessionId,
                        token);
                }

                var change = new TranslationHistoryChange(
                    entry,
                    replacedEntryId,
                    identity?.SegmentId,
                    identity?.Sequence,
                    identity?.Revision ?? 0,
                    identity?.IsFinal ?? true);
                TranslationEntryLogged?.Invoke(change);
                TranslationLogged?.Invoke();
                return change;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] Logging history failed.", ex.Message, SnackbarType.Error, 
                    timeout: 2, closeButton: true);
                return null;
            }
        }

        public static async Task LogOnly(string originalText,
            bool isOverwrite = false, long? sessionId = null,
            CancellationToken token = default,
            TranslationTaskIdentity? identity = null)
        {
            try
            {
                await Log(
                    originalText,
                    "N/A",
                    isOverwrite,
                    "LogOnly",
                    sessionId,
                    token,
                    "N/A",
                    identity);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] Logging history failed.", ex.Message, SnackbarType.Error, 
                    timeout: 2, closeButton: true);
            }
        }

        public static async Task AddContexts(long? sessionId = null, CancellationToken token = default)
        {
            var lastLog = await SQLiteHistoryLogger.LoadLastTranslation(sessionId, token);
            if (lastLog == null)
                return;

            Caption?.UpsertContext(lastLog);
        }

        internal static void ApplyLoggedContext(TranslationHistoryChange change)
        {
            Caption?.UpsertContext(
                change.Entry,
                change.ReplacedEntryId,
                change.SegmentId);
        }

        private static long transientContextId;

        private static void ApplyAcceptedContext(TranslationQueueResult result)
        {
            if (!result.IsComplete ||
                string.IsNullOrWhiteSpace(result.TranslatedText) ||
                TranslationTextPolicy.IsProviderNotice(result.TranslatedText))
            {
                return;
            }

            DateTime localTime = result.Identity?.CapturedAt.LocalDateTime ?? DateTime.Now;
            Caption?.UpsertContext(new TranslationHistoryEntry
            {
                Id = Interlocked.Decrement(ref transientContextId),
                SessionId = result.SessionId,
                Timestamp = localTime.ToString("MM/dd HH:mm"),
                TimestampFull = localTime.ToString("MM/dd/yy, HH:mm:ss"),
                SourceText = result.OriginalText,
                TranslatedText = result.TranslatedText,
                TargetLanguage = result.TargetLanguage,
                ApiUsed = result.ApiName
            }, segmentId: result.Identity?.SegmentId);
        }

        public static void ClearContexts()
        {
            Caption?.ClearContextHistory();
        }

        public static async Task SuspendAndResetCaptureAsync(CancellationToken token = default)
        {
            Volatile.Write(ref captureSuspended, true);
            captionSegmenter.Reset();
            translationTaskQueue.CancelPendingAndActive();
            while (pendingTextQueue.TryDequeue(out _))
            {
            }
            await translationTaskQueue.WaitForPersistenceAsync().WaitAsync(token);
            await segmentPersistence.ClearAsync(token);
            ClearContexts();
            if (Caption != null)
            {
                Caption.ClearCurrentSegment();
                Caption.OriginalCaption = string.Empty;
                Caption.TranslatedCaption = string.Empty;
                Caption.DisplayOriginalCaption = string.Empty;
                Caption.DisplayTranslatedCaption = string.Empty;
                Caption.OverlayOriginalCaption = " ";
                Caption.OverlayNoticePrefix = " ";
                Caption.OverlayCurrentTranslation = " ";
            }
        }

        public static void ResumeCapture()
        {
            // Seed the first current Windows caption snapshot after the session
            // transition, so text left on screen from the previous class is not
            // replayed into the new class.
            captionSegmenter.Reset();
            Volatile.Write(ref captureSuspended, false);
        }

        private static Task<TranslationHistoryEntry> CreatePersistedSegmentAsync(
            TranslationPersistenceRequest request,
            CancellationToken token)
        {
            return SQLiteHistoryLogger.LogTranslation(
                request.SourceText,
                request.TranslatedText,
                request.TargetLanguage,
                request.ApiName,
                request.SessionId,
                token,
                request.Identity.CapturedAt);
        }

        private static Task<TranslationHistoryEntry?> UpdatePersistedSegmentAsync(
            long entryId,
            TranslationPersistenceRequest request,
            CancellationToken token)
        {
            return SQLiteHistoryLogger.UpdateLoggedTranslationAsync(
                entryId,
                request.SessionId,
                request.SourceText,
                request.TranslatedText,
                request.TargetLanguage,
                request.ApiName,
                token);
        }

        // If this text is too similar to the last one, overwrite it when logging.
        public static async Task<bool> IsOverwrite(
            string originalText, long? sessionId = null, CancellationToken token = default)
        {
            string lastOriginalText = await SQLiteHistoryLogger.LoadLastSourceText(sessionId, token);
            if (lastOriginalText == null)
                return false;
            
            return CaptionRevisionPolicy.IsRevision(lastOriginalText, originalText);
        }
    }
}
