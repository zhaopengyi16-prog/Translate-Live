using System.Windows;

using System.Diagnostics;
using System.IO;
using System.Windows.Media.Animation;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.services;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class App : Application
    {
        private const string SINGLE_INSTANCE_MUTEX_NAME = "Local\\LectureCopilot.Dev";
        private static readonly TimeSpan STARTUP_GRACE_PERIOD = TimeSpan.FromSeconds(15);

        private readonly CancellationTokenSource shutdownTokenSource = new();
        private Task[] backgroundTasks = [];
        private Mutex? singleInstanceMutex;
        private bool ownsSingleInstanceMutex;
        private bool servicesStarted;
        private int shutdownStarted;
        private int uiStartupCompleted;

        App()
        {
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            DispatcherUnhandledException += OnDispatcherUnhandledException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            AppPaths.EnsureCreated();
            if (!TryAcquireSingleInstance())
            {
                Shutdown();
                return;
            }

            try
            {
                SQLiteHistoryLogger.EnsureInitialized();
            }
            catch (Exception exception)
            {
                ProductDiagnostics.Write("startup.history-initialization-failed", exception);
            }
            RuntimeJournal.Begin();
            ProductDiagnostics.Write(RuntimeJournal.PreviousRunWasInterrupted
                ? "startup.after-interrupted-run"
                : "startup");
            Translator.Setting?.Save();
            TypographyService.Apply(Translator.Setting?.Appearance);

            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            if (Translator.Setting?.Appearance.ReduceMotion == true)
            {
                ShowMainWindow(mainWindow);
                CompleteUiStartup(mainWindow);
                return;
            }

            ShowStartupReveal(mainWindow);
        }

        private void ShowStartupReveal(Window mainWindow)
        {
            StartupWindow? startupWindow = null;
            int revealStarted = 0;

            void RevealMainWindow()
            {
                if (Interlocked.Exchange(ref revealStarted, 1) != 0)
                    return;

                ShowMainWindow(mainWindow);
                if (startupWindow == null || !startupWindow.IsVisible)
                {
                    CompleteUiStartup(mainWindow);
                    return;
                }

                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new QuadraticEase
                    {
                        EasingMode = EasingMode.EaseOut
                    }
                };
                fadeOut.Completed += (_, _) =>
                {
                    startupWindow.Close();
                    CompleteUiStartup(mainWindow);
                };
                startupWindow.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }

            try
            {
                startupWindow = new StartupWindow(new Rect(
                    mainWindow.Left,
                    mainWindow.Top,
                    mainWindow.Width,
                    mainWindow.Height));
                startupWindow.RevealReady += (_, _) => RevealMainWindow();
                startupWindow.Closed += (_, _) => RevealMainWindow();
                startupWindow.Show();
            }
            catch (Exception exception)
            {
                ProductDiagnostics.Write("startup.reveal-failed", exception);
                RevealMainWindow();
            }
        }

        private static void ShowMainWindow(Window mainWindow)
        {
            mainWindow.Opacity = 1;
            mainWindow.Show();
            mainWindow.Activate();
        }

        private void CompleteUiStartup(Window mainWindow)
        {
            if (Interlocked.Exchange(ref uiStartupCompleted, 1) != 0)
                return;

            mainWindow.Dispatcher.BeginInvoke(new Action(() =>
            {
                StartBackgroundServices();
                ShowCredentialStorageWarning(mainWindow);
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private void StartBackgroundServices()
        {
            if (servicesStarted)
                return;

            servicesStarted = true;
            ProductDiagnostics.Write("startup.ui-ready");
            backgroundTasks =
            [
                Task.Run(() => Translator.SyncLoop(shutdownTokenSource.Token), shutdownTokenSource.Token),
                Task.Run(() => Translator.TranslateLoop(shutdownTokenSource.Token), shutdownTokenSource.Token),
                Task.Run(() => Translator.DisplayLoop(shutdownTokenSource.Token), shutdownTokenSource.Token)
            ];
        }

        private static void ShowCredentialStorageWarning(Window owner)
        {
            string? warning = Translator.Setting?.CredentialStorageWarning;
            if (string.IsNullOrWhiteSpace(warning))
                return;

            MessageBox.Show(
                owner,
                warning,
                "Translate Live · API 凭据需要处理",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private bool TryAcquireSingleInstance()
        {
            singleInstanceMutex = new Mutex(
                initiallyOwned: true,
                SINGLE_INSTANCE_MUTEX_NAME,
                out bool createdNew);
            ownsSingleInstanceMutex = createdNew;
            if (createdNew)
                return true;

            for (int attempt = 0; attempt < 20; attempt++)
            {
                using var existing = FindExistingInstance();
                if (existing != null && TryActivate(existing))
                {
                    ProductDiagnostics.Write("startup.existing-instance-activated");
                    return false;
                }
                Thread.Sleep(100);
            }

            using (var existing = FindExistingInstance())
            {
                if (existing != null)
                {
                    try
                    {
                        existing.Refresh();
                        if (DateTime.Now - existing.StartTime < STARTUP_GRACE_PERIOD)
                        {
                            ProductDiagnostics.Write("startup.existing-instance-still-starting");
                            return false;
                        }

                        if (existing.MainWindowHandle != nint.Zero && TryActivate(existing))
                            return false;

                        ProductDiagnostics.Write("startup.stale-instance-terminating");
                        existing.Kill(entireProcessTree: true);
                        existing.WaitForExit(5000);
                    }
                    catch (Exception exception)
                    {
                        ProductDiagnostics.Write(
                            "startup.stale-instance-termination-failed", exception);
                        return false;
                    }
                }
            }

            try
            {
                ownsSingleInstanceMutex =
                    singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                ownsSingleInstanceMutex = true;
            }

            if (ownsSingleInstanceMutex)
                ProductDiagnostics.Write("startup.stale-instance-recovered");
            return ownsSingleInstanceMutex;
        }

        private static Process? FindExistingInstance()
        {
            using var current = Process.GetCurrentProcess();
            string? currentPath = Environment.ProcessPath;
            foreach (var process in Process.GetProcessesByName(current.ProcessName))
            {
                if (process.Id == current.Id)
                {
                    process.Dispose();
                    continue;
                }

                try
                {
                    string? processPath = process.MainModule?.FileName;
                    if (currentPath == null || processPath == null ||
                        !string.Equals(
                            Path.GetFullPath(processPath),
                            Path.GetFullPath(currentPath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        process.Dispose();
                        continue;
                    }

                    return process;
                }
                catch
                {
                    process.Dispose();
                }
            }

            return null;
        }

        private static bool TryActivate(Process process)
        {
            process.Refresh();
            nint handle = process.MainWindowHandle;
            if (handle == nint.Zero)
                return false;

            WindowsAPI.ShowWindow(handle, WindowsAPI.SW_RESTORE);
            WindowsAPI.SetForegroundWindow(handle);
            return true;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ShutdownCore();
            base.OnExit(e);
        }

        private void OnProcessExit(object? sender, EventArgs e)
        {
            ShutdownCore();
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            ProductDiagnostics.Write("appdomain.unhandled", e.ExceptionObject as Exception);
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            ProductDiagnostics.Write("task.unobserved", e.Exception);
            e.SetObserved();
        }

        private static void OnDispatcherUnhandledException(
            object sender,
            System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            ProductDiagnostics.Write("dispatcher.unhandled", e.Exception);
        }

        private void ShutdownCore()
        {
            if (Interlocked.Exchange(ref shutdownStarted, 1) != 0)
                return;

            if (servicesStarted)
            {
                bool cleanShutdown = true;
                try
                {
                    using var flushDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    Task.Run(() => Translator.StopCaptureAndFlushAsync(flushDeadline.Token))
                        .GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    cleanShutdown = false;
                    ProductDiagnostics.Write("shutdown.caption-flush-failed", exception);
                }
                shutdownTokenSource.Cancel();
                try
                {
                    if (!Task.WaitAll(backgroundTasks, TimeSpan.FromSeconds(3)))
                    {
                        cleanShutdown = false;
                        ProductDiagnostics.Write("shutdown.workers-timeout");
                    }
                }
                catch (AggregateException exception)
                {
                    if (exception.Flatten().InnerExceptions.Any(
                            inner => inner is not OperationCanceledException &&
                                     inner is not TaskCanceledException))
                    {
                        cleanShutdown = false;
                        ProductDiagnostics.Write("shutdown.worker-fault", exception);
                    }
                }

                try
                {
                    LectureSessionTracker.EndCurrentAsync().GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    cleanShutdown = false;
                    ProductDiagnostics.Write("shutdown.session-close-failed", exception);
                }

                Translator.Setting?.Save();
                if (Translator.Window != null)
                {
                    try
                    {
                        LiveCaptionsMicrophoneProbe.DisableIfEnabledByCurrentApp(Translator.Window);
                        if (!LiveCaptionsHandler.WasStartedByCurrentApp)
                            LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);
                        LiveCaptionsHandler.KillLiveCaptions(Translator.Window);
                    }
                    catch (System.Windows.Automation.ElementNotAvailableException)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }

                if (cleanShutdown && !Translator.HasHistoryWriteFailure)
                {
                    RuntimeJournal.Complete();
                    ProductDiagnostics.Write("shutdown.clean");
                }
                else
                {
                    ProductDiagnostics.Write("shutdown.recovery-retained");
                }
            }

            if (ownsSingleInstanceMutex)
                singleInstanceMutex?.ReleaseMutex();
            singleInstanceMutex?.Dispose();
        }
    }
}
