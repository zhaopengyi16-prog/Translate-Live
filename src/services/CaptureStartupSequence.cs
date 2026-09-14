using System.Diagnostics;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.services
{
    internal enum CaptureStartupStage
    {
        EndingPreviousSession,
        PreparingCaptionSource,
        ReadingBaseline,
        PreparingCaptureEpoch,
        CreatingSession,
        ActivatingInput,
        RebindingCaptionSource,
        ResumingCapture,
        Completed,
        Cancelled
    }

    internal sealed record CaptureStepResult(
        bool Succeeded,
        string? ErrorCode = null);

    internal sealed record CaptionSourceSnapshot(
        bool IsReadable,
        string Text,
        string? ErrorCode = null)
    {
        public static CaptionSourceSnapshot Readable(string? text) =>
            new(true, text ?? string.Empty);

        public static CaptionSourceSnapshot Unavailable(string errorCode) =>
            new(false, string.Empty, errorCode);
    }

    internal sealed record PreparedCaptureSession(
        long CaptureEpoch,
        long PreparationId);

    internal sealed record CaptureInputActivation(
        bool IsActive,
        bool ChangedByRequest,
        string? ErrorCode = null);

    internal sealed record CaptureStartupResult(
        bool IsStarted,
        CaptureStartupStage Stage,
        LectureSessionEntry? Session,
        PreparedCaptureSession? Preparation,
        string? ErrorCode = null,
        Exception? Exception = null,
        bool RollbackSucceeded = true);

    internal sealed class CaptureStartupOperations
    {
        public required Func<CancellationToken, Task> EndPreviousSessionAsync { get; init; }
        public required Func<CancellationToken, Task<CaptureStepResult>> PrepareCaptionSourceAsync { get; init; }
        public required Func<CancellationToken, Task<CaptionSourceSnapshot>> ReadBaselineAsync { get; init; }
        public required Func<string, CancellationToken, Task<PreparedCaptureSession>> PrepareCaptureAsync { get; init; }
        public required Func<CancellationToken, Task<LectureSessionEntry>> CreateSessionAsync { get; init; }
        public required Func<CancellationToken, Task<CaptureInputActivation>> ActivateInputAsync { get; init; }
        public required Func<CancellationToken, Task<CaptionSourceSnapshot>> RebindCaptionSourceAsync { get; init; }
        public required Func<PreparedCaptureSession, long, bool> ResumeCapture { get; init; }
        public required Func<PreparedCaptureSession?, long?, bool, CancellationToken, Task> AbortAsync { get; init; }
        public Action<CaptureStartupStage, string, long?, long>? ReportStage { get; init; }
    }

    /// <summary>
    /// Owns the ordering for a new live-capture session. The baseline is read
    /// while capture is suspended and before the requested input is activated.
    /// </summary>
    internal static class CaptureStartupSequence
    {
        public static async Task<CaptureStartupResult> RunAsync(
            CaptureStartupOperations operations,
            CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(operations);

            PreparedCaptureSession? preparation = null;
            LectureSessionEntry? session = null;
            bool inputChangedByRequest = false;
            CaptureStartupStage stage = CaptureStartupStage.EndingPreviousSession;

            try
            {
                await RunStageAsync(stage, async () =>
                {
                    await operations.EndPreviousSessionAsync(token);
                    token.ThrowIfCancellationRequested();
                    return "completed";
                }, operations, () => preparation?.CaptureEpoch);

                stage = CaptureStartupStage.PreparingCaptionSource;
                CaptureStepResult sourcePreparation = default!;
                await RunStageAsync(stage, async () =>
                {
                    sourcePreparation = await operations.PrepareCaptionSourceAsync(token);
                    token.ThrowIfCancellationRequested();
                    return sourcePreparation.Succeeded ? "ready" : "failed";
                }, operations, () => preparation?.CaptureEpoch);
                if (!sourcePreparation.Succeeded)
                {
                    return new(false, stage, null, null,
                        sourcePreparation.ErrorCode ?? "caption-source-not-ready");
                }

                stage = CaptureStartupStage.ReadingBaseline;
                CaptionSourceSnapshot baseline = default!;
                await RunStageAsync(stage, async () =>
                {
                    baseline = await operations.ReadBaselineAsync(token);
                    token.ThrowIfCancellationRequested();
                    return !baseline.IsReadable
                        ? $"unavailable-{baseline.ErrorCode ?? "unknown"}"
                        : baseline.Text.Length == 0 ? "readable-empty" : "readable-nonempty";
                }, operations, () => preparation?.CaptureEpoch);
                if (!baseline.IsReadable)
                {
                    return new(false, stage, null, null,
                        baseline.ErrorCode ?? "caption-baseline-unavailable");
                }

                stage = CaptureStartupStage.PreparingCaptureEpoch;
                await RunStageAsync(stage, async () =>
                {
                    preparation = await operations.PrepareCaptureAsync(
                        baseline.Text,
                        token);
                    token.ThrowIfCancellationRequested();
                    return "prepared";
                }, operations, () => preparation?.CaptureEpoch);

                stage = CaptureStartupStage.CreatingSession;
                await RunStageAsync(stage, async () =>
                {
                    session = await operations.CreateSessionAsync(token);
                    token.ThrowIfCancellationRequested();
                    return "created";
                }, operations, () => preparation?.CaptureEpoch);

                stage = CaptureStartupStage.ActivatingInput;
                CaptureInputActivation activation = default!;
                await RunStageAsync(stage, async () =>
                {
                    // Some Windows UI Automation operations cannot be cancelled
                    // after invocation. Await their result, then reject a stale
                    // startup request before it can resume capture.
                    activation = await operations.ActivateInputAsync(token);
                    inputChangedByRequest = activation.ChangedByRequest;
                    token.ThrowIfCancellationRequested();
                    return activation.IsActive
                        ? activation.ChangedByRequest ? "active-changed" : "active-existing"
                        : "failed";
                }, operations, () => preparation?.CaptureEpoch);
                if (!activation.IsActive)
                {
                    bool rollback = await TryAbortAsync(
                        operations, preparation, session, inputChangedByRequest);
                    return new(false, stage, session, preparation,
                        activation.ErrorCode ?? "capture-input-not-active",
                        RollbackSucceeded: rollback);
                }

                stage = CaptureStartupStage.RebindingCaptionSource;
                CaptionSourceSnapshot rebound = default!;
                await RunStageAsync(stage, async () =>
                {
                    rebound = await operations.RebindCaptionSourceAsync(token);
                    token.ThrowIfCancellationRequested();
                    return !rebound.IsReadable
                        ? $"unavailable-{rebound.ErrorCode ?? "unknown"}"
                        : rebound.Text.Length == 0 ? "readable-empty" : "readable-nonempty";
                }, operations, () => preparation?.CaptureEpoch);
                if (!rebound.IsReadable)
                {
                    bool rollback = await TryAbortAsync(
                        operations, preparation, session, inputChangedByRequest);
                    return new(false, stage, session, preparation,
                        rebound.ErrorCode ?? "caption-source-rebind-failed",
                        RollbackSucceeded: rollback);
                }

                token.ThrowIfCancellationRequested();
                stage = CaptureStartupStage.ResumingCapture;
                bool resumed = false;
                await RunStageAsync(stage, () =>
                {
                    resumed = operations.ResumeCapture(preparation!, session!.Id);
                    return Task.FromResult(resumed ? "resumed" : "superseded");
                }, operations, () => preparation?.CaptureEpoch);
                if (!resumed)
                {
                    bool rollback = await TryAbortAsync(
                        operations, preparation, session, inputChangedByRequest);
                    return new(false, stage, session, preparation,
                        "prepared-capture-superseded",
                        RollbackSucceeded: rollback);
                }

                operations.ReportStage?.Invoke(
                    CaptureStartupStage.Completed,
                    "started",
                    preparation!.CaptureEpoch,
                    0);
                return new(true, CaptureStartupStage.Completed, session, preparation);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                bool rollback = await TryAbortAsync(
                    operations, preparation, session, inputChangedByRequest);
                operations.ReportStage?.Invoke(
                    CaptureStartupStage.Cancelled,
                    "cancelled",
                    preparation?.CaptureEpoch,
                    0);
                return new(false, CaptureStartupStage.Cancelled, session, preparation,
                    "capture-startup-cancelled",
                    RollbackSucceeded: rollback);
            }
            catch (Exception exception)
            {
                bool rollback = await TryAbortAsync(
                    operations, preparation, session, inputChangedByRequest);
                operations.ReportStage?.Invoke(
                    stage,
                    "exception",
                    preparation?.CaptureEpoch,
                    0);
                return new(false, stage, session, preparation,
                    "capture-startup-exception",
                    exception,
                    rollback);
            }
        }

        private static async Task RunStageAsync(
            CaptureStartupStage stage,
            Func<Task<string>> action,
            CaptureStartupOperations operations,
            Func<long?> captureEpoch)
        {
            var stopwatch = Stopwatch.StartNew();
            string state = await action();
            stopwatch.Stop();
            operations.ReportStage?.Invoke(
                stage,
                state,
                captureEpoch(),
                stopwatch.ElapsedMilliseconds);
        }

        private static async Task<bool> TryAbortAsync(
            CaptureStartupOperations operations,
            PreparedCaptureSession? preparation,
            LectureSessionEntry? session,
            bool inputChangedByRequest)
        {
            if (preparation == null && session == null && !inputChangedByRequest)
                return true;

            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await operations.AbortAsync(
                    preparation,
                    session?.Id,
                    inputChangedByRequest,
                    deadline.Token);
                return true;
            }
            catch (Exception exception)
            {
                operations.ReportStage?.Invoke(
                    CaptureStartupStage.Cancelled,
                    $"rollback-{exception.GetType().Name}",
                    preparation?.CaptureEpoch,
                    0);
                return false;
            }
        }
    }
}
