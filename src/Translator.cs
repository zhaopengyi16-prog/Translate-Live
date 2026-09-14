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
    internal sealed record CaptionTranslationSubmission(
        TranslationTaskIdentity Identity,
        string Text,
        long? SessionId);

    public static class Translator
    {
        private static AutomationElement? window = null;
        private static Caption? caption = null;
        private static Setting? setting = null;

        private static readonly System.Collections.Concurrent.ConcurrentQueue<CaptionTranslationSubmission>
            pendingTextQueue = new();
        private static readonly TranslationTaskQueue translationTaskQueue =
            new(acceptedResultHandler: OnAcceptedTranslation, resultValidator: CanAcceptTranslation);
        private static readonly LiveCaptionIdentityResolver captionIdentityResolver = new(splitLongDrafts: false);
        private static readonly CaptionRecordingPolicy recordingPolicy = new();
        private static readonly object captureStateLock = new();
        private static readonly object translationIngressLock = new();
        private static readonly Stopwatch observationClock = Stopwatch.StartNew();
        private static readonly DateTimeOffset observationOrigin = DateTimeOffset.UtcNow;
        private static long captureEpoch = 1;
        private static Task recordingPersistenceTail = Task.CompletedTask;
        private static int historyWriteFailed;
        internal static bool HasHistoryWriteFailure => Volatile.Read(ref historyWriteFailed) != 0;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<
            (long Epoch, Guid Id, int Revision, string Api, string Language), TranslationQueueResult>
            provisionalTranslations = new();
        public static long CaptureEpoch => Interlocked.Read(ref captureEpoch);
        private static readonly LiveCaptionsConnectionPolicy liveCaptionsConnectionPolicy = new();
        private static readonly SemaphoreSlim liveCaptionsConnectionGate = new(1, 1);
        private static readonly TranslationSegmentPersistence segmentPersistence = new(
            CreatePersistedSegmentAsync,
            UpdatePersistedSegmentAsync);
        private static bool captureSuspended;
        private static long capturePreparationSequence;
        private static long activeCapturePreparationId;
        private static AutomationElement? preparedCaptureWindow;

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
                lock (captureStateLock)
                {
                    captionIdentityResolver.StartFromCurrentSnapshot(
                        existingSnapshot, observationOrigin + observationClock.Elapsed);
                    recordingPolicy.Reset();
                }
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
            Guid? lastPublishedId = null;
            int lastPublishedRevision = -1;
            Guid? lastQueuedId = null;
            int lastQueuedRevision = -1;
            bool lastQueuedFinal = false;
            long lastEpoch = -1;
            int usefulRevisions = 0;

            while (!token.IsCancellationRequested)
            {
                if (Volatile.Read(ref captureSuspended) || Window == null)
                {
                    if (token.WaitHandle.WaitOne(100))
                        return;
                    continue;
                }

                AutomationElement? observedWindow = Window;
                long observedEpoch = CaptureEpoch;
                long? observedSession = LectureSessionTracker.CurrentSessionId;
                string fullText;
                try
                {
                    if (observedWindow == null)
                        continue;
                    fullText = LiveCaptionsHandler.GetCaptions(observedWindow);
                }
                catch (Exception exception) when (
                    exception is ElementNotAvailableException or InvalidOperationException or
                        System.Runtime.InteropServices.COMException)
                {
                    lock (captureStateLock)
                    {
                        if (observedEpoch != CaptureEpoch || !ReferenceEquals(observedWindow, Window))
                            continue;
                        // Keep the last observed words before rebuilding source
                        // identity. Networking may finish later, source cannot vanish.
                        foreach (RecordedCaption record in recordingPolicy.Flush())
                            PublishRecordedCaption(record, observedSession, observedEpoch, enqueueTranslation: false);
                        Interlocked.Increment(ref captureEpoch);
                        captionIdentityResolver.Reset();
                        recordingPolicy.Reset();
                        Caption?.ClearCurrentSegment();
                        Window = null;
                    }
                    LiveCaptionsHandler.NotifyWindowUnavailable();
                    Caption?.DisplayTranslatedCaption =
                        "[状态] Windows 实时字幕已关闭，请在菜单中重新连接。";
                    ProductDiagnostics.Write("livecaptions.disconnected", exception);
                    continue;
                }

                lock (captureStateLock)
                {
                    if (captureSuspended || observedEpoch != CaptureEpoch ||
                        observedSession != LectureSessionTracker.CurrentSessionId ||
                        !ReferenceEquals(observedWindow, Window))
                        continue;
                    if (lastEpoch != observedEpoch)
                    {
                        lastEpoch = observedEpoch;
                        lastPublishedId = lastQueuedId = null;
                        lastPublishedRevision = lastQueuedRevision = -1;
                        usefulRevisions = 0;
                    }

                    TimeSpan elapsed = observationClock.Elapsed;
                    LiveCaptionUpdate update = captionIdentityResolver.Process(
                        fullText, observationOrigin + elapsed);
                    IReadOnlyList<RecordedCaption> recorded = recordingPolicy.Observe(update, elapsed);
                    LiveCaptionSegment? current = update.CurrentSegment;
                    if (current != null && !string.IsNullOrWhiteSpace(current.Text))
                    {
                        bool isRecorded = recordingPolicy.IsRecorded(current.Id, current.Revision);
                        TranslationTaskIdentity identity = ToIdentity(current, observedEpoch, isRecorded);
                        Caption.BeginCurrentSegment(identity);
                        Caption.OriginalCaption = current.Text;
                        Caption.OverlayOriginalCaption = TextUtil.ShortenDisplaySentence(
                            current.Text, TextUtil.VERYLONG_THRESHOLD);
                        Caption.DisplayOriginalCaption = TextUtil.ShortenDisplaySentence(
                            current.Text, TextUtil.VERYLONG_THRESHOLD);

                        bool changed = lastPublishedId != current.Id || lastPublishedRevision != current.Revision;
                        if (changed)
                        {
                            TranscriptDraftChanged?.Invoke(ToTranscriptSegment(
                                current, null, SegmentState.Draft, observedSession, observedEpoch));
                            lastPublishedId = current.Id;
                            lastPublishedRevision = current.Revision;
                            usefulRevisions++;
                        }

                        // Punctuation candidates are translated immediately in
                        // the live area. Only recording admission makes them final.
                        bool translateLive = !isRecorded &&
                            (update.FinalizedSegments.Any(item => item.Id == current.Id) ||
                             update.DraftIsEligible &&
                             Encoding.UTF8.GetByteCount(current.Text) >= TextUtil.SHORT_THRESHOLD &&
                             (usefulRevisions > Setting.MaxSyncInterval ||
                              update.DraftStableFor >= LiveCaptionSegmentationThresholds.DraftQuietTranslationDelay));
                        if (translateLive &&
                            (lastQueuedId != current.Id || lastQueuedRevision != current.Revision || lastQueuedFinal))
                        {
                            pendingTextQueue.Enqueue(new CaptionTranslationSubmission(
                                identity, current.Text, observedSession));
                            lastQueuedId = current.Id;
                            lastQueuedRevision = current.Revision;
                            lastQueuedFinal = false;
                            usefulRevisions = 0;
                        }
                    }

                    foreach (RecordedCaption record in recorded)
                    {
                        PublishRecordedCaption(record, observedSession, observedEpoch);
                        lastQueuedId = record.Segment.Id;
                        lastQueuedRevision = record.Segment.Revision;
                        lastQueuedFinal = true;
                    }
                }

                if (token.WaitHandle.WaitOne(25))
                    return;
            }
        }

        private static void PublishRecordedCaption(
            RecordedCaption record, long? sessionId, long epoch, bool enqueueTranslation = true)
        {
            if (!sessionId.HasValue)
                return;
            LiveCaptionSegment segment = record.Segment;
            TranslationTaskIdentity identity = ToIdentity(segment, epoch, true, record.IsIncomplete);
            string apiName = LogOnlyFlag ? "LogOnly" : Setting.ApiName;
            string targetLanguage = LogOnlyFlag ? "N/A" : Setting.TargetLanguage;
            // Source persistence is serialized independently of the provider.
            // A canceled/failed translation must not erase an accepted sentence.
            string? readyTranslation = null;
            if (!LogOnlyFlag && provisionalTranslations.TryRemove(
                    (identity.CaptureEpoch, identity.SegmentId, identity.Revision, apiName, targetLanguage),
                    out TranslationQueueResult? cached))
                readyTranslation = cached.TranslatedText;
            recordingPersistenceTail = PersistRecordedSourceAsync(
                recordingPersistenceTail, segment.Text, readyTranslation ?? string.Empty,
                apiName, targetLanguage, sessionId.Value, identity);
            TranscriptSegmentChanged?.Invoke(ToTranscriptSegment(
                segment, readyTranslation,
                readyTranslation == null ? SegmentState.Committed : SegmentState.Translated,
                sessionId, epoch, record.IsIncomplete));
            if (readyTranslation != null)
                ApplyAcceptedContext(new TranslationQueueResult(segment.Text, readyTranslation,
                    true, apiName, sessionId, targetLanguage, identity));
            if (enqueueTranslation && !LogOnlyFlag && readyTranslation == null)
                pendingTextQueue.Enqueue(new CaptionTranslationSubmission(identity, segment.Text, sessionId));
        }

        private static async Task PersistRecordedSourceAsync(
            Task previous, string text, string translatedText, string apiName, string targetLanguage,
            long sessionId, TranslationTaskIdentity identity)
        {
            await previous.ConfigureAwait(false);
            await Log(text, translatedText, apiName: apiName, sessionId: sessionId,
                targetLanguageSnapshot: targetLanguage, identity: identity).ConfigureAwait(false);
        }

        public static Task WaitForRecordingPersistenceAsync()
        {
            lock (captureStateLock)
                return recordingPersistenceTail;
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
                    // The ingress gate serializes enqueue with stop without
                    // holding the source-state lock through queue callbacks.
                    lock (translationIngressLock)
                    {
                        string queuedText = submission.Text;
                        if (Volatile.Read(ref captureSuspended) || LogOnlyFlag ||
                            !CanAcceptTranslation(new TranslationQueueResult(
                                queuedText, string.Empty, submission.Identity.IsFinal,
                                string.Empty, submission.SessionId, string.Empty, submission.Identity)))
                            continue;
                        string apiName = Setting.ApiName;
                        var translateFunction = TranslateAPI.GetFunction(apiName);
                        string targetLanguage = Setting.TargetLanguage;
                        translationTaskQueue.Enqueue(
                            (requestToken, partialOutput) => TranslateOrReuseProvisional(
                                submission,
                                queuedText,
                                apiName,
                                translateFunction,
                                targetLanguage,
                                partialOutput,
                                requestToken),
                            queuedText,
                            apiName,
                            submission.SessionId,
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
                lock (captureStateLock)
                    ApplyDisplayResult(result);
            }
        }

        private static void ApplyDisplayResult(TranslationQueueResult result)
        {
            if (!CanAcceptTranslation(result))
                return;
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
                if (result.IsPartial)
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
                identity.CapturedAt,
                result.SessionId,
                identity.CaptureEpoch,
                identity.IsIncomplete);

            if (identity.IsFinal)
                TranscriptSegmentChanged?.Invoke(segment);
            else
                TranscriptDraftChanged?.Invoke(segment);
        }

        private static TranslationTaskIdentity ToIdentity(
            LiveCaptionSegment segment, long epoch, bool isFinal, bool isIncomplete = false) =>
            new(segment.Id, segment.Sequence, segment.Revision, isFinal,
                segment.CapturedAt, epoch, isIncomplete);

        private static TranscriptSegment ToTranscriptSegment(
            LiveCaptionSegment segment, string? translatedText, SegmentState state,
            long? sessionId, long epoch, bool isIncomplete = false) =>
            new(segment.Id, segment.Sequence, segment.Revision, segment.Text,
                translatedText, state, segment.CapturedAt, sessionId, epoch, isIncomplete);

        private static bool CanAcceptTranslation(TranslationQueueResult result)
        {
            if (result.Identity == null)
                return true;
            lock (captureStateLock)
            {
                return result.Identity.CaptureEpoch == CaptureEpoch &&
                       result.SessionId == LectureSessionTracker.CurrentSessionId &&
                       recordingPolicy.IsCurrentRevision(result.Identity);
            }
        }

        private static void OnAcceptedTranslation(TranslationQueueResult result)
        {
            lock (captureStateLock)
            {
                if (!CanAcceptTranslation(result))
                    return;
                if (result.Identity is { IsFinal: false } identity &&
                    !TranslationTextPolicy.IsProviderNotice(result.TranslatedText))
                {
                    if (provisionalTranslations.Count >= LiveCaptionSegmentationThresholds.RecentLedgerCapacity)
                        provisionalTranslations.Clear();
                    provisionalTranslations[(identity.CaptureEpoch, identity.SegmentId,
                        identity.Revision, result.ApiName, result.TargetLanguage)] = result;
                }
                // Completion events are lossless even if the visual latest-output
                // channel coalesces several fast provider results. Epoch ownership
                // remains locked through context and event effects.
                PublishTranscriptResult(result);
                ApplyAcceptedContext(result);
            }
        }

        private static Task<(string, bool)> TranslateOrReuseProvisional(
            CaptionTranslationSubmission submission, string text, string apiName,
            Func<string, CancellationToken, Task<string>> translateFunction,
            string targetLanguage, Action<string>? partialOutput, CancellationToken token)
        {
            TranslationTaskIdentity identity = submission.Identity;
            if (identity.IsFinal && provisionalTranslations.TryRemove(
                    (identity.CaptureEpoch, identity.SegmentId, identity.Revision, apiName, targetLanguage),
                    out TranslationQueueResult? cached))
                return Task.FromResult((cached.TranslatedText, true));
            return Translate(text, apiName, translateFunction, targetLanguage, partialOutput, token);
        }

        public static async Task<TranslationHistoryChange?> Log(string originalText, string translatedText,
            bool isOverwrite = false, string? apiName = null, long? sessionId = null,
            CancellationToken token = default, string? targetLanguageSnapshot = null,
            TranslationTaskIdentity? identity = null)
        {
            if (identity is { IsFinal: false })
                return null;
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
                    identity?.IsFinal ?? true,
                    identity?.CaptureEpoch ?? 0,
                    identity?.IsIncomplete ?? false);
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
                Interlocked.Exchange(ref historyWriteFailed, 1);
                ProductDiagnostics.Write("history.write-failed", ex);
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
            lock (captureStateLock)
            {
                if (change.SegmentId.HasValue &&
                    (change.CaptureEpoch != CaptureEpoch ||
                     change.Entry.SessionId != LectureSessionTracker.CurrentSessionId))
                    return;
                Caption?.UpsertContext(
                    change.Entry,
                    change.ReplacedEntryId,
                    change.SegmentId);
            }
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

        public static async Task StopCaptureAndFlushAsync(CancellationToken token = default)
        {
            Task sourcePersistence;
            lock (translationIngressLock)
            lock (captureStateLock)
            {
                Volatile.Write(ref captureSuspended, true);
                activeCapturePreparationId = 0;
                preparedCaptureWindow = null;
                long? sessionId = LectureSessionTracker.CurrentSessionId;
                foreach (RecordedCaption record in recordingPolicy.Flush())
                    PublishRecordedCaption(record, sessionId, CaptureEpoch, enqueueTranslation: false);
                sourcePersistence = recordingPersistenceTail;
            }
            await sourcePersistence.WaitAsync(token).ConfigureAwait(false);
            translationTaskQueue.CancelPendingAndActive();
            while (pendingTextQueue.TryDequeue(out _)) { }
            await translationTaskQueue.WaitForIdleAsync(token).ConfigureAwait(false);
        }

        public static async Task SuspendAndResetCaptureAsync(CancellationToken token = default)
        {
            await StopCaptureAndFlushAsync(token).ConfigureAwait(false);
            lock (captureStateLock)
            {
                Interlocked.Increment(ref captureEpoch);
                captionIdentityResolver.Reset();
                recordingPolicy.Reset();
                provisionalTranslations.Clear();
            }
            await segmentPersistence.ClearAsync(token).ConfigureAwait(false);
            ClearCapturePresentation();
        }

        internal static async Task<PreparedCaptureSession>
            PrepareSuspendedCaptureFromBaselineAsync(
                AutomationElement expectedWindow,
                string baselineSnapshot,
                CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(expectedWindow);
            ArgumentNullException.ThrowIfNull(baselineSnapshot);

            await segmentPersistence.ClearAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            long epoch;
            long preparationId;
            lock (translationIngressLock)
            lock (captureStateLock)
            {
                if (!Volatile.Read(ref captureSuspended))
                {
                    throw new InvalidOperationException(
                        "Capture must be suspended before preparing a baseline.");
                }
                if (!ReferenceEquals(expectedWindow, Window))
                {
                    throw new InvalidOperationException(
                        "The Live Captions window changed during capture startup.");
                }

                epoch = Interlocked.Increment(ref captureEpoch);
                captionIdentityResolver.StartFromCurrentSnapshot(
                    baselineSnapshot,
                    observationOrigin + observationClock.Elapsed);
                recordingPolicy.Reset();
                provisionalTranslations.Clear();
                preparationId = Interlocked.Increment(ref capturePreparationSequence);
                activeCapturePreparationId = preparationId;
                preparedCaptureWindow = expectedWindow;
            }

            ClearCapturePresentation();
            return new PreparedCaptureSession(epoch, preparationId);
        }

        internal static bool ResumePreparedCapture(
            PreparedCaptureSession preparation,
            long sessionId,
            AutomationElement expectedWindow)
        {
            lock (captureStateLock)
            {
                if (!Volatile.Read(ref captureSuspended) ||
                    preparation.CaptureEpoch != CaptureEpoch ||
                    preparation.PreparationId != activeCapturePreparationId ||
                    sessionId != LectureSessionTracker.CurrentSessionId ||
                    !ReferenceEquals(expectedWindow, Window) ||
                    !ReferenceEquals(expectedWindow, preparedCaptureWindow))
                {
                    return false;
                }

                activeCapturePreparationId = 0;
                preparedCaptureWindow = null;
                Volatile.Write(ref captureSuspended, false);
                return true;
            }
        }

        internal static void CancelPreparedCapture(PreparedCaptureSession? preparation)
        {
            if (preparation == null)
                return;

            lock (captureStateLock)
            {
                if (preparation.CaptureEpoch != CaptureEpoch ||
                    preparation.PreparationId != activeCapturePreparationId)
                {
                    return;
                }

                activeCapturePreparationId = 0;
                preparedCaptureWindow = null;
            }
        }

        public static void ResumeCapture()
        {
            lock (captureStateLock)
            {
                // Resolver reset seeds the first snapshot, excluding words left
                // on screen from a previous class or a rebuilt source window.
                captionIdentityResolver.Reset();
                recordingPolicy.Reset();
                activeCapturePreparationId = 0;
                preparedCaptureWindow = null;
                Volatile.Write(ref captureSuspended, false);
            }
        }

        private static void ClearCapturePresentation()
        {
            ClearContexts();
            if (Caption == null)
                return;

            Caption.ClearCurrentSegment();
            Caption.OriginalCaption = string.Empty;
            Caption.TranslatedCaption = string.Empty;
            Caption.DisplayOriginalCaption = string.Empty;
            Caption.DisplayTranslatedCaption = string.Empty;
            Caption.OverlayOriginalCaption = " ";
            Caption.OverlayNoticePrefix = " ";
            Caption.OverlayCurrentTranslation = " ";
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
