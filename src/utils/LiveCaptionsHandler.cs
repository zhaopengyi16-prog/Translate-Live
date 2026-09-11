using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Automation;

using LiveCaptionsTranslator.apis;

namespace LiveCaptionsTranslator.utils
{
    public static class LiveCaptionsHandler
    {
        public static readonly string PROCESS_NAME = "LiveCaptions";

        private static AutomationElement? captionsTextBlock = null;
        private static readonly object lifecycleLock = new();
        private static int? startedProcessId;
        private static DateTime? startedProcessStartTimeUtc;
        private static nint hiddenWindowHandle;
        private static int? originalExtendedStyle;
        private static RECT? originalWindowRect;

        public static bool WasStartedByCurrentApp
        {
            get
            {
                lock (lifecycleLock)
                    return startedProcessId.HasValue;
            }
        }

        public static bool IsHiddenByCurrentApp
        {
            get
            {
                nint hWnd;
                lock (lifecycleLock)
                    hWnd = hiddenWindowHandle;
                if (hWnd == nint.Zero ||
                    !WindowsAPI.IsWindowVisible(hWnd) ||
                    !WindowsAPI.GetWindowRect(hWnd, out RECT rect))
                {
                    return false;
                }

                return IsParkedOffscreen(rect);
            }
        }

        public static AutomationElement LaunchLiveCaptions(
            bool hideImmediately = false,
            CancellationToken token = default)
        {
            captionsTextBlock = null;

            var existingWindow = FindExistingWindow();
            if (existingWindow != null)
            {
                ClearOwnership();
                if (!WaitForAutomationReady(
                        existingWindow,
                        TimeSpan.FromSeconds(5),
                        token))
                {
                    throw new Exception(
                        "The existing Live Captions window is not ready for automation.");
                }
                if (hideImmediately)
                    HideLiveCaptions(existingWindow);
                return existingWindow;
            }

            using var process = Process.Start(new ProcessStartInfo(PROCESS_NAME)
            {
                UseShellExecute = true
            }) ?? throw new Exception("Failed to start LiveCaptions process.");

            lock (lifecycleLock)
            {
                startedProcessId = process.Id;
                startedProcessStartTimeUtc = process.StartTime.ToUniversalTime();
            }

            try
            {
                var timeout = Stopwatch.StartNew();
                while (timeout.Elapsed < TimeSpan.FromSeconds(15))
                {
                    token.ThrowIfCancellationRequested();
                    nint windowHandle = process.HasExited
                        ? nint.Zero
                        : FindWindowHandleByPId(process.Id);
                    if (windowHandle == nint.Zero)
                        windowHandle = FindExistingWindowHandle();

                    if (windowHandle != nint.Zero)
                    {
                        AutomationElement window = AutomationElement.FromHandle(windowHandle);
                        bool stagedOffscreen = hideImmediately && WasStartedByCurrentApp;
                        if (stagedOffscreen)
                            StageWindowOffscreen(windowHandle);

                        TimeSpan readinessTimeout = TimeSpan.FromSeconds(15) - timeout.Elapsed;
                        if (readinessTimeout <= TimeSpan.Zero ||
                            !WaitForAutomationReady(window, readinessTimeout, token))
                        {
                            continue;
                        }

                        TrackStartedWindow(window);
                        if (hideImmediately)
                            HideLiveCaptions(windowHandle);
                        return window;
                    }

                    if (token.WaitHandle.WaitOne(10))
                        token.ThrowIfCancellationRequested();
                }

                TryStopLaunchProcess(process);
                NotifyWindowUnavailable();
                throw new Exception("Failed to find the LiveCaptions window within 15 seconds.");
            }
            catch (OperationCanceledException)
            {
                TryStopLaunchProcess(process);
                NotifyWindowUnavailable();
                throw;
            }
        }

        private static void TryStopLaunchProcess(Process process)
        {
            try
            {
                if (process.HasExited)
                    return;

                process.Kill();
                process.WaitForExit(3000);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        private static void TrackStartedWindow(AutomationElement window)
        {
            int processId = window.Current.ProcessId;
            using var process = Process.GetProcessById(processId);
            lock (lifecycleLock)
            {
                startedProcessId = processId;
                startedProcessStartTimeUtc = process.StartTime.ToUniversalTime();
            }
        }

        public static void KillLiveCaptions(AutomationElement window)
        {
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);
            WindowsAPI.GetWindowThreadProcessId(hWnd, out int processId);

            int? ownedProcessId;
            DateTime? ownedStartTimeUtc;
            lock (lifecycleLock)
            {
                ownedProcessId = startedProcessId;
                ownedStartTimeUtc = startedProcessStartTimeUtc;
            }

            if (ownedProcessId != processId || ownedStartTimeUtc == null)
                return;

            using var process = Process.GetProcessById(processId);
            if (process.StartTime.ToUniversalTime() != ownedStartTimeUtc.Value)
                return;

            try
            {
                if (window.TryGetCurrentPattern(WindowPattern.Pattern, out object pattern) &&
                    pattern is WindowPattern windowPattern)
                {
                    windowPattern.Close();
                }
                else
                {
                    process.CloseMainWindow();
                }

                if (!process.WaitForExit(3000))
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
            }
            finally
            {
                ClearOwnership();
            }
        }

        public static bool HideLiveCaptions(AutomationElement window)
        {
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);
            return HideLiveCaptions(hWnd);
        }

        private static bool HideLiveCaptions(nint hWnd)
        {
            int exStyle = WindowsAPI.GetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE);
            WindowsAPI.GetWindowRect(hWnd, out RECT currentRect);

            lock (lifecycleLock)
            {
                if (hiddenWindowHandle != hWnd)
                {
                    hiddenWindowHandle = hWnd;
                    originalExtendedStyle = exStyle;
                    originalWindowRect = IsParkedOffscreen(currentRect)
                        ? null
                        : currentRect;
                }
            }

            WindowsAPI.SetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE, exStyle | WindowsAPI.WS_EX_TOOLWINDOW);
            Rect parkedBounds = CalculateParkedBounds(SystemParameters.WorkArea);
            bool moved = WindowsAPI.MoveWindow(
                hWnd,
                (int)parkedBounds.Left,
                (int)parkedBounds.Top,
                (int)parkedBounds.Width,
                (int)parkedBounds.Height,
                false);
            WindowsAPI.ShowWindow(hWnd, WindowsAPI.SW_RESTORE);
            return moved &&
                   WindowsAPI.IsWindowVisible(hWnd) &&
                   WindowsAPI.GetWindowRect(hWnd, out RECT parkedRect) &&
                   IsParkedOffscreen(parkedRect);
        }

        public static void NotifyWindowUnavailable()
        {
            captionsTextBlock = null;
            lock (lifecycleLock)
            {
                startedProcessId = null;
                startedProcessStartTimeUtc = null;
                hiddenWindowHandle = nint.Zero;
                originalExtendedStyle = null;
                originalWindowRect = null;
            }
        }

        public static void RestoreLiveCaptions(AutomationElement window)
        {
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);
            int? exStyle = null;
            RECT? restoreRect = null;
            lock (lifecycleLock)
            {
                if (hiddenWindowHandle == hWnd)
                {
                    exStyle = originalExtendedStyle;
                    restoreRect = originalWindowRect;
                    hiddenWindowHandle = nint.Zero;
                    originalExtendedStyle = null;
                    originalWindowRect = null;
                }
            }

            if (exStyle.HasValue)
                WindowsAPI.SetWindowLong(hWnd, WindowsAPI.GWL_EXSTYLE, exStyle.Value);
            if (restoreRect.HasValue && !IsParkedOffscreen(restoreRect.Value))
            {
                RECT rect = restoreRect.Value;
                WindowsAPI.MoveWindow(
                    hWnd,
                    rect.Left,
                    rect.Top,
                    rect.Right - rect.Left,
                    rect.Bottom - rect.Top,
                    true);
            }
            else
            {
                MoveWindowToReadableBounds(hWnd);
            }
            WindowsAPI.ShowWindow(hWnd, WindowsAPI.SW_RESTORE);
            try
            {
                FixLiveCaptions(window);
            }
            catch (Exception exception)
            {
                ProductDiagnostics.Write("livecaptions.restore-bounds-failed", exception);
            }
            WindowsAPI.SetForegroundWindow(hWnd);
        }

        public static void FixLiveCaptions(AutomationElement window)
        {
            nint hWnd = new nint((long)window.Current.NativeWindowHandle);

            RECT rect;
            if (!WindowsAPI.GetWindowRect(hWnd, out rect))
                throw new Exception("Unable to get the window rectangle of LiveCaptions!");
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            Rect workArea = SystemParameters.WorkArea;
            bool isOutsideWorkArea = rect.Right <= workArea.Left ||
                                     rect.Left >= workArea.Right ||
                                     rect.Bottom <= workArea.Top ||
                                     rect.Top >= workArea.Bottom;

            bool isSuccess = true;
            if (width < 800 || height < 220 || isOutsideWorkArea)
                isSuccess = MoveWindowToReadableBounds(hWnd);
            if (!isSuccess)
                throw new Exception("Failed to fix LiveCaptions!");
        }

        internal static Rect CalculateReadableBounds(Rect workArea)
        {
            double width = Math.Clamp(workArea.Width * 0.72, 960, 1440);
            double height = Math.Clamp(workArea.Height * 0.24, 280, 420);
            double left = workArea.Left + (workArea.Width - width) / 2;
            double top = Math.Max(workArea.Top, workArea.Bottom - height - 32);
            return new Rect(left, top, width, height);
        }

        internal static Rect CalculateParkedBounds(Rect workArea)
        {
            Rect readable = CalculateReadableBounds(workArea);
            return new Rect(-32000, -32000, readable.Width, readable.Height);
        }

        private static void StageWindowOffscreen(nint hWnd)
        {
            HideLiveCaptions(hWnd);
        }

        private static bool MoveWindowToReadableBounds(nint hWnd)
        {
            Rect readableBounds = CalculateReadableBounds(SystemParameters.WorkArea);
            return WindowsAPI.MoveWindow(
                hWnd,
                (int)readableBounds.Left,
                (int)readableBounds.Top,
                (int)readableBounds.Width,
                (int)readableBounds.Height,
                true);
        }

        private static bool IsParkedOffscreen(RECT rect)
        {
            return rect.Left <= -30000 && rect.Top <= -30000;
        }

        private static bool WaitForAutomationReady(
            AutomationElement window,
            TimeSpan timeout,
            CancellationToken token)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < timeout)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    AutomationElement? captions = FindElementByAId(
                        window,
                        "CaptionsTextBlock",
                        token);
                    AutomationElement? settings = FindElementByAId(
                        window,
                        "SettingsButton",
                        token);
                    AutomationElement? continueButton = FindElementByAId(
                        window,
                        "ContinueButton",
                        token);
                    if ((captions != null && settings != null) || continueButton != null)
                    {
                        captionsTextBlock = captions;
                        return true;
                    }
                }
                catch (ElementNotAvailableException)
                {
                }
                catch (InvalidOperationException)
                {
                }

                if (token.WaitHandle.WaitOne(50))
                    token.ThrowIfCancellationRequested();
            }

            return false;
        }

        public static string GetCaptions(AutomationElement window)
        {
            if (captionsTextBlock == null)
                captionsTextBlock = FindElementByAId(window, "CaptionsTextBlock");
            try
            {
                return captionsTextBlock?.Current.Name ?? string.Empty;
            }
            catch (ElementNotAvailableException)
            {
                captionsTextBlock = null;
                throw;
            }
        }

        private static AutomationElement? FindWindowByPId(int processId)
        {
            nint windowHandle = FindWindowHandleByPId(processId);
            return windowHandle == nint.Zero
                ? null
                : AutomationElement.FromHandle(windowHandle);
        }

        private static AutomationElement? FindExistingWindow()
        {
            nint windowHandle = FindExistingWindowHandle();
            return windowHandle == nint.Zero
                ? null
                : AutomationElement.FromHandle(windowHandle);
        }

        private static nint FindExistingWindowHandle()
        {
            foreach (var process in Process.GetProcessesByName(PROCESS_NAME))
            {
                using (process)
                {
                    try
                    {
                        nint windowHandle = FindWindowHandleByPId(process.Id);
                        if (windowHandle != nint.Zero)
                            return windowHandle;
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (ElementNotAvailableException)
                    {
                    }
                }
            }

            return nint.Zero;
        }

        private static nint FindWindowHandleByPId(int processId)
        {
            nint result = nint.Zero;
            WindowsAPI.EnumWindows((windowHandle, _) =>
            {
                WindowsAPI.GetWindowThreadProcessId(windowHandle, out int candidateProcessId);
                if (candidateProcessId != processId)
                    return true;

                var className = new StringBuilder(128);
                WindowsAPI.GetClassName(windowHandle, className, className.Capacity);
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
            return result;
        }

        public static AutomationElement? FindElementByAId(
            AutomationElement window, string automationId, CancellationToken token = default)
        {
            try
            {
                PropertyCondition condition = new PropertyCondition(
                    AutomationElement.AutomationIdProperty, automationId);
                return window.FindFirst(TreeScope.Descendants, condition);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (NullReferenceException)
            {
                return null;
            }
        }

        public static void PrintAllElementsAId(AutomationElement window)
        {
            var treeWalker = TreeWalker.RawViewWalker;
            var stack = new Stack<AutomationElement>();
            stack.Push(window);

            while (stack.Count > 0)
            {
                var element = stack.Pop();
                if (!string.IsNullOrEmpty(element.Current.AutomationId))
                    Console.WriteLine(element.Current.AutomationId);

                var child = treeWalker.GetFirstChild(element);
                while (child != null)
                {
                    stack.Push(child);
                    child = treeWalker.GetNextSibling(child);
                }
            }
        }

        public static bool ClickSettingsButton(AutomationElement window)
        {
            var settingsButton = FindElementByAId(window, "SettingsButton");
            if (settingsButton != null)
            {
                var invokePattern = settingsButton.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
                if (invokePattern != null)
                {
                    invokePattern.Invoke();
                    return true;
                }
            }
            return false;
        }

        private static void ClearOwnership()
        {
            lock (lifecycleLock)
            {
                startedProcessId = null;
                startedProcessStartTimeUtc = null;
            }
        }
    }
}
