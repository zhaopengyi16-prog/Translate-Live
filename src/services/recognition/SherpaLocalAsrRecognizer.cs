using SherpaOnnx;

namespace LiveCaptionsTranslator.services.recognition
{
    internal sealed class SherpaLocalAsrRecognizerFactory : ILocalAsrRecognizerFactory
    {
        public async ValueTask<ILocalAsrRecognizer> CreateAsync(
            LocalAsrModelFiles modelFiles,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(modelFiles);
            return await Task.Run<ILocalAsrRecognizer>(
                () => new SherpaLocalAsrRecognizer(modelFiles),
                token);
        }
    }

    internal sealed class SherpaLocalAsrRecognizer : ILocalAsrRecognizer
    {
        private readonly OnlineRecognizer recognizer;
        private readonly OnlineStream stream;
        private long nextSourceRevision;
        private bool inputFinished;
        private bool disposed;

        public SherpaLocalAsrRecognizer(LocalAsrModelFiles modelFiles)
        {
            ArgumentNullException.ThrowIfNull(modelFiles);

            var config = new OnlineRecognizerConfig
            {
                FeatConfig = new FeatureConfig
                {
                    SampleRate = 16000,
                    FeatureDim = 80
                },
                ModelConfig = new OnlineModelConfig
                {
                    Transducer = new OnlineTransducerModelConfig
                    {
                        Encoder = modelFiles.Encoder,
                        Decoder = modelFiles.Decoder,
                        Joiner = modelFiles.Joiner
                    },
                    Tokens = modelFiles.Tokens,
                    NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                    Provider = "cpu",
                    Debug = 0
                },
                DecodingMethod = "greedy_search",
                MaxActivePaths = 4,
                EnableEndpoint = 1,
                Rule1MinTrailingSilence = 2.4f,
                Rule2MinTrailingSilence = 1.2f,
                Rule3MinUtteranceLength = 20f
            };

            recognizer = new OnlineRecognizer(config);
            stream = recognizer.CreateStream();
        }

        public IReadOnlyList<LocalAsrHypothesis> Process(
            int sampleRate,
            float[] monoSamples)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (inputFinished)
                throw new InvalidOperationException("The recognizer input has already finished.");
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            ArgumentNullException.ThrowIfNull(monoSamples);
            if (monoSamples.Length == 0)
                return [];

            // sherpa-onnx accepts the actual device rate and performs its own
            // resampling to the feature extractor's configured sample rate.
            stream.AcceptWaveform(sampleRate, monoSamples);
            while (recognizer.IsReady(stream))
                recognizer.Decode(stream);

            string text = recognizer.GetResult(stream).Text?.Trim() ?? string.Empty;
            bool endpoint = recognizer.IsEndpoint(stream);
            if (endpoint)
            {
                recognizer.Reset(stream);
                if (text.Length == 0)
                    return [];

                return
                [
                    new LocalAsrHypothesis(
                        ++nextSourceRevision,
                        text,
                        IsFinal: true,
                        RecognitionEndpointReason.Silence)
                ];
            }

            if (text.Length == 0)
                return [];

            return
            [
                new LocalAsrHypothesis(
                    ++nextSourceRevision,
                    text,
                    IsFinal: false)
            ];
        }

        public LocalAsrHypothesis? Flush()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (inputFinished)
                return null;

            inputFinished = true;
            stream.InputFinished();
            while (recognizer.IsReady(stream))
                recognizer.Decode(stream);

            string text = recognizer.GetResult(stream).Text?.Trim() ?? string.Empty;
            return text.Length == 0
                ? null
                : new LocalAsrHypothesis(
                    ++nextSourceRevision,
                    text,
                    IsFinal: false);
        }

        public ValueTask DisposeAsync()
        {
            if (disposed)
                return ValueTask.CompletedTask;

            disposed = true;
            stream.Dispose();
            recognizer.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
