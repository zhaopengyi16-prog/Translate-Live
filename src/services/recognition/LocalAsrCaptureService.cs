using System.IO;
using System.Threading.Channels;

namespace LiveCaptionsTranslator.services.recognition
{
    internal sealed record LocalAudioChunk(
        LocalAudioFormat Format,
        byte[] Buffer);

    /// <summary>
    /// Owns exactly one WASAPI source, one sherpa recognizer and one stable
    /// recognition identity session. Audio callbacks only copy into a bounded
    /// queue; all conversion and inference run on one worker.
    /// </summary>
    internal sealed class LocalAsrCaptureService : IAsyncDisposable
    {
        internal const int AudioQueueCapacity = 32;
        public const string ModelRootEnvironmentVariable =
            "TRANSLATE_LIVE_LOCAL_ASR_MODEL_ROOT";

        private readonly SemaphoreSlim lifecycleGate = new(1, 1);
        private readonly ILocalAudioCaptureFactory captureFactory;
        private readonly ILocalAsrRecognizerFactory recognizerFactory;
        private readonly LocalAsrAudioPreprocessor audioPreprocessor = new();

        private ILocalAudioCapture? capture;
        private ILocalAsrRecognizer? recognizer;
        private LocalRecognitionSession? recognitionSession;
        private Channel<LocalAudioChunk>? audioQueue;
        private Task? workerTask;
        private Action<RecognitionEvent>? eventSink;
        private Action<LocalAsrError>? errorSink;
        private long activeSessionId;
        private long activeCaptureEpoch;
        private long processedSamples;
        private long nonSilentSamples;
        private int peakLevelPartsPerMillion;
        private int droppedFrames;
        private bool finalizeOnStop;

        public LocalAsrCaptureService()
            : this(
                new WasapiLocalAudioCaptureFactory(),
                new SherpaLocalAsrRecognizerFactory())
        {
        }

        internal LocalAsrCaptureService(
            ILocalAudioCaptureFactory captureFactory,
            ILocalAsrRecognizerFactory recognizerFactory)
        {
            this.captureFactory = captureFactory;
            this.recognizerFactory = recognizerFactory;
        }

        public bool IsRunning => Volatile.Read(ref workerTask) != null;
        public int DroppedFrames => Volatile.Read(ref droppedFrames);
        public long ProcessedSamples => Interlocked.Read(ref processedSamples);
        public long NonSilentSamples => Interlocked.Read(ref nonSilentSamples);
        public double PeakLevel => Volatile.Read(ref peakLevelPartsPerMillion) /
            1_000_000d;
        public LocalAudioFormat? ActiveFormat => capture?.Format;

        public async Task StartAsync(
            long sessionId,
            long captureEpoch,
            LocalAsrAudioSource source,
            string? configuredModelRoot,
            Action<RecognitionEvent> onRecognition,
            Action<LocalAsrError> onError,
            CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(onRecognition);
            ArgumentNullException.ThrowIfNull(onError);

            await lifecycleGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (workerTask != null)
                    throw new InvalidOperationException("Local ASR is already running.");

                string modelRoot = ResolveModelRoot(configuredModelRoot);
                LocalAsrModelFiles modelFiles = LocalAsrModelLocator.Resolve(modelRoot);
                ILocalAsrRecognizer createdRecognizer =
                    await recognizerFactory.CreateAsync(modelFiles, token)
                        .ConfigureAwait(false);
                ILocalAudioCapture? createdCapture = null;
                try
                {
                    token.ThrowIfCancellationRequested();
                    createdCapture = captureFactory.Create(source);
                    var session = new LocalRecognitionSession();
                    session.BeginSession(sessionId, captureEpoch);
                    var queue = Channel.CreateBounded<LocalAudioChunk>(
                        new BoundedChannelOptions(AudioQueueCapacity)
                        {
                            SingleReader = true,
                            SingleWriter = false,
                            FullMode = BoundedChannelFullMode.Wait,
                            AllowSynchronousContinuations = false
                        });

                    capture = createdCapture;
                    recognizer = createdRecognizer;
                    recognitionSession = session;
                    audioQueue = queue;
                    eventSink = onRecognition;
                    errorSink = onError;
                    activeSessionId = sessionId;
                    activeCaptureEpoch = captureEpoch;
                    droppedFrames = 0;
                    processedSamples = 0;
                    nonSilentSamples = 0;
                    peakLevelPartsPerMillion = 0;
                    audioPreprocessor.Reset();
                    finalizeOnStop = true;

                    createdCapture.DataAvailable += OnDataAvailable;
                    createdCapture.Stopped += OnCaptureStopped;
                    Task createdWorker = Task.Run(
                        () => ProcessAudioAsync(queue.Reader),
                        CancellationToken.None);
                    workerTask = createdWorker;
                    _ = ObserveWorkerCompletionAsync(createdWorker);
                    createdCapture.Start();
                }
                catch
                {
                    audioQueue?.Writer.TryComplete();
                    Task? failedWorker = workerTask;
                    if (failedWorker != null)
                        await failedWorker.ConfigureAwait(false);
                    if (createdCapture != null)
                    {
                        createdCapture.DataAvailable -= OnDataAvailable;
                        createdCapture.Stopped -= OnCaptureStopped;
                        createdCapture.Dispose();
                    }
                    await createdRecognizer.DisposeAsync().ConfigureAwait(false);
                    ResetRuntimeState();
                    throw;
                }
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        public async Task StopAsync(
            bool finalizeUnfinished,
            CancellationToken token = default)
        {
            await lifecycleGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                Task? stoppingWorker = workerTask;
                if (stoppingWorker == null)
                    return;

                finalizeOnStop = finalizeUnfinished;
                ILocalAudioCapture? stoppingCapture = capture;
                if (stoppingCapture != null)
                {
                    stoppingCapture.DataAvailable -= OnDataAvailable;
                    stoppingCapture.Stopped -= OnCaptureStopped;
                    try
                    {
                        stoppingCapture.Stop();
                    }
                    catch (Exception exception)
                    {
                        ReportError("audio-stop-failed", exception);
                    }
                }
                audioQueue?.Writer.TryComplete();
                await stoppingWorker.WaitAsync(token).ConfigureAwait(false);

                stoppingCapture?.Dispose();
                if (recognizer != null)
                    await recognizer.DisposeAsync().ConfigureAwait(false);
                ResetRuntimeState();
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        private void OnDataAvailable(
            object? sender,
            LocalAudioDataAvailableEventArgs args)
        {
            ChannelWriter<LocalAudioChunk>? writer = audioQueue?.Writer;
            LocalAudioFormat? format = capture?.Format;
            if (writer == null || format == null || args.BytesRecorded == 0)
                return;

            var copy = new byte[args.BytesRecorded];
            Buffer.BlockCopy(args.Buffer, 0, copy, 0, copy.Length);
            if (writer.TryWrite(new LocalAudioChunk(format, copy)))
                return;

            int dropped = Interlocked.Increment(ref droppedFrames);
            if (dropped == 1)
                ReportError("audio-queue-overflow", null, dropped);
        }

        private void OnCaptureStopped(
            object? sender,
            LocalAudioCaptureStoppedEventArgs args)
        {
            if (args.Exception != null)
                ReportError("audio-capture-stopped", args.Exception);
            audioQueue?.Writer.TryComplete(args.Exception);
        }

        private async Task ProcessAudioAsync(ChannelReader<LocalAudioChunk> reader)
        {
            try
            {
                await foreach (LocalAudioChunk chunk in reader.ReadAllAsync()
                                   .ConfigureAwait(false))
                {
                    float[] samples = LocalAudioSampleConverter.ConvertToMonoFloat(
                        chunk.Buffer,
                        chunk.Format);
                    Interlocked.Add(ref processedSamples, samples.LongLength);
                    long audible = 0;
                    float peak = 0;
                    foreach (float sample in samples)
                    {
                        float level = Math.Abs(sample);
                        if (level > 0.001f)
                            audible++;
                        if (level > peak)
                            peak = level;
                    }
                    Interlocked.Add(ref nonSilentSamples, audible);
                    int peakPpm = (int)Math.Round(
                        Math.Clamp(peak, 0f, 1f) * 1_000_000d);
                    int observedPeak;
                    while (peakPpm > (observedPeak = Volatile.Read(
                               ref peakLevelPartsPerMillion)) &&
                           Interlocked.CompareExchange(
                               ref peakLevelPartsPerMillion,
                               peakPpm,
                               observedPeak) != observedPeak)
                    {
                    }
                    audioPreprocessor.ProcessInPlace(samples);
                    foreach (LocalAsrHypothesis hypothesis in
                             recognizer!.Process(chunk.Format.SampleRate, samples))
                    {
                        AcceptHypothesis(hypothesis);
                    }
                }

                if (finalizeOnStop)
                {
                    LocalAsrHypothesis? tail = recognizer?.Flush();
                    if (tail != null)
                        AcceptHypothesis(tail);
                    RecognitionAcceptance final =
                        recognitionSession?.Stop(finalizeUnfinished: true)
                        ?? new RecognitionAcceptance(
                            RecognitionAcceptanceStatus.RejectedNoActiveSession);
                    Publish(final);
                }
                else
                {
                    recognitionSession?.Stop(finalizeUnfinished: false);
                }
            }
            catch (Exception exception)
            {
                ReportError("recognition-worker-failed", exception);
            }
        }

        private async Task ObserveWorkerCompletionAsync(Task completedWorker)
        {
            try
            {
                await completedWorker.ConfigureAwait(false);
            }
            catch
            {
                // ProcessAudioAsync converts worker failures into safe error codes.
            }

            await lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(workerTask, completedWorker))
                    return;

                ILocalAudioCapture? endedCapture = capture;
                if (endedCapture != null)
                {
                    endedCapture.DataAvailable -= OnDataAvailable;
                    endedCapture.Stopped -= OnCaptureStopped;
                    try
                    {
                        endedCapture.Stop();
                    }
                    catch (Exception exception)
                    {
                        ReportError("audio-stop-failed", exception);
                    }
                    endedCapture.Dispose();
                }

                if (recognizer != null)
                    await recognizer.DisposeAsync().ConfigureAwait(false);
                ResetRuntimeState();
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        private void AcceptHypothesis(LocalAsrHypothesis hypothesis)
        {
            if (recognitionSession == null)
                return;
            RecognitionAcceptance acceptance = recognitionSession.Accept(
                new RecognitionUpdate(
                    activeSessionId,
                    activeCaptureEpoch,
                    hypothesis.SourceRevision,
                    DateTimeOffset.UtcNow,
                    hypothesis.Text,
                    hypothesis.IsFinal,
                    hypothesis.EndpointReason));
            Publish(acceptance);
        }

        private void Publish(RecognitionAcceptance acceptance)
        {
            if (acceptance.IsAccepted)
                eventSink?.Invoke(acceptance.Event!);
        }

        private static string ResolveModelRoot(string? configuredModelRoot)
        {
            if (!string.IsNullOrWhiteSpace(configuredModelRoot))
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(
                    configuredModelRoot.Trim()));
            }

            string? environmentRoot = Environment.GetEnvironmentVariable(
                ModelRootEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(environmentRoot))
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(
                    environmentRoot.Trim()));
            }

            return Path.Combine(AppContext.BaseDirectory, "Models");
        }

        private void ReportError(
            string code,
            Exception? exception,
            long count = 0)
        {
            errorSink?.Invoke(new LocalAsrError(
                code,
                exception?.GetType().Name,
                count));
        }

        private void ResetRuntimeState()
        {
            capture = null;
            recognizer = null;
            recognitionSession = null;
            audioQueue = null;
            workerTask = null;
            eventSink = null;
            errorSink = null;
            activeSessionId = 0;
            activeCaptureEpoch = 0;
            finalizeOnStop = false;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync(finalizeUnfinished: false).ConfigureAwait(false);
            }
            finally
            {
                lifecycleGate.Dispose();
            }
        }
    }
}
