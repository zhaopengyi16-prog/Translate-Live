using System.Diagnostics;
using System.Windows.Automation;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.services
{
    public enum MicrophoneCaptionState
    {
        Unknown,
        Off,
        On,
        Blocked
    }

    public sealed record MicrophoneProbeResult(
        MicrophoneCaptionState State,
        bool AutomationAvailable,
        string? ErrorCode = null);

    public static class LiveCaptionsMicrophoneProbe
    {
        private const string SETTINGS_BUTTON_ID = "SettingsButton";
        private const string SETTINGS_MENU_ID = "SettingsMenuFlyout";
        private const string PREFERENCES_BUTTON_ID = "PreferencesButton";
        private const string MICROPHONE_ITEM_ID = "MicrophoneMenuFlyoutItem";
        private const string CONTINUE_BUTTON_ID = "ContinueButton";
        private static readonly TimeSpan flyoutTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan toggleTimeout = TimeSpan.FromSeconds(3);
        private static readonly object stateLock = new();
        private static bool enabledByCurrentApp;

        public static bool WasEnabledByCurrentApp
        {
            get
            {
                lock (stateLock)
                    return enabledByCurrentApp;
            }
        }

        public static MicrophoneProbeResult ReadState(AutomationElement liveCaptionsWindow)
        {
            return Probe(liveCaptionsWindow, requestedState: null);
        }

        public static MicrophoneProbeResult EnableAfterUserAction(AutomationElement liveCaptionsWindow)
        {
            return Probe(liveCaptionsWindow, ToggleState.On);
        }

        public static MicrophoneProbeResult DisableAfterUserAction(AutomationElement liveCaptionsWindow)
        {
            return Probe(liveCaptionsWindow, ToggleState.Off);
        }

        public static MicrophoneProbeResult DisableIfEnabledByCurrentApp(AutomationElement liveCaptionsWindow)
        {
            if (!WasEnabledByCurrentApp)
                return new(MicrophoneCaptionState.Unknown, true, "not-enabled-by-app");

            return Probe(liveCaptionsWindow, ToggleState.Off);
        }

        public static bool EnsureCaptureStartedAfterUserAction(
            AutomationElement liveCaptionsWindow)
        {
            bool restoreHiddenState = LiveCaptionsHandler.IsHiddenByCurrentApp;
            try
            {
                if (restoreHiddenState)
                    LiveCaptionsHandler.RestoreLiveCaptions(liveCaptionsWindow);

                nint hWnd = new((long)liveCaptionsWindow.Current.NativeWindowHandle);
                WindowsAPI.GetWindowThreadProcessId(hWnd, out int processId);
                return processId != 0 &&
                       CompleteLanguagePreparationAfterUserAction(processId);
            }
            catch (ElementNotAvailableException)
            {
                ProductDiagnostics.Write("livecaptions.start-element-stale");
                return false;
            }
            catch (InvalidOperationException)
            {
                ProductDiagnostics.Write("livecaptions.start-automation-failed");
                return false;
            }
            finally
            {
                if (restoreHiddenState)
                {
                    try
                    {
                        LiveCaptionsHandler.HideLiveCaptions(liveCaptionsWindow);
                    }
                    catch (ElementNotAvailableException)
                    {
                    }
                }
            }
        }

        private static MicrophoneProbeResult Probe(
            AutomationElement liveCaptionsWindow,
            ToggleState? requestedState)
        {
            bool restoreHiddenState = LiveCaptionsHandler.IsHiddenByCurrentApp;
            int processId = 0;
            InvokePattern? settingsInvokeForCleanup = null;
            try
            {
                if (restoreHiddenState)
                    LiveCaptionsHandler.RestoreLiveCaptions(liveCaptionsWindow);

                nint hWnd = new((long)liveCaptionsWindow.Current.NativeWindowHandle);
                WindowsAPI.GetWindowThreadProcessId(hWnd, out processId);

                if (requestedState == ToggleState.On &&
                    !CompleteLanguagePreparationAfterUserAction(processId))
                {
                    return Failure(
                        MicrophoneCaptionState.Unknown,
                        false,
                        "livecaptions-preparation-timeout");
                }

                var microphoneItem = WaitForVisibleProcessElement(
                    processId,
                    MICROPHONE_ITEM_ID,
                    TimeSpan.FromMilliseconds(250));
                if (!SupportsToggle(microphoneItem))
                {
                    var settingsButton = WaitForWindowElement(
                        processId,
                        SETTINGS_BUTTON_ID,
                        flyoutTimeout);
                    if (settingsButton == null ||
                        !settingsButton.TryGetCurrentPattern(
                            InvokePattern.Pattern,
                            out object settingsPattern) ||
                        settingsPattern is not InvokePattern settingsInvoke)
                    {
                        return Failure(MicrophoneCaptionState.Unknown, false, "settings-unavailable");
                    }

                    settingsInvokeForCleanup = settingsInvoke;
                    microphoneItem = OpenMicrophoneMenu(
                        processId,
                        settingsInvoke,
                        out string? openError);
                    if (microphoneItem == null)
                    {
                        return Failure(MicrophoneCaptionState.Unknown, false,
                            openError ?? "microphone-toggle-unavailable");
                    }
                }

                if (microphoneItem == null ||
                    !microphoneItem.TryGetCurrentPattern(TogglePattern.Pattern, out object microphonePattern) ||
                    microphonePattern is not TogglePattern microphoneToggle)
                {
                    return Failure(
                        MicrophoneCaptionState.Unknown,
                        false,
                        "microphone-toggle-unavailable");
                }

                if (!microphoneItem.Current.IsEnabled)
                    return Failure(MicrophoneCaptionState.Blocked, true, "microphone-toggle-disabled");

                var state = MapState(microphoneToggle.Current.ToggleState);
                if (!requestedState.HasValue)
                    return new(state, true);

                var requestedMicrophoneState = MapState(requestedState.Value);
                if (state == requestedMicrophoneState)
                {
                    if (requestedState == ToggleState.Off)
                    {
                        lock (stateLock)
                            enabledByCurrentApp = false;
                    }

                    return new(state, true);
                }

                microphoneToggle.Toggle();
                var verification = WaitForToggleState(microphoneToggle, requestedState.Value);
                if (!verification)
                    return Failure(MicrophoneCaptionState.Unknown, true, "microphone-state-not-confirmed");

                lock (stateLock)
                    enabledByCurrentApp = requestedState == ToggleState.On;

                return new(requestedMicrophoneState, true);
            }
            catch (ElementNotAvailableException)
            {
                return new(MicrophoneCaptionState.Unknown, false, "livecaptions-element-stale");
            }
            catch (InvalidOperationException)
            {
                return new(MicrophoneCaptionState.Unknown, false, "automation-pattern-failed");
            }
            catch (Exception exception)
            {
                ProductDiagnostics.Write("microphone.probe-failed", exception);
                return new(MicrophoneCaptionState.Unknown, false, "automation-unexpected");
            }
            finally
            {
                if (processId != 0)
                    DismissSettingsMenu(processId, settingsInvokeForCleanup);

                if (restoreHiddenState)
                {
                    try
                    {
                        LiveCaptionsHandler.HideLiveCaptions(liveCaptionsWindow);
                    }
                    catch (ElementNotAvailableException)
                    {
                    }
                }
            }
        }

        private static bool CompleteLanguagePreparationAfterUserAction(int processId)
        {
            var continueButton = WaitForWindowElement(
                processId,
                CONTINUE_BUTTON_ID,
                TimeSpan.FromMilliseconds(250));
            if (continueButton == null)
                return true;

            if (!continueButton.TryGetCurrentPattern(
                    InvokePattern.Pattern,
                    out object continuePattern) ||
                continuePattern is not InvokePattern continueInvoke)
            {
                return false;
            }

            continueInvoke.Invoke();
            ProductDiagnostics.Write("livecaptions.capture-started-by-user-action");
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (WaitForWindowElement(
                        processId,
                        CONTINUE_BUTTON_ID,
                        TimeSpan.FromMilliseconds(150)) == null)
                {
                    return true;
                }

                Thread.Sleep(150);
            }

            return false;
        }

        private static void DismissSettingsMenu(
            int processId,
            InvokePattern? settingsInvoke)
        {
            try
            {
                if (WaitForVisibleProcessElement(
                        processId,
                        SETTINGS_MENU_ID,
                        TimeSpan.FromMilliseconds(150)) == null)
                {
                    return;
                }

                if (settingsInvoke == null)
                {
                    var preferences = WaitForVisibleProcessElement(
                        processId,
                        PREFERENCES_BUTTON_ID,
                        TimeSpan.FromMilliseconds(250));
                    if (preferences != null &&
                        preferences.TryGetCurrentPattern(
                            ExpandCollapsePattern.Pattern,
                            out object preferencesPattern) &&
                        preferencesPattern is ExpandCollapsePattern preferencesExpand &&
                        preferencesExpand.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                    {
                        preferencesExpand.Collapse();
                        Thread.Sleep(150);
                    }

                    var settingsButton = WaitForWindowElement(
                        processId,
                        SETTINGS_BUTTON_ID,
                        TimeSpan.FromSeconds(1));
                    if (settingsButton != null &&
                        settingsButton.TryGetCurrentPattern(
                            InvokePattern.Pattern,
                            out object settingsPattern) &&
                        settingsPattern is InvokePattern refreshedSettingsInvoke)
                    {
                        settingsInvoke = refreshedSettingsInvoke;
                    }
                }

                settingsInvoke?.Invoke();
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static MicrophoneProbeResult Failure(
            MicrophoneCaptionState state,
            bool automationAvailable,
            string errorCode)
        {
            ProductDiagnostics.Write($"microphone.{errorCode}");
            return new(state, automationAvailable, errorCode);
        }

        private static AutomationElement? OpenMicrophoneMenu(
            int processId,
            InvokePattern settingsInvoke,
            out string? errorCode)
        {
            errorCode = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var microphoneItem = WaitForVisibleProcessElement(
                    processId,
                    MICROPHONE_ITEM_ID,
                    TimeSpan.FromMilliseconds(250));
                if (SupportsToggle(microphoneItem))
                    return microphoneItem;

                var settingsMenu = WaitForVisibleProcessElement(
                    processId,
                    SETTINGS_MENU_ID,
                    TimeSpan.FromMilliseconds(250));
                if (settingsMenu == null)
                {
                    settingsInvoke.Invoke();
                    settingsMenu = WaitForVisibleProcessElement(
                        processId,
                        SETTINGS_MENU_ID,
                        flyoutTimeout);
                    if (settingsMenu == null)
                    {
                        errorCode = "settings-menu-unavailable";
                        continue;
                    }
                }

                var preferences = WaitForVisibleProcessElement(
                    processId,
                    PREFERENCES_BUTTON_ID,
                    flyoutTimeout);
                if (preferences == null ||
                    !preferences.TryGetCurrentPattern(
                        ExpandCollapsePattern.Pattern,
                        out object preferencesPattern) ||
                    preferencesPattern is not ExpandCollapsePattern preferencesExpand)
                {
                    errorCode = "preferences-unavailable";
                    ToggleSettingsMenu(settingsInvoke, processId);
                    continue;
                }

                if (preferencesExpand.Current.ExpandCollapseState != ExpandCollapseState.Expanded)
                    preferencesExpand.Expand();

                microphoneItem = WaitForVisibleProcessElement(
                    processId,
                    MICROPHONE_ITEM_ID,
                    flyoutTimeout);
                if (SupportsToggle(microphoneItem))
                    return microphoneItem;

                errorCode = "microphone-toggle-unavailable";
                ToggleSettingsMenu(settingsInvoke, processId);
            }

            return null;
        }

        private static bool SupportsToggle(AutomationElement? element)
        {
            if (element == null)
                return false;

            try
            {
                return element.TryGetCurrentPattern(TogglePattern.Pattern, out object pattern) &&
                       pattern is TogglePattern;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        }

        private static void ToggleSettingsMenu(InvokePattern settingsInvoke, int processId)
        {
            if (WaitForVisibleProcessElement(
                    processId,
                    SETTINGS_MENU_ID,
                    TimeSpan.FromMilliseconds(150)) == null)
            {
                return;
            }

            settingsInvoke.Invoke();
            Thread.Sleep(150);
        }

        private static AutomationElement? WaitForWindowElement(
            int processId,
            string automationId,
            TimeSpan timeout)
        {
            var processCondition = new PropertyCondition(
                AutomationElement.ProcessIdProperty,
                processId);
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < timeout)
            {
                var windows = AutomationElement.RootElement.FindAll(
                    TreeScope.Children,
                    processCondition);
                foreach (AutomationElement window in windows)
                {
                    try
                    {
                        if (window.Current.ClassName != "LiveCaptionsDesktopWindow")
                            continue;

                        var element = FindRawDescendantByAutomationId(
                            window,
                            automationId,
                            requireVisibleAndEnabled: true);
                        if (element != null)
                            return element;
                    }
                    catch (ElementNotAvailableException)
                    {
                    }
                }

                Thread.Sleep(75);
            }

            return null;
        }

        private static AutomationElement? FindRawDescendantByAutomationId(
            AutomationElement root,
            string automationId,
            bool requireVisibleAndEnabled = false)
        {
            var walker = TreeWalker.RawViewWalker;
            var pending = new Stack<AutomationElement>();
            var child = walker.GetFirstChild(root);
            while (child != null)
            {
                pending.Push(child);
                child = walker.GetNextSibling(child);
            }

            while (pending.Count > 0)
            {
                var element = pending.Pop();
                try
                {
                    bool matches = string.Equals(
                        element.Current.AutomationId,
                        automationId,
                        StringComparison.Ordinal);
                    if (matches &&
                        (!requireVisibleAndEnabled ||
                         (!element.Current.IsOffscreen && element.Current.IsEnabled)))
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

        private static AutomationElement? WaitForVisibleProcessElement(
            int processId,
            string automationId,
            TimeSpan timeout)
        {
            var processCondition = new PropertyCondition(
                AutomationElement.ProcessIdProperty,
                processId);
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < timeout)
            {
                var roots = AutomationElement.RootElement.FindAll(
                    TreeScope.Children,
                    processCondition);
                foreach (AutomationElement root in roots)
                {
                    try
                    {
                        if (string.Equals(
                                root.Current.AutomationId,
                                automationId,
                                StringComparison.Ordinal) &&
                            !root.Current.IsOffscreen &&
                            root.Current.IsEnabled)
                        {
                            return root;
                        }

                        var element = FindRawDescendantByAutomationId(
                            root,
                            automationId,
                            requireVisibleAndEnabled: true);
                        if (element != null)
                            return element;
                    }
                    catch (ElementNotAvailableException)
                    {
                    }
                }

                Thread.Sleep(75);
            }

            return null;
        }

        private static bool WaitForToggleState(TogglePattern pattern, ToggleState expected)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < toggleTimeout)
            {
                if (pattern.Current.ToggleState == expected)
                    return true;

                Thread.Sleep(75);
            }

            return false;
        }

        private static MicrophoneCaptionState MapState(ToggleState state)
        {
            return state switch
            {
                ToggleState.On => MicrophoneCaptionState.On,
                ToggleState.Off => MicrophoneCaptionState.Off,
                _ => MicrophoneCaptionState.Unknown
            };
        }
    }
}
