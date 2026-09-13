using System.Diagnostics;
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
using LiveCaptionsTranslator.utils;
using LiveCaptionsTranslator.viewmodels;

AutomationElement? liveCaptionsWindow = null;
int? liveCaptionsProcessId = null;
int exitCode = 0;
MicrophoneCaptionState? initialMicrophoneState = null;
bool exerciseSystemAudio = args.Contains(
    "--system-audio",
    StringComparer.OrdinalIgnoreCase);

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
        bool systemAudioDetected = ExerciseSystemAudio(
            liveCaptionsWindow,
            out int initialCharacterCount,
            out int finalCharacterCount);
        Console.WriteLine($"SystemAudioInitialCharacterCount={initialCharacterCount}");
        Console.WriteLine($"SystemAudioFinalCharacterCount={finalCharacterCount}");
        Console.WriteLine($"SystemAudioCaptionDetected={systemAudioDetected}");
        if (!systemAudioDetected)
            exitCode = 7;
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
        Console.WriteLine($"EnableErrorCode={enabled.ErrorCode ?? "none"}");
        if (enabled.State != MicrophoneCaptionState.On)
        {
            exitCode = exitCode == 0 ? 5 : exitCode;
        }
        else if (initialMicrophoneState == MicrophoneCaptionState.Off)
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

static bool ExerciseSystemAudio(
    AutomationElement liveCaptionsWindow,
    out int initialCharacterCount,
    out int finalCharacterCount)
{
    initialCharacterCount = ReadCaptionCharacterCount(liveCaptionsWindow);
    finalCharacterCount = initialCharacterCount;

    if (!LiveCaptionsMicrophoneProbe.EnsureCaptureStartedAfterUserAction(
            liveCaptionsWindow))
    {
        return false;
    }

    object? voice = null;
    try
    {
        Type? voiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
        if (voiceType == null)
            return false;

        voice = Activator.CreateInstance(voiceType);
        if (voice == null)
            return false;

        voiceType.InvokeMember(
            "Speak",
            BindingFlags.InvokeMethod,
            binder: null,
            target: voice,
            args: new object[]
            {
                "The lecture timeline keeps every sentence in order for review.",
                0
            });

        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            finalCharacterCount = ReadCaptionCharacterCount(liveCaptionsWindow);
            if (finalCharacterCount > initialCharacterCount)
                return true;

            Thread.Sleep(150);
        }

        return false;
    }
    finally
    {
        if (voice != null && Marshal.IsComObject(voice))
            Marshal.FinalReleaseComObject(voice);
    }
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
