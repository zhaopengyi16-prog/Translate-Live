using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.services;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class CaptureStartupSequenceTests
    {
        [TestMethod]
        public async Task EmptyBaselineLetsTheImmediateFirstMicrophoneSentenceThrough()
        {
            var runtime = new FakeRuntime
            {
                Baseline = string.Empty,
                PostActivationSnapshot = "The microphone is working."
            };

            CaptureStartupResult result = await runtime.StartAsync();
            IReadOnlyList<RecordedCaption> recorded = runtime.ObserveAt(900);

            Assert.IsTrue(result.IsStarted);
            Assert.IsNotNull(runtime.FirstUpdate?.CurrentSegment);
            Assert.AreEqual(
                "The microphone is working.",
                runtime.FirstUpdate.CurrentSegment.Text);
            Assert.HasCount(1, recorded);
            Assert.AreEqual(
                runtime.FirstUpdate.CurrentSegment.Id,
                recorded[0].Segment.Id);
        }

        [TestMethod]
        public async Task OldVisibleWindowIsExcludedButTheFirstNewSentenceIsKept()
        {
            var runtime = new FakeRuntime
            {
                Baseline = "Old classroom sentence.",
                PostActivationSnapshot =
                    "Old classroom sentence. This is the first microphone sentence."
            };

            CaptureStartupResult result = await runtime.StartAsync();
            IReadOnlyList<RecordedCaption> recorded = runtime.ObserveAt(900);

            Assert.IsTrue(result.IsStarted);
            Assert.HasCount(1, recorded);
            Assert.AreEqual(
                "This is the first microphone sentence.",
                recorded[0].Segment.Text);
            Assert.IsFalse(recorded.Any(item => item.Segment.Text.Contains(
                "Old classroom",
                StringComparison.Ordinal)));
        }

        [TestMethod]
        public async Task CaptionPreparationAndBaselineReadPrecedeInputActivation()
        {
            var runtime = new FakeRuntime
            {
                Baseline = "Existing window.",
                PostActivationSnapshot = "Existing window. New speech."
            };

            CaptureStartupResult result = await runtime.StartAsync();

            Assert.IsTrue(result.IsStarted);
            CollectionAssert.AreEqual(
                new[]
                {
                    "end-previous",
                    "prepare-source",
                    "read-baseline",
                    "prepare-epoch",
                    "create-session",
                    "activate-input",
                    "rebind-source",
                    "resume"
                },
                runtime.Events.ToArray());
        }

        [TestMethod]
        public async Task AlreadyActiveMicrophoneStillGetsANewCaptureBoundary()
        {
            var runtime = new FakeRuntime
            {
                Baseline = "Previous speech.",
                PostActivationSnapshot = "Previous speech. New classroom speech.",
                Activation = new CaptureInputActivation(true, false)
            };

            CaptureStartupResult result = await runtime.StartAsync();

            Assert.IsTrue(result.IsStarted);
            Assert.AreEqual(1, runtime.PrepareCount);
            Assert.AreEqual(
                "New classroom speech.",
                runtime.FirstUpdate?.CurrentSegment?.Text);
        }

        [TestMethod]
        public async Task SecondStartupAndModeSwitchUseANewPreparedBoundary()
        {
            var runtime = new FakeRuntime
            {
                Baseline = string.Empty,
                PostActivationSnapshot = "First classroom sentence."
            };

            CaptureStartupResult first = await runtime.StartAsync();
            Guid firstId = runtime.FirstUpdate!.CurrentSegment!.Id;
            runtime.Baseline = "First classroom sentence.";
            runtime.PostActivationSnapshot =
                "First classroom sentence. First sentence after switching modes.";
            runtime.Activation = new CaptureInputActivation(true, false);
            CaptureStartupResult second = await runtime.StartAsync();

            Assert.IsTrue(first.IsStarted);
            Assert.IsTrue(second.IsStarted);
            Assert.AreEqual(2, runtime.PrepareCount);
            Assert.AreNotEqual(first.Preparation, second.Preparation);
            Assert.AreNotEqual(firstId, runtime.FirstUpdate!.CurrentSegment!.Id);
            Assert.AreEqual(
                "First sentence after switching modes.",
                runtime.FirstUpdate.CurrentSegment.Text);
        }

        [TestMethod]
        public async Task UnreadableBaselineNeverFallsBackToAnEmptyBaseline()
        {
            var runtime = new FakeRuntime
            {
                BaselineReadable = false,
                BaselineError = "captions-node-unavailable"
            };

            CaptureStartupResult result = await runtime.StartAsync();

            Assert.IsFalse(result.IsStarted);
            Assert.AreEqual(CaptureStartupStage.ReadingBaseline, result.Stage);
            Assert.AreEqual(0, runtime.PrepareCount);
            Assert.IsFalse(runtime.Events.Contains("create-session"));
            Assert.IsFalse(runtime.Events.Contains("activate-input"));
            Assert.IsFalse(runtime.Aborted);
        }

        [TestMethod]
        public async Task ReadableEmptyRebuiltNodeDoesNotWaitForSpeechOrReplaceTheBaseline()
        {
            var runtime = new FakeRuntime
            {
                Baseline = "Old visible sentence.",
                PostActivationSnapshot = "Old visible sentence.",
                ReboundSnapshot = string.Empty
            };

            CaptureStartupResult result = await runtime.StartAsync();
            LiveCaptionUpdate firstSpeech = runtime.ObserveSnapshot(
                "Old visible sentence. Speech after the ready state.",
                200);

            Assert.IsTrue(result.IsStarted);
            Assert.AreEqual(
                "Speech after the ready state.",
                firstSpeech.CurrentSegment?.Text);
        }

        [TestMethod]
        public async Task CaptionNodeRebindFailureAbortsTheEmptySession()
        {
            var runtime = new FakeRuntime
            {
                Baseline = string.Empty,
                RebindReadable = false,
                RebindError = "captions-node-stale"
            };

            CaptureStartupResult result = await runtime.StartAsync();

            Assert.IsFalse(result.IsStarted);
            Assert.AreEqual(CaptureStartupStage.RebindingCaptionSource, result.Stage);
            Assert.IsTrue(runtime.Aborted);
            Assert.IsTrue(runtime.AbortInputChanged);
            Assert.IsFalse(runtime.Resumed);
        }

        [TestMethod]
        public async Task InputActivationFailureIsExplainableAndRetryable()
        {
            var runtime = new FakeRuntime
            {
                Activation = new CaptureInputActivation(
                    false,
                    false,
                    "microphone-toggle-disabled")
            };

            CaptureStartupResult result = await runtime.StartAsync();

            Assert.IsFalse(result.IsStarted);
            Assert.AreEqual(CaptureStartupStage.ActivatingInput, result.Stage);
            Assert.AreEqual("microphone-toggle-disabled", result.ErrorCode);
            Assert.IsTrue(runtime.Aborted);
            Assert.IsFalse(runtime.Resumed);
        }

        [TestMethod]
        public async Task CancelledActivationCannotResumeAfterItsLateResult()
        {
            var runtime = new FakeRuntime
            {
                PostActivationSnapshot = "Late first sentence.",
                ActivationRelease = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously),
                ActivationStarted = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            };
            using var cancellation = new CancellationTokenSource();

            Task<CaptureStartupResult> pending = runtime.StartAsync(cancellation.Token);
            await runtime.ActivationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            runtime.ActivationRelease.TrySetResult();
            CaptureStartupResult result = await pending;

            Assert.IsFalse(result.IsStarted);
            Assert.AreEqual(CaptureStartupStage.Cancelled, result.Stage);
            Assert.IsTrue(runtime.Aborted);
            Assert.IsTrue(runtime.AbortInputChanged);
            Assert.IsFalse(runtime.Resumed);
        }

        private sealed class FakeRuntime
        {
            private static readonly DateTimeOffset Start =
                new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);
            private readonly LiveCaptionIdentityResolver source =
                new(splitLongDrafts: false);
            private readonly CaptionRecordingPolicy recordingPolicy = new();
            private long elapsedMilliseconds = 100;

            public string Baseline { get; set; } = string.Empty;
            public bool BaselineReadable { get; init; } = true;
            public string? BaselineError { get; init; }
            public string PostActivationSnapshot { get; set; } = string.Empty;
            public string? ReboundSnapshot { get; init; }
            public bool RebindReadable { get; init; } = true;
            public string? RebindError { get; init; }
            public CaptureInputActivation Activation { get; set; } =
                new(true, true);
            public TaskCompletionSource? ActivationStarted { get; init; }
            public TaskCompletionSource? ActivationRelease { get; init; }
            public List<string> Events { get; } = [];
            public int PrepareCount { get; private set; }
            public bool Resumed { get; private set; }
            public bool Aborted { get; private set; }
            public bool AbortInputChanged { get; private set; }
            public LiveCaptionUpdate? FirstUpdate { get; private set; }

            public Task<CaptureStartupResult> StartAsync(
                CancellationToken token = default)
            {
                var operations = new CaptureStartupOperations
                {
                    EndPreviousSessionAsync = _ =>
                    {
                        Events.Add("end-previous");
                        return Task.CompletedTask;
                    },
                    PrepareCaptionSourceAsync = _ =>
                    {
                        Events.Add("prepare-source");
                        return Task.FromResult(new CaptureStepResult(true));
                    },
                    ReadBaselineAsync = _ =>
                    {
                        Events.Add("read-baseline");
                        return Task.FromResult(BaselineReadable
                            ? CaptionSourceSnapshot.Readable(Baseline)
                            : CaptionSourceSnapshot.Unavailable(
                                BaselineError ?? "baseline-unavailable"));
                    },
                    PrepareCaptureAsync = (baseline, _) =>
                    {
                        Events.Add("prepare-epoch");
                        PrepareCount++;
                        source.StartFromCurrentSnapshot(baseline, Start);
                        recordingPolicy.Reset();
                        return Task.FromResult(new PreparedCaptureSession(
                            CaptureEpoch: 1 + PrepareCount,
                            PreparationId: PrepareCount));
                    },
                    CreateSessionAsync = _ =>
                    {
                        Events.Add("create-session");
                        return Task.FromResult(new LectureSessionEntry
                        {
                            Id = 16 + PrepareCount,
                            StartedAt = Start,
                            Mode = "线下课堂 · 麦克风",
                            ApiUsed = "Fake",
                            TargetLanguage = "zh-CN"
                        });
                    },
                    ActivateInputAsync = async _ =>
                    {
                        Events.Add("activate-input");
                        ActivationStarted?.TrySetResult();
                        if (ActivationRelease != null)
                            await ActivationRelease.Task;
                        return Activation;
                    },
                    RebindCaptionSourceAsync = _ =>
                    {
                        Events.Add("rebind-source");
                        return Task.FromResult(RebindReadable
                            ? CaptionSourceSnapshot.Readable(
                                ReboundSnapshot ?? PostActivationSnapshot)
                            : CaptionSourceSnapshot.Unavailable(
                                RebindError ?? "rebind-unavailable"));
                    },
                    ResumeCapture = (_, _) =>
                    {
                        Events.Add("resume");
                        Resumed = true;
                        FirstUpdate = Process(PostActivationSnapshot, elapsedMilliseconds);
                        return true;
                    },
                    AbortAsync = (_, _, inputChanged, _) =>
                    {
                        Events.Add("abort");
                        Aborted = true;
                        AbortInputChanged = inputChanged;
                        return Task.CompletedTask;
                    }
                };
                return CaptureStartupSequence.RunAsync(operations, token);
            }

            public IReadOnlyList<RecordedCaption> ObserveAt(long milliseconds)
            {
                elapsedMilliseconds = milliseconds;
                LiveCaptionUpdate update = source.Process(
                    PostActivationSnapshot,
                    Start.AddMilliseconds(milliseconds));
                return recordingPolicy.Observe(
                    update,
                    TimeSpan.FromMilliseconds(milliseconds));
            }

            public LiveCaptionUpdate ObserveSnapshot(string snapshot, long milliseconds)
            {
                elapsedMilliseconds = milliseconds;
                LiveCaptionUpdate update = source.Process(
                    snapshot,
                    Start.AddMilliseconds(milliseconds));
                recordingPolicy.Observe(
                    update,
                    TimeSpan.FromMilliseconds(milliseconds));
                return update;
            }

            private LiveCaptionUpdate Process(string snapshot, long milliseconds)
            {
                LiveCaptionUpdate update = source.Process(
                    snapshot,
                    Start.AddMilliseconds(milliseconds));
                recordingPolicy.Observe(
                    update,
                    TimeSpan.FromMilliseconds(milliseconds));
                return update;
            }
        }
    }
}
