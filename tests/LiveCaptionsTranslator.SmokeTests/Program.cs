using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using LiveCaptionsTranslator.controls;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.services;
using LiveCaptionsTranslator.services.recognition;
using LiveCaptionsTranslator.utils;
using LiveCaptionsTranslator.viewmodels;

using NAudio.Wave;
using NAudio.CoreAudioApi;

AutomationElement? liveCaptionsWindow = null;
int? liveCaptionsProcessId = null;
int exitCode = 0;
MicrophoneCaptionState? initialMicrophoneState = null;
bool exerciseSystemAudio = args.Contains(
    "--system-audio",
    StringComparer.OrdinalIgnoreCase) ||
    args.Contains("--multi-voice-audio", StringComparer.OrdinalIgnoreCase);
bool exerciseMultipleVoices = args.Contains(
    "--multi-voice-audio",
    StringComparer.OrdinalIgnoreCase);

string? localAsrWavModelRoot = ReadOptionValue(args, "--local-asr-wav");
if (localAsrWavModelRoot != null)
{
    string? wavPath = ReadOptionValue(args, "--wav");
    bool simulate48Khz = args.Contains(
        "--simulate-48k",
        StringComparer.OrdinalIgnoreCase);
    return await RunLocalAsrWavSmoke(
        localAsrWavModelRoot,
        wavPath,
        simulate48Khz);
}

string? localAsrSystemModelRoot = ReadOptionValue(
    args,
    "--local-asr-system-audio");
if (localAsrSystemModelRoot != null)
{
    return await RunLocalAsrSystemAudioSmoke(localAsrSystemModelRoot);
}

string? localAsrMicrophoneModelRoot = ReadOptionValue(
    args,
    "--local-asr-microphone");
if (localAsrMicrophoneModelRoot != null)
{
    return await RunLocalAsrMicrophoneStartSmoke(localAsrMicrophoneModelRoot);
}

if (args.Contains("--inspect-existing", StringComparer.OrdinalIgnoreCase))
{
    return InspectExistingLiveCaptions();
}

if (args.Contains("--timeline-ui", StringComparer.OrdinalIgnoreCase))
{
    return RunTimelineUiSmoke();
}

if (args.Contains("--exercise-existing", StringComparer.OrdinalIgnoreCase))
{
    return ExerciseExistingLiveCaptions();
}

if (args.Contains("--prepare-existing", StringComparer.OrdinalIgnoreCase))
{
    return PrepareExistingLiveCaptions();
}

if (Process.GetProcessesByName(LiveCaptionsHandler.PROCESS_NAME).Length != 0)
{
    Console.WriteLine("SmokeTestSkipped=ExistingLiveCaptions");
    return 4;
}

try
{
    liveCaptionsWindow = LiveCaptionsHandler.LaunchLiveCaptions();
    liveCaptionsProcessId = liveCaptionsWindow.Current.ProcessId;
    if (exerciseSystemAudio)
    {
        bool sourcePrepared = LiveCaptionsMicrophoneProbe
            .EnsureCaptureStartedAfterUserAction(liveCaptionsWindow);
        LiveCaptionsSnapshotReadResult preSpeechBaseline = sourcePrepared
            ? LiveCaptionsHandler.ReadCaptionSnapshot(
                liveCaptionsWindow,
                refreshNode: true,
                TimeSpan.FromSeconds(3))
            : new(false, string.Empty, "caption-source-not-prepared");
        Console.WriteLine($"PreSpeechSourcePrepared={sourcePrepared}");
        Console.WriteLine($"PreSpeechBaselineReadable={preSpeechBaseline.IsReadable}");
        Console.WriteLine($"PreSpeechBaselineEmpty={preSpeechBaseline.IsReadable && preSpeechBaseline.Text.Length == 0}");
        Console.WriteLine($"PreSpeechBaselineKind={preSpeechBaseline.Kind}");
        Console.WriteLine($"PreSpeechBaselineErrorCode={preSpeechBaseline.ErrorCode ?? "none"}");
        if (!preSpeechBaseline.IsReadable)
            exitCode = 11;

        bool systemAudioDetected = ExerciseSystemAudio(
            liveCaptionsWindow,
            exerciseMultipleVoices,
            out int initialCharacterCount,
            out int finalCharacterCount,
            out int installedVoiceCount,
            out int snapshotFrames,
            out int revisionEvents,
            out int logicalIdentities);
        Console.WriteLine($"SystemAudioInitialCharacterCount={initialCharacterCount}");
        Console.WriteLine($"SystemAudioFinalCharacterCount={finalCharacterCount}");
        Console.WriteLine($"SystemAudioCaptionDetected={systemAudioDetected}");
        Console.WriteLine($"SystemAudioInstalledVoiceCount={installedVoiceCount}");
        Console.WriteLine($"SystemAudioSnapshotFrames={snapshotFrames}");
        Console.WriteLine($"SystemAudioRevisionEvents={revisionEvents}");
        Console.WriteLine($"SystemAudioLogicalIdentities={logicalIdentities}");
        Console.WriteLine($"SystemAudioMultipleVoices={exerciseMultipleVoices}");
        if (!systemAudioDetected)
            exitCode = 7;
        if (exerciseMultipleVoices && installedVoiceCount < 2)
            exitCode = exitCode == 0 ? 14 : exitCode;
    }

    MicrophoneProbeResult result = LiveCaptionsMicrophoneProbe.ReadState(liveCaptionsWindow);
    initialMicrophoneState = result.State;

    Console.WriteLine($"MicrophoneState={result.State}");
    Console.WriteLine($"AutomationAvailable={result.AutomationAvailable}");
    Console.WriteLine($"ErrorCode={result.ErrorCode ?? "none"}");
    if (!result.AutomationAvailable)
    {
        exitCode = exitCode == 0 ? 2 : exitCode;
    }
    else
    {
        MicrophoneProbeResult enabled = LiveCaptionsMicrophoneProbe.EnableAfterUserAction(liveCaptionsWindow);
        Console.WriteLine($"EnableState={enabled.State}");
        Console.WriteLine($"EnableChangedByRequest={enabled.ChangedByRequest}");
        Console.WriteLine($"EnableErrorCode={enabled.ErrorCode ?? "none"}");
        if (enabled.State != MicrophoneCaptionState.On)
        {
            exitCode = exitCode == 0 ? 5 : exitCode;
        }
        else
        {
            LiveCaptionsSnapshotReadResult rebound =
                LiveCaptionsHandler.ReadCaptionSnapshot(
                    liveCaptionsWindow,
                    refreshNode: true,
                    TimeSpan.FromSeconds(3));
            Console.WriteLine($"PostEnableCaptionNodeReadable={rebound.IsReadable}");
            Console.WriteLine($"PostEnableCaptionSurfaceEmpty={rebound.IsReadable && rebound.Text.Length == 0}");
            Console.WriteLine($"PostEnableCaptionSnapshotKind={rebound.Kind}");
            Console.WriteLine($"PostEnableCaptionNodeErrorCode={rebound.ErrorCode ?? "none"}");
            if (!rebound.IsReadable)
                exitCode = exitCode == 0 ? 8 : exitCode;

            if (initialMicrophoneState == MicrophoneCaptionState.Off)
            {
                MicrophoneProbeResult restored =
                    LiveCaptionsMicrophoneProbe.DisableAfterUserAction(liveCaptionsWindow);
                Console.WriteLine($"RestoreState={restored.State}");
                Console.WriteLine($"RestoreErrorCode={restored.ErrorCode ?? "none"}");
                if (restored.State != MicrophoneCaptionState.Off)
                    exitCode = exitCode == 0 ? 6 : exitCode;
            }
        }
    }
}
finally
{
    if (liveCaptionsWindow != null)
    {
        if (initialMicrophoneState == MicrophoneCaptionState.Off &&
            LiveCaptionsMicrophoneProbe.WasEnabledByCurrentApp)
        {
            LiveCaptionsMicrophoneProbe.DisableAfterUserAction(liveCaptionsWindow);
        }

        LiveCaptionsHandler.KillLiveCaptions(liveCaptionsWindow);

        if (liveCaptionsProcessId.HasValue)
        {
            try
            {
                using var process = Process.GetProcessById(liveCaptionsProcessId.Value);
                if (!process.WaitForExit(5000))
                {
                    Console.WriteLine("CleanupConfirmed=False");
                    exitCode = 3;
                }
            }
            catch (ArgumentException)
            {
            }
        }
    }
}

Console.WriteLine($"CleanupConfirmed={exitCode != 3}");
return exitCode;

static string? ReadOptionValue(string[] arguments, string option)
{
    int index = Array.FindIndex(
        arguments,
        item => string.Equals(item, option, StringComparison.OrdinalIgnoreCase));
    if (index < 0)
        return null;
    if (index + 1 >= arguments.Length ||
        arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
    {
        throw new ArgumentException($"{option} requires a value.");
    }

    return arguments[index + 1];
}

static async Task<int> RunLocalAsrWavSmoke(
    string modelRoot,
    string? wavPath,
    bool simulate48Khz)
{
    try
    {
        LocalAsrModelFiles model = LocalAsrModelLocator.Resolve(modelRoot);
        string validationWav = string.IsNullOrWhiteSpace(wavPath)
            ? Path.Combine(model.ModelDirectory, "validation-wavs", "0.wav")
            : Path.GetFullPath(wavPath);
        if (!File.Exists(validationWav))
            throw new FileNotFoundException("Local ASR validation WAV is missing.");

        await using ILocalAsrRecognizer recognizer =
            new SherpaLocalAsrRecognizer(model);
        using var reader = new WaveFileReader(validationWav);
        LocalAudioFormat format = LocalAudioSampleConverter.Describe(
            reader.WaveFormat);
        int chunkBytes = Math.Max(
            format.BlockAlign,
            format.SampleRate * format.BlockAlign / 10);
        chunkBytes -= chunkBytes % format.BlockAlign;
        var buffer = new byte[chunkBytes];
        var session = new LocalRecognitionSession();
        session.BeginSession(newSessionId: 1, newCaptureEpoch: 1);
        var events = new List<RecognitionEvent>();

        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            float[] samples = LocalAudioSampleConverter.ConvertToMonoFloat(
                buffer.AsSpan(0, read),
                format);
            int sourceSampleRate = format.SampleRate;
            if (simulate48Khz)
            {
                samples = samples
                    .SelectMany(sample => new[] { sample, sample, sample })
                    .ToArray();
                sourceSampleRate *= 3;
            }
            foreach (LocalAsrHypothesis hypothesis in
                     recognizer.Process(sourceSampleRate, samples))
            {
                RecognitionAcceptance accepted = session.Accept(
                    new RecognitionUpdate(
                        1,
                        1,
                        hypothesis.SourceRevision,
                        DateTimeOffset.UtcNow,
                        hypothesis.Text,
                        hypothesis.IsFinal,
                        hypothesis.EndpointReason));
                if (accepted.IsAccepted)
                    events.Add(accepted.Event!);
            }
        }

        LocalAsrHypothesis? tail = recognizer.Flush();
        if (tail != null)
        {
            RecognitionAcceptance accepted = session.Accept(
                new RecognitionUpdate(
                    1,
                    1,
                    tail.SourceRevision,
                    DateTimeOffset.UtcNow,
                    tail.Text,
                    tail.IsFinal,
                    tail.EndpointReason));
            if (accepted.IsAccepted)
                events.Add(accepted.Event!);
        }

        RecognitionAcceptance stop = session.Stop(finalizeUnfinished: true);
        if (stop.IsAccepted)
            events.Add(stop.Event!);

        int finalCount = events.Count(item => item.IsFinal);
        int identityCount = events
            .Select(item => item.SegmentId)
            .Distinct()
            .Count();
        Console.WriteLine("LocalAsrModelLoaded=True");
        Console.WriteLine($"LocalAsrWavEvents={events.Count}");
        Console.WriteLine($"LocalAsrWavFinals={finalCount}");
        Console.WriteLine($"LocalAsrWavIdentities={identityCount}");
        Console.WriteLine($"LocalAsrWavSimulated48Khz={simulate48Khz}");
        Console.WriteLine($"LocalAsrWavDroppedFrames=0");
        return finalCount > 0 && identityCount > 0 ? 0 : 21;
    }
    catch (Exception exception)
    {
        Console.WriteLine($"LocalAsrWavFailure={exception.GetType().Name}");
        return 20;
    }
}

static async Task<int> RunLocalAsrSystemAudioSmoke(string modelRoot)
{
    string stage = "initialize";
    try
    {
        stage = "resolve-validation-audio";
        LocalAsrModelFiles model = LocalAsrModelLocator.Resolve(modelRoot);
        string[] validationWavs =
        [
            Path.Combine(model.ModelDirectory, "validation-wavs", "0.wav"),
            Path.Combine(model.ModelDirectory, "validation-wavs", "1.wav")
        ];
        if (validationWavs.Any(path => !File.Exists(path)))
            throw new FileNotFoundException("Local ASR validation audio is missing.");

        stage = "start-capture";
        var events = new List<RecognitionEvent>();
        var errors = new List<LocalAsrError>();
        await using var service = new LocalAsrCaptureService(
            new WasapiLocalAudioCaptureFactory(
                includeCurrentProcessAudio: true),
            new SherpaLocalAsrRecognizerFactory());
        await service.StartAsync(
            sessionId: 2,
            captureEpoch: 2,
            LocalAsrAudioSource.SystemAudio,
            modelRoot,
            item =>
            {
                lock (events)
                    events.Add(item);
            },
            error =>
            {
                lock (errors)
                    errors.Add(error);
            });
        LocalAudioFormat? captureFormat = service.ActiveFormat;

        using MMDevice renderDevice =
            WasapiLoopbackCapture.GetDefaultLoopbackCaptureDevice();
        for (int index = 0; index < validationWavs.Length; index++)
        {
            stage = $"play-validation-audio-{index + 1}";
            await PlayWaveOnDeviceAsync(validationWavs[index], renderDevice);
            await Task.Delay(TimeSpan.FromMilliseconds(1600));
        }

        await Task.Delay(TimeSpan.FromMilliseconds(2600));
        stage = "stop-capture";
        await service.StopAsync(finalizeUnfinished: true);

        RecognitionEvent[] snapshot;
        LocalAsrError[] errorSnapshot;
        lock (events)
            snapshot = events.ToArray();
        lock (errors)
            errorSnapshot = errors.ToArray();
        int finalCount = snapshot.Count(item => item.IsFinal);
        int identityCount = snapshot
            .Where(item => item.IsFinal)
            .Select(item => item.SegmentId)
            .Distinct()
            .Count();
        bool fatalError = errorSnapshot.Any(error => error.Code is
            "recognition-worker-failed" or "audio-capture-stopped");
        Console.WriteLine($"LocalAsrSystemAudioFiles={validationWavs.Length}");
        Console.WriteLine(
            $"LocalAsrSystemFormat={captureFormat?.SampleRate}/" +
            $"{captureFormat?.Channels}/{captureFormat?.BitsPerSample}/" +
            $"{captureFormat?.Encoding}");
        Console.WriteLine($"LocalAsrSystemEvents={snapshot.Length}");
        Console.WriteLine($"LocalAsrSystemFinals={finalCount}");
        Console.WriteLine($"LocalAsrSystemIdentities={identityCount}");
        Console.WriteLine($"LocalAsrSystemDroppedFrames={service.DroppedFrames}");
        Console.WriteLine($"LocalAsrSystemProcessedSamples={service.ProcessedSamples}");
        Console.WriteLine($"LocalAsrSystemNonSilentSamples={service.NonSilentSamples}");
        Console.WriteLine($"LocalAsrSystemPeakLevel={service.PeakLevel:F6}");
        Console.WriteLine($"LocalAsrSystemErrors={errorSnapshot.Length}");
        return finalCount >= 1 && identityCount >= 1 && !fatalError ? 0 : 23;
    }
    catch (Exception exception)
    {
        Console.WriteLine($"LocalAsrSystemFailureStage={stage}");
        Console.WriteLine($"LocalAsrSystemFailure={exception.GetType().Name}");
        return 22;
    }
}

static async Task PlayWaveOnDeviceAsync(string path, MMDevice renderDevice)
{
    using var reader = new WaveFileReader(path);
    using var output = new WasapiOut(
        renderDevice,
        AudioClientShareMode.Shared,
        useEventSync: false,
        latency: 100);
    var stopped = new TaskCompletionSource(
        TaskCreationOptions.RunContinuationsAsynchronously);
    output.PlaybackStopped += (_, args) =>
    {
        if (args.Exception == null)
            stopped.TrySetResult();
        else
            stopped.TrySetException(args.Exception);
    };
    output.Init(reader);
    output.Play();
    await stopped.Task.WaitAsync(TimeSpan.FromSeconds(30));
}

static async Task<int> RunLocalAsrMicrophoneStartSmoke(string modelRoot)
{
    string stage = "initialize";
    try
    {
        var errors = new List<LocalAsrError>();
        await using var service = new LocalAsrCaptureService();
        stage = "start-capture";
        await service.StartAsync(
            sessionId: 3,
            captureEpoch: 3,
            LocalAsrAudioSource.Microphone,
            modelRoot,
            _ => { },
            error =>
            {
                lock (errors)
                    errors.Add(error);
            });
        LocalAudioFormat? format = service.ActiveFormat;
        stage = "observe-capture";
        await Task.Delay(TimeSpan.FromSeconds(2));
        stage = "stop-capture";
        await service.StopAsync(finalizeUnfinished: false);

        LocalAsrError[] snapshot;
        lock (errors)
            snapshot = errors.ToArray();
        bool fatalError = snapshot.Any(error => error.Code is
            "recognition-worker-failed" or "audio-capture-stopped");
        Console.WriteLine("LocalAsrMicrophoneStarted=True");
        Console.WriteLine(
            $"LocalAsrMicrophoneFormat={format?.SampleRate}/" +
            $"{format?.Channels}/{format?.BitsPerSample}/{format?.Encoding}");
        Console.WriteLine($"LocalAsrMicrophoneProcessedSamples={service.ProcessedSamples}");
        Console.WriteLine($"LocalAsrMicrophoneDroppedFrames={service.DroppedFrames}");
        Console.WriteLine($"LocalAsrMicrophoneErrors={snapshot.Length}");
        return !fatalError && service.ProcessedSamples > 0 ? 0 : 25;
    }
    catch (Exception exception)
    {
        Console.WriteLine($"LocalAsrMicrophoneFailureStage={stage}");
        Console.WriteLine($"LocalAsrMicrophoneFailure={exception.GetType().Name}");
        Console.WriteLine($"LocalAsrMicrophoneFailureHResult=0x{exception.HResult:X8}");
        return 24;
    }
}

static bool ExerciseSystemAudio(
    AutomationElement liveCaptionsWindow,
    bool useMultipleVoices,
    out int initialCharacterCount,
    out int finalCharacterCount,
    out int installedVoiceCount,
    out int snapshotFrames,
    out int revisionEvents,
    out int logicalIdentities)
{
    initialCharacterCount = ReadCaptionCharacterCount(liveCaptionsWindow);
    finalCharacterCount = initialCharacterCount;
    installedVoiceCount = 0;
    snapshotFrames = 0;
    revisionEvents = 0;
    logicalIdentities = 0;

    if (!LiveCaptionsMicrophoneProbe.EnsureCaptureStartedAfterUserAction(
            liveCaptionsWindow))
    {
        return false;
    }

    object? primaryVoice = null;
    object? secondaryVoice = null;
    object? installedVoices = null;
    object? primaryToken = null;
    object? secondaryToken = null;
    try
    {
        Type? voiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
        if (voiceType == null)
            return false;

        primaryVoice = Activator.CreateInstance(voiceType);
        if (primaryVoice == null)
            return false;

        dynamic primary = primaryVoice;
        installedVoices = primary.GetVoices();
        dynamic voices = installedVoices;
        installedVoiceCount = Convert.ToInt32(voices.Count);
        if (installedVoiceCount > 0)
        {
            primaryToken = voices.Item(0);
            primary.Voice = primaryToken;
        }

        if (useMultipleVoices && installedVoiceCount >= 2)
        {
            secondaryVoice = Activator.CreateInstance(voiceType);
            if (secondaryVoice == null)
                return false;
            dynamic secondary = secondaryVoice;
            secondaryToken = voices.Item(1);
            secondary.Voice = secondaryToken;
        }

        var resolver = new LiveCaptionIdentityResolver(splitLongDrafts: false);
        resolver.StartFromCurrentSnapshot(ReadCaptionText(liveCaptionsWindow));
        var observedIdentities = new HashSet<Guid>();
        string previousSnapshot = string.Empty;

        dynamic firstSpeaker = primaryVoice;
        firstSpeaker.Speak(
            "The first speaker introduces the topic. The first speaker explains one result. " +
            "The first speaker closes with a short summary.",
            1);
        if (secondaryVoice != null)
        {
            Thread.Sleep(300);
            dynamic secondSpeaker = secondaryVoice;
            secondSpeaker.Speak(
                "The second speaker adds another example. The second speaker compares the evidence. " +
                "The second speaker confirms the final condition.",
                1);
        }

        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(12))
        {
            string snapshot = ReadCaptionText(liveCaptionsWindow);
            finalCharacterCount = snapshot.Length;
            if (!string.Equals(snapshot, previousSnapshot, StringComparison.Ordinal))
            {
                snapshotFrames++;
                LiveCaptionUpdate update = resolver.Process(
                    snapshot,
                    DateTimeOffset.UtcNow);
                revisionEvents += update.FinalizedSegments.Count;
                foreach (LiveCaptionSegment segment in update.FinalizedSegments)
                    observedIdentities.Add(segment.Id);
                previousSnapshot = snapshot;
            }

            Thread.Sleep(75);
        }

        logicalIdentities = observedIdentities.Count;
        return finalCharacterCount > initialCharacterCount;
    }
    finally
    {
        ReleaseComObject(secondaryToken);
        ReleaseComObject(primaryToken);
        ReleaseComObject(installedVoices);
        ReleaseComObject(secondaryVoice);
        ReleaseComObject(primaryVoice);
    }
}

static string ReadCaptionText(AutomationElement liveCaptionsWindow)
{
    try
    {
        return FindRawDescendant(liveCaptionsWindow, "CaptionsTextBlock")
            ?.Current.Name ?? string.Empty;
    }
    catch (Exception exception) when (
        exception is ElementNotAvailableException or InvalidOperationException or COMException)
    {
        return string.Empty;
    }
}

static void ReleaseComObject(object? value)
{
    if (value != null && Marshal.IsComObject(value))
        Marshal.FinalReleaseComObject(value);
}

static int ReadCaptionCharacterCount(AutomationElement liveCaptionsWindow)
{
    try
    {
        return FindRawDescendant(liveCaptionsWindow, "CaptionsTextBlock")
            ?.Current.Name?.Length ?? 0;
    }
    catch (ElementNotAvailableException)
    {
        return 0;
    }
}

static int RunTimelineUiSmoke()
{
    int exitCode = 13;
    Exception? failure = null;
    var finished = new ManualResetEventSlim();
    var thread = new Thread(() =>
    {
        System.Windows.Application? application = null;
        Window? window = null;
        try
        {
            application = new System.Windows.Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "/LectureCopilot.Dev;component/src/styles/LectureTheme.xaml",
                    UriKind.Relative)
            });

            var viewModel = new TranscriptSessionViewModel();
            DateTimeOffset startedAt = DateTimeOffset.Now;
            for (int index = 0; index < 90; index++)
            {
                viewModel.ApplySegment(CreateUiSmokeSegment(
                    index,
                    startedAt,
                    $"Source sentence {index} keeps the transcript ordered.",
                    $"第 {index} 条译文用于验证真实时间线控件。"));
            }

            var timeline = new TranscriptTimeline
            {
                DataContext = viewModel
            };
            window = new Window
            {
                Width = 900,
                Height = 620,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = timeline
            };
            window.Show();
            PumpDispatcher();

            var list = timeline.FindName("TimelineList") as ListBox
                ?? throw new InvalidOperationException("Timeline list was not created.");
            var scroller = FindVisualDescendant<ScrollViewer>(list)
                ?? throw new InvalidOperationException("Timeline scroll viewer was not created.");

            bool initiallyAtBottom = DistanceFromBottom(scroller) <= 2;
            var draftPanel = timeline.FindName("DraftPanel") as FrameworkElement
                ?? throw new InvalidOperationException("Live caption panel was not created.");
            double initialLivePanelHeight = draftPanel.ActualHeight;

            scroller.ScrollToVerticalOffset(scroller.ScrollableHeight * 0.45);
            PumpDispatcher();
            RaiseUpwardWheel(list);
            PumpDispatcher();
            var beforeAnchor = ReadFirstVisibleAnchor(list, scroller);
            if (beforeAnchor.Id == Guid.Empty)
                throw new InvalidOperationException("No visible reading anchor was found.");

            viewModel.ApplySegment(CreateUiSmokeSegment(
                90,
                startedAt,
                "A new live sentence arrives while the user is reviewing history.",
                "用户浏览历史时，新字幕到达。"));
            PumpDispatcher();
            var afterArrivalAnchor = ReadFirstVisibleAnchor(list, scroller);
            bool queuedFollowCancelled =
                viewModel.IsBrowsingHistory &&
                beforeAnchor.Id == afterArrivalAnchor.Id &&
                Math.Abs(beforeAnchor.Top - afterArrivalAnchor.Top) <= 1.5;

            int anchorIndex = viewModel.Segments
                .Select((segment, index) => (segment, index))
                .First(pair => pair.segment.Id == beforeAnchor.Id)
                .index;
            int precedingIndex = Math.Max(0, anchorIndex - 1);
            TranscriptSegment preceding = viewModel.Segments[precedingIndex].Snapshot;
            viewModel.ApplySegment(preceding with
            {
                Revision = preceding.Revision + 1,
                TranslatedText = string.Join(' ', Enumerable.Repeat(
                    "这段扩展译文会改变上方条目的高度，但不应移动当前阅读锚点。",
                    8))
            });
            PumpDispatcher();
            var afterHeightChangeAnchor = ReadFirstVisibleAnchor(list, scroller);
            bool anchorHeldAfterHeightChange =
                beforeAnchor.Id == afterHeightChangeAnchor.Id &&
                Math.Abs(beforeAnchor.Top - afterHeightChangeAnchor.Top) <= 1.5;

            Guid draftId = Guid.NewGuid();
            var liveDraft = new TranscriptSegment(
                draftId,
                91,
                0,
                string.Join(' ', Enumerable.Repeat(
                    "A long draft remains fully scrollable without taking over the viewport.",
                    30)),
                string.Join(' ', Enumerable.Repeat(
                    "长草稿保留全文，并限制面板高度。",
                    30)),
                SegmentState.Draft,
                startedAt.AddSeconds(91));
            viewModel.SetDraft(liveDraft);
            PumpDispatcher();
            bool draftHeightBounded = draftPanel.ActualHeight <= 190.5;
            var afterLiveDraftAnchor = ReadFirstVisibleAnchor(list, scroller);
            bool liveDraftPreservesViewport =
                Math.Abs(draftPanel.ActualHeight - initialLivePanelHeight) <= 0.5 &&
                beforeAnchor.Id == afterLiveDraftAnchor.Id &&
                Math.Abs(beforeAnchor.Top - afterLiveDraftAnchor.Top) <= 1.5;

            application.Resources["LectureSubtitleFontSize"] = 28d;
            window.Width = 700;
            window.Height = 500;
            PumpDispatcher();
            bool resizedWithoutOverflow =
                draftPanel.ActualHeight <= 190.5 &&
                scroller.ViewportHeight > 0;

            TranscriptSegment finalizedLive = liveDraft with
            {
                Revision = 1,
                SourceText = "The final sentence remains visible in the live caption area.",
                TranslatedText = "完整句进入课堂记录后，实时区域仍保留最后一句。",
                State = SegmentState.Translated
            };
            viewModel.ApplySegment(finalizedLive);
            viewModel.ClearDraft(finalizedLive.Id, finalizedLive.Revision);
            PumpDispatcher();
            bool liveFinalRemainsVisible =
                viewModel.HasLiveCaption &&
                viewModel.LiveText == finalizedLive.SourceText &&
                Math.Abs(draftPanel.ActualHeight - initialLivePanelHeight) <= 0.5;

            timeline.ReturnToLive();
            PumpDispatcher();
            bool returnedToLive =
                viewModel.IsFollowingLive &&
                DistanceFromBottom(scroller) <= 2;

            Console.WriteLine($"TimelineInitiallyAtBottom={initiallyAtBottom}");
            Console.WriteLine($"TimelineQueuedFollowCancelled={queuedFollowCancelled}");
            Console.WriteLine($"TimelineAnchorHeldAfterHeightChange={anchorHeldAfterHeightChange}");
            Console.WriteLine($"TimelineDraftHeight={draftPanel.ActualHeight:F1}");
            Console.WriteLine($"TimelineDraftHeightBounded={draftHeightBounded}");
            Console.WriteLine($"TimelineLiveDraftPreservesViewport={liveDraftPreservesViewport}");
            Console.WriteLine($"TimelineLiveFinalRemainsVisible={liveFinalRemainsVisible}");
            Console.WriteLine($"TimelineResizeAndFontChange={resizedWithoutOverflow}");
            Console.WriteLine($"TimelineReturnedToLive={returnedToLive}");

            exitCode = initiallyAtBottom &&
                       queuedFollowCancelled &&
                       anchorHeldAfterHeightChange &&
                       draftHeightBounded &&
                       liveDraftPreservesViewport &&
                       liveFinalRemainsVisible &&
                       resizedWithoutOverflow &&
                       returnedToLive
                ? 0
                : 14;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            window?.Close();
            application?.Shutdown();
            finished.Set();
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!finished.Wait(TimeSpan.FromSeconds(30)))
    {
        Console.WriteLine("TimelineSmokeTimedOut=True");
        return 15;
    }
    thread.Join(TimeSpan.FromSeconds(2));

    if (failure != null)
    {
        Console.WriteLine($"TimelineSmokeFailure={failure.GetType().Name}");
        Console.WriteLine($"TimelineSmokeFailureMessage={failure.Message}");
        return 13;
    }

    return exitCode;
}

static TranscriptSegment CreateUiSmokeSegment(
    int sequence,
    DateTimeOffset startedAt,
    string source,
    string translation)
{
    return new TranscriptSegment(
        Guid.NewGuid(),
        sequence,
        0,
        source,
        translation,
        SegmentState.Translated,
        startedAt.AddSeconds(sequence));
}

static void RaiseUpwardWheel(ListBox list)
{
    var wheel = new MouseWheelEventArgs(
        Mouse.PrimaryDevice,
        Environment.TickCount,
        120)
    {
        RoutedEvent = UIElement.PreviewMouseWheelEvent,
        Source = list
    };
    list.RaiseEvent(wheel);
}

static (Guid Id, double Top) ReadFirstVisibleAnchor(
    ListBox list,
    ScrollViewer scroller)
{
    for (int index = 0; index < list.Items.Count; index++)
    {
        if (list.ItemContainerGenerator.ContainerFromIndex(index)
            is not ListBoxItem container)
        {
            continue;
        }

        double top = container.TransformToAncestor(scroller)
            .Transform(new Point(0, 0)).Y;
        if (top + container.ActualHeight < 0)
            continue;

        if (list.Items[index] is TranscriptSegmentViewModel segment)
            return (segment.Id, top);
    }

    return (Guid.Empty, double.NaN);
}

static double DistanceFromBottom(ScrollViewer scroller)
{
    return Math.Max(
        0,
        scroller.ExtentHeight - scroller.ViewportHeight - scroller.VerticalOffset);
}

static void PumpDispatcher()
{
    Dispatcher.CurrentDispatcher.Invoke(
        DispatcherPriority.ApplicationIdle,
        new Action(() => { }));
}

static T? FindVisualDescendant<T>(DependencyObject parent)
    where T : DependencyObject
{
    for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
    {
        DependencyObject child = VisualTreeHelper.GetChild(parent, index);
        if (child is T match)
            return match;

        T? descendant = FindVisualDescendant<T>(child);
        if (descendant != null)
            return descendant;
    }

    return null;
}

static int InspectExistingLiveCaptions()
{
    AutomationElement? window = FindExistingLiveCaptionsWindow(
        out bool windowWasInAutomationRoot);
    if (window == null)
    {
        Console.WriteLine("ExistingLiveCaptionsWindow=False");
        return 8;
    }

    AutomationElement? captions = FindRawDescendant(window, "CaptionsTextBlock");
    AutomationElement? settings = FindRawDescendant(window, "SettingsButton");
    AutomationElement? continueButton = FindRawDescendant(window, "ContinueButton");
    AutomationElement? microphone = FindRawDescendant(window, "MicrophoneMenuFlyoutItem");
    string microphoneState = "Unavailable";
    if (microphone != null &&
        microphone.TryGetCurrentPattern(TogglePattern.Pattern, out object pattern) &&
        pattern is TogglePattern toggle)
    {
        microphoneState = toggle.Current.ToggleState.ToString();
    }

    Console.WriteLine("ExistingLiveCaptions=True");
    Console.WriteLine("WindowAvailable=True");
    Console.WriteLine($"WindowInAutomationRoot={windowWasInAutomationRoot}");
    Console.WriteLine($"WindowOffscreen={window.Current.IsOffscreen}");
    nint nativeHandle = new((long)window.Current.NativeWindowHandle);
    if (SmokeNativeMethods.GetWindowRect(nativeHandle, out SmokeWindowRect rect))
    {
        Console.WriteLine($"WindowLeft={rect.Left}");
        Console.WriteLine($"WindowTop={rect.Top}");
        Console.WriteLine($"WindowWidth={rect.Right - rect.Left}");
        Console.WriteLine($"WindowHeight={rect.Bottom - rect.Top}");
    }
    Console.WriteLine($"CaptionsNodeAvailable={captions != null}");
    Console.WriteLine($"CaptionsCharacterCount={captions?.Current.Name?.Length ?? 0}");
    Console.WriteLine($"SettingsNodeAvailable={settings != null}");
    Console.WriteLine($"SettingsNodeOffscreen={ReadOffscreen(settings)}");
    Console.WriteLine($"ContinueNodeAvailable={continueButton != null}");
    Console.WriteLine($"ContinueNodeOffscreen={ReadOffscreen(continueButton)}");
    Console.WriteLine($"ContinueNodeEnabled={ReadEnabled(continueButton)}");
    Console.WriteLine($"MicrophoneNodeAvailable={microphone != null}");
    Console.WriteLine($"MicrophoneState={microphoneState}");
    return captions != null && settings != null ? 0 : 9;
}

static int ExerciseExistingLiveCaptions()
{
    AutomationElement? window = FindExistingLiveCaptionsWindow(out _);
    if (window == null)
    {
        Console.WriteLine("ExistingLiveCaptionsWindow=False");
        return 8;
    }

    MicrophoneProbeResult initial = LiveCaptionsMicrophoneProbe.ReadState(window);
    Console.WriteLine($"InitialMicrophoneState={initial.State}");
    Console.WriteLine($"InitialAutomationAvailable={initial.AutomationAvailable}");
    MicrophoneProbeResult enabled =
        LiveCaptionsMicrophoneProbe.EnableAfterUserAction(window);
    Console.WriteLine($"EnableState={enabled.State}");
    Console.WriteLine($"EnableErrorCode={enabled.ErrorCode ?? "none"}");

    int inspectionExit = InspectExistingLiveCaptions();
    if (initial.State == MicrophoneCaptionState.Off &&
        LiveCaptionsMicrophoneProbe.WasEnabledByCurrentApp)
    {
        MicrophoneProbeResult restored =
            LiveCaptionsMicrophoneProbe.DisableAfterUserAction(window);
        Console.WriteLine($"RestoreState={restored.State}");
    }

    return enabled.State == MicrophoneCaptionState.On && inspectionExit == 0
        ? 0
        : 10;
}

static int PrepareExistingLiveCaptions()
{
    AutomationElement? window = FindExistingLiveCaptionsWindow(out _);
    if (window == null)
    {
        Console.WriteLine("ExistingLiveCaptionsWindow=False");
        return 8;
    }

    bool prepared =
        LiveCaptionsMicrophoneProbe.EnsureCaptureStartedAfterUserAction(window);
    Console.WriteLine($"PreparationComplete={prepared}");
    return prepared ? 0 : 12;
}

static AutomationElement? FindExistingLiveCaptionsWindow(
    out bool windowWasInAutomationRoot)
{
    using Process? process = Process.GetProcessesByName(
            LiveCaptionsHandler.PROCESS_NAME)
        .OrderByDescending(candidate => candidate.StartTime)
        .FirstOrDefault();
    if (process == null)
    {
        windowWasInAutomationRoot = false;
        return null;
    }

    var processCondition = new PropertyCondition(
        AutomationElement.ProcessIdProperty,
        process.Id);
    AutomationElement? window = AutomationElement.RootElement
        .FindAll(TreeScope.Children, processCondition)
        .Cast<AutomationElement>()
        .FirstOrDefault(candidate => string.Equals(
            candidate.Current.ClassName,
            "LiveCaptionsDesktopWindow",
            StringComparison.Ordinal));
    windowWasInAutomationRoot = window != null;
    return window ?? FindWindowFromNativeHandle(process.Id);
}

static string ReadOffscreen(AutomationElement? element)
{
    if (element == null)
        return "Unavailable";

    try
    {
        return element.Current.IsOffscreen.ToString();
    }
    catch (ElementNotAvailableException)
    {
        return "Stale";
    }
}

static string ReadEnabled(AutomationElement? element)
{
    if (element == null)
        return "Unavailable";

    try
    {
        return element.Current.IsEnabled.ToString();
    }
    catch (ElementNotAvailableException)
    {
        return "Stale";
    }
}

static AutomationElement? FindWindowFromNativeHandle(int processId)
{
    nint result = nint.Zero;
    SmokeNativeMethods.EnumWindows((windowHandle, _) =>
    {
        SmokeNativeMethods.GetWindowThreadProcessId(
            windowHandle,
            out int candidateProcessId);
        if (candidateProcessId != processId)
            return true;

        var className = new System.Text.StringBuilder(128);
        SmokeNativeMethods.GetClassName(
            windowHandle,
            className,
            className.Capacity);
        if (!string.Equals(
                className.ToString(),
                "LiveCaptionsDesktopWindow",
                StringComparison.Ordinal))
        {
            return true;
        }

        result = windowHandle;
        return false;
    }, nint.Zero);

    return result == nint.Zero
        ? null
        : AutomationElement.FromHandle(result);
}

static AutomationElement? FindRawDescendant(
    AutomationElement root,
    string automationId)
{
    var walker = TreeWalker.RawViewWalker;
    var pending = new Stack<AutomationElement>();
    AutomationElement? child = walker.GetFirstChild(root);
    while (child != null)
    {
        pending.Push(child);
        child = walker.GetNextSibling(child);
    }

    while (pending.Count > 0)
    {
        AutomationElement element = pending.Pop();
        try
        {
            if (string.Equals(
                    element.Current.AutomationId,
                    automationId,
                    StringComparison.Ordinal))
            {
                return element;
            }

            child = walker.GetFirstChild(element);
            while (child != null)
            {
                pending.Push(child);
                child = walker.GetNextSibling(child);
            }
        }
        catch (ElementNotAvailableException)
        {
        }
    }

    return null;
}

internal static class SmokeNativeMethods
{
    internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern int GetWindowThreadProcessId(
        nint hWnd,
        out int processId);

    [System.Runtime.InteropServices.DllImport(
        "user32.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    internal static extern int GetClassName(
        nint hWnd,
        System.Text.StringBuilder className,
        int maxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(
        nint hWnd,
        out SmokeWindowRect rect);
}

[System.Runtime.InteropServices.StructLayout(
    System.Runtime.InteropServices.LayoutKind.Sequential)]
internal struct SmokeWindowRect
{
    internal int Left;
    internal int Top;
    internal int Right;
    internal int Bottom;
}
