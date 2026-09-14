using LiveCaptionsTranslator.services.recognition;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class LocalAsrCaptureServiceTests
    {
        [TestMethod]
        public void StereoPcm16IsMixedToMonoWithoutChangingFrameCount()
        {
            var format = new LocalAudioFormat(
                SampleRate: 16000,
                Channels: 2,
                BitsPerSample: 16,
                BlockAlign: 4,
                LocalAudioSampleEncoding.Pcm);
            byte[] bytes =
            [
                0x00, 0x40, 0x00, 0xC0,
                0xFF, 0x7F, 0xFF, 0x7F
            ];

            float[] samples = LocalAudioSampleConverter.ConvertToMonoFloat(
                bytes,
                format);

            Assert.HasCount(2, samples);
            Assert.AreEqual(0f, samples[0], 0.0001f);
            Assert.AreEqual(0.9999f, samples[1], 0.001f);
        }

        [TestMethod]
        public void AudioPreprocessorBoostsQuietSpeechButLeavesSilenceAlone()
        {
            var preprocessor = new LocalAsrAudioPreprocessor();
            var silence = new float[1600];
            preprocessor.ProcessInPlace(silence);
            Assert.AreEqual(1f, preprocessor.CurrentGain);
            Assert.IsTrue(silence.All(sample => sample == 0));

            float[] quietSpeech = Enumerable
                .Range(0, 1600)
                .Select(index => index % 2 == 0 ? 0.003f : -0.003f)
                .ToArray();
            preprocessor.ProcessInPlace(quietSpeech);

            Assert.IsGreaterThan(1f, preprocessor.CurrentGain);
            Assert.IsLessThanOrEqualTo(
                LocalAsrAudioPreprocessor.MaximumGain,
                preprocessor.CurrentGain);
            Assert.IsTrue(quietSpeech.Max(Math.Abs) > 0.003f);
            Assert.IsTrue(quietSpeech.All(sample => sample is >= -1f and <= 1f));
        }

        [TestMethod]
        public void MissingModelFilesAreReportedWithoutStartingAudio()
        {
            string root = CreateTemporaryDirectory();
            try
            {
                var captureFactory = new FakeCaptureFactory();
                var recognizerFactory = new FakeRecognizerFactory([]);
                var service = new LocalAsrCaptureService(
                    captureFactory,
                    recognizerFactory);

                LocalAsrModelValidationException exception =
                    Assert.ThrowsExactly<LocalAsrModelValidationException>(() =>
                        service.StartAsync(
                            1,
                            1,
                            LocalAsrAudioSource.Microphone,
                            root,
                            _ => { },
                            _ => { }).GetAwaiter().GetResult());

                Assert.HasCount(4, exception.InvalidFiles);
                Assert.AreEqual(0, captureFactory.CreateCount);
                Assert.IsFalse(service.IsRunning);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public async Task PartialAndFinalUseOneIdentityAndOneFinalEvent()
        {
            string root = CreateModelDirectory();
            try
            {
                var captureFactory = new FakeCaptureFactory();
                var recognizerFactory = new FakeRecognizerFactory(
                [
                    [new LocalAsrHypothesis(1, "hello", false)],
                    [new LocalAsrHypothesis(
                        2,
                        "hello world",
                        true,
                        RecognitionEndpointReason.Silence)]
                ]);
                await using var service = new LocalAsrCaptureService(
                    captureFactory,
                    recognizerFactory);
                var events = new List<RecognitionEvent>();

                await service.StartAsync(
                    11,
                    5,
                    LocalAsrAudioSource.Microphone,
                    root,
                    item =>
                    {
                        lock (events)
                            events.Add(item);
                    },
                    error => Assert.Fail(error.Code));
                captureFactory.Capture.EmitFrame();
                captureFactory.Capture.EmitFrame();
                await WaitUntilAsync(() =>
                {
                    lock (events)
                        return events.Count == 2;
                });
                await service.StopAsync(finalizeUnfinished: true);

                RecognitionEvent[] snapshot;
                lock (events)
                    snapshot = events.ToArray();
                Assert.HasCount(2, snapshot);
                Assert.AreEqual(snapshot[0].SegmentId, snapshot[1].SegmentId);
                CollectionAssert.AreEqual(
                    new[] { 0, 1 },
                    snapshot.Select(item => item.Revision).ToArray());
                Assert.AreEqual(1, snapshot.Count(item => item.IsFinal));
                Assert.AreEqual("hello world", snapshot[^1].Text);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public async Task TwoFinalsWithSameTextRemainTwoRealOccurrences()
        {
            string root = CreateModelDirectory();
            try
            {
                var captureFactory = new FakeCaptureFactory();
                var recognizerFactory = new FakeRecognizerFactory(
                [
                    [new LocalAsrHypothesis(
                        1,
                        "repeat me",
                        true,
                        RecognitionEndpointReason.Silence)],
                    [new LocalAsrHypothesis(
                        2,
                        "repeat me",
                        true,
                        RecognitionEndpointReason.Silence)]
                ]);
                await using var service = new LocalAsrCaptureService(
                    captureFactory,
                    recognizerFactory);
                var events = new List<RecognitionEvent>();

                await service.StartAsync(
                    12,
                    6,
                    LocalAsrAudioSource.SystemAudio,
                    root,
                    item =>
                    {
                        lock (events)
                            events.Add(item);
                    },
                    error => Assert.Fail(error.Code));
                captureFactory.Capture.EmitFrame();
                captureFactory.Capture.EmitFrame();
                await WaitUntilAsync(() =>
                {
                    lock (events)
                        return events.Count == 2;
                });
                await service.StopAsync(finalizeUnfinished: true);

                RecognitionEvent[] snapshot;
                lock (events)
                    snapshot = events.ToArray();
                Assert.HasCount(2, snapshot);
                Assert.AreNotEqual(snapshot[0].SegmentId, snapshot[1].SegmentId);
                CollectionAssert.AreEqual(
                    new long[] { 1, 2 },
                    snapshot.Select(item => item.Sequence).ToArray());
                Assert.IsTrue(snapshot.All(item => item.IsFinal));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public async Task StopFinalizesOneUnfinishedUtterance()
        {
            string root = CreateModelDirectory();
            try
            {
                var captureFactory = new FakeCaptureFactory();
                var recognizerFactory = new FakeRecognizerFactory(
                [
                    [new LocalAsrHypothesis(1, "unfinished sentence", false)]
                ],
                flush: new LocalAsrHypothesis(
                    2,
                    "unfinished sentence",
                    false));
                await using var service = new LocalAsrCaptureService(
                    captureFactory,
                    recognizerFactory);
                var events = new List<RecognitionEvent>();

                await service.StartAsync(
                    13,
                    7,
                    LocalAsrAudioSource.Microphone,
                    root,
                    item =>
                    {
                        lock (events)
                            events.Add(item);
                    },
                    error => Assert.Fail(error.Code));
                captureFactory.Capture.EmitFrame();
                await WaitUntilAsync(() =>
                {
                    lock (events)
                        return events.Count == 1;
                });
                await service.StopAsync(finalizeUnfinished: true);

                RecognitionEvent[] snapshot;
                lock (events)
                    snapshot = events.ToArray();
                Assert.HasCount(2, snapshot);
                Assert.AreEqual(snapshot[0].SegmentId, snapshot[1].SegmentId);
                Assert.IsTrue(snapshot[^1].IsFinal);
                Assert.AreEqual(
                    RecognitionEndpointReason.SessionStopped,
                    snapshot[^1].EndpointReason);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public async Task HundredContinuousFinalsProduceExactlyHundredIdentities()
        {
            const int utteranceCount = 100;
            string root = CreateModelDirectory();
            try
            {
                IReadOnlyList<LocalAsrHypothesis>[] outputs = Enumerable
                    .Range(1, utteranceCount)
                    .Select(index => (IReadOnlyList<LocalAsrHypothesis>)
                    [
                        new LocalAsrHypothesis(
                            index,
                            $"Continuous sentence {index}.",
                            true,
                            RecognitionEndpointReason.Silence)
                    ])
                    .ToArray();
                var captureFactory = new FakeCaptureFactory();
                var recognizerFactory = new FakeRecognizerFactory(outputs);
                await using var service = new LocalAsrCaptureService(
                    captureFactory,
                    recognizerFactory);
                var events = new List<RecognitionEvent>(utteranceCount);

                await service.StartAsync(
                    14,
                    8,
                    LocalAsrAudioSource.Microphone,
                    root,
                    item =>
                    {
                        lock (events)
                            events.Add(item);
                    },
                    error => Assert.Fail(error.Code));
                for (int index = 0; index < utteranceCount; index++)
                {
                    captureFactory.Capture.EmitFrame();
                    int expected = index + 1;
                    await WaitUntilAsync(() =>
                    {
                        lock (events)
                            return events.Count >= expected;
                    });
                }
                await service.StopAsync(finalizeUnfinished: true);

                RecognitionEvent[] snapshot;
                lock (events)
                    snapshot = events.ToArray();
                Assert.HasCount(utteranceCount, snapshot);
                Assert.AreEqual(
                    utteranceCount,
                    snapshot.Select(item => item.SegmentId).Distinct().Count());
                Assert.AreEqual(utteranceCount, snapshot.Count(item => item.IsFinal));
                Assert.AreEqual(0, service.DroppedFrames);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public async Task FailedAudioStartCleansWorkerAndAllowsRetry()
        {
            string root = CreateModelDirectory();
            try
            {
                var captureFactory = new FakeCaptureFactory();
                captureFactory.Capture.ThrowOnNextStart = true;
                var recognizerFactory = new FakeRecognizerFactory([]);
                await using var service = new LocalAsrCaptureService(
                    captureFactory,
                    recognizerFactory);

                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    service.StartAsync(
                        15,
                        9,
                        LocalAsrAudioSource.Microphone,
                        root,
                        _ => { },
                        _ => { }));
                Assert.IsFalse(service.IsRunning);

                await service.StartAsync(
                    15,
                    10,
                    LocalAsrAudioSource.Microphone,
                    root,
                    _ => { },
                    error => Assert.Fail(error.Code));
                Assert.IsTrue(service.IsRunning);
                await service.StopAsync(finalizeUnfinished: false);
                Assert.IsFalse(service.IsRunning);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public async Task UnexpectedCaptureEndCleansRuntimeState()
        {
            string root = CreateModelDirectory();
            try
            {
                var captureFactory = new FakeCaptureFactory();
                var recognizerFactory = new FakeRecognizerFactory([]);
                await using var service = new LocalAsrCaptureService(
                    captureFactory,
                    recognizerFactory);

                await service.StartAsync(
                    16,
                    11,
                    LocalAsrAudioSource.Microphone,
                    root,
                    _ => { },
                    error => Assert.Fail(error.Code));
                captureFactory.Capture.EmitStopped();
                await WaitUntilAsync(() => !service.IsRunning);

                Assert.IsFalse(service.IsRunning);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!condition())
                await Task.Delay(10, timeout.Token);
        }

        private static string CreateModelDirectory()
        {
            string root = CreateTemporaryDirectory();
            string model = Path.Combine(
                root,
                LocalAsrModelLocator.DefaultModelDirectoryName);
            Directory.CreateDirectory(model);
            foreach (string file in new[]
                     {
                         LocalAsrModelLocator.EncoderFileName,
                         LocalAsrModelLocator.DecoderFileName,
                         LocalAsrModelLocator.JoinerFileName,
                         LocalAsrModelLocator.TokensFileName
                     })
            {
                File.WriteAllText(Path.Combine(model, file), "test");
            }
            return root;
        }

        private static string CreateTemporaryDirectory()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "TranslateLive.LocalAsr.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private sealed class FakeCaptureFactory : ILocalAudioCaptureFactory
        {
            public FakeCapture Capture { get; } = new();
            public int CreateCount { get; private set; }

            public ILocalAudioCapture Create(LocalAsrAudioSource source)
            {
                CreateCount++;
                return Capture;
            }
        }

        private sealed class FakeCapture : ILocalAudioCapture
        {
            public bool ThrowOnNextStart { get; set; }
            public LocalAudioFormat Format { get; } = new(
                16000,
                1,
                16,
                2,
                LocalAudioSampleEncoding.Pcm);

            public event EventHandler<LocalAudioDataAvailableEventArgs>? DataAvailable;
            public event EventHandler<LocalAudioCaptureStoppedEventArgs>? Stopped;

            public void Start()
            {
                if (!ThrowOnNextStart)
                    return;

                ThrowOnNextStart = false;
                throw new InvalidOperationException("Synthetic capture start failure.");
            }
            public void Stop() => Stopped?.Invoke(
                this,
                new LocalAudioCaptureStoppedEventArgs());
            public void Dispose() { }

            public void EmitStopped() => Stopped?.Invoke(
                this,
                new LocalAudioCaptureStoppedEventArgs());

            public void EmitFrame()
            {
                DataAvailable?.Invoke(
                    this,
                    new LocalAudioDataAvailableEventArgs(new byte[320], 320));
            }
        }

        private sealed class FakeRecognizerFactory : ILocalAsrRecognizerFactory
        {
            private readonly Queue<IReadOnlyList<LocalAsrHypothesis>> outputs;
            private readonly LocalAsrHypothesis? flush;

            public FakeRecognizerFactory(
                IEnumerable<IReadOnlyList<LocalAsrHypothesis>> outputs,
                LocalAsrHypothesis? flush = null)
            {
                this.outputs = new Queue<IReadOnlyList<LocalAsrHypothesis>>(outputs);
                this.flush = flush;
            }

            public ValueTask<ILocalAsrRecognizer> CreateAsync(
                LocalAsrModelFiles modelFiles,
                CancellationToken token)
            {
                return ValueTask.FromResult<ILocalAsrRecognizer>(
                    new FakeRecognizer(outputs, flush));
            }
        }

        private sealed class FakeRecognizer : ILocalAsrRecognizer
        {
            private readonly Queue<IReadOnlyList<LocalAsrHypothesis>> outputs;
            private readonly LocalAsrHypothesis? flush;

            public FakeRecognizer(
                Queue<IReadOnlyList<LocalAsrHypothesis>> outputs,
                LocalAsrHypothesis? flush)
            {
                this.outputs = outputs;
                this.flush = flush;
            }

            public IReadOnlyList<LocalAsrHypothesis> Process(
                int sampleRate,
                float[] monoSamples)
            {
                return outputs.Count > 0 ? outputs.Dequeue() : [];
            }

            public LocalAsrHypothesis? Flush() => flush;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
