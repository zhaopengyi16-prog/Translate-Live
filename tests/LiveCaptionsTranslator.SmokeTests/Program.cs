using System.Diagnostics;
using System.Windows.Automation;

using LiveCaptionsTranslator.services;
using LiveCaptionsTranslator.utils;

AutomationElement? liveCaptionsWindow = null;
int? liveCaptionsProcessId = null;
int exitCode = 0;
MicrophoneCaptionState? initialMicrophoneState = null;

if (args.Contains("--inspect-existing", StringComparer.OrdinalIgnoreCase))
{
    return InspectExistingLiveCaptions();
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
    MicrophoneProbeResult result = LiveCaptionsMicrophoneProbe.ReadState(liveCaptionsWindow);
    initialMicrophoneState = result.State;

    Console.WriteLine($"MicrophoneState={result.State}");
    Console.WriteLine($"AutomationAvailable={result.AutomationAvailable}");
    Console.WriteLine($"ErrorCode={result.ErrorCode ?? "none"}");
    if (!result.AutomationAvailable)
    {
        exitCode = 2;
    }
    else
    {
        MicrophoneProbeResult enabled = LiveCaptionsMicrophoneProbe.EnableAfterUserAction(liveCaptionsWindow);
        Console.WriteLine($"EnableState={enabled.State}");
        Console.WriteLine($"EnableErrorCode={enabled.ErrorCode ?? "none"}");
        if (enabled.State != MicrophoneCaptionState.On)
        {
            exitCode = 5;
        }
        else if (initialMicrophoneState == MicrophoneCaptionState.Off)
        {
            MicrophoneProbeResult restored =
                LiveCaptionsMicrophoneProbe.DisableAfterUserAction(liveCaptionsWindow);
            Console.WriteLine($"RestoreState={restored.State}");
            Console.WriteLine($"RestoreErrorCode={restored.ErrorCode ?? "none"}");
            if (restored.State != MicrophoneCaptionState.Off)
                exitCode = 6;
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
