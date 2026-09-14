namespace LiveCaptionsTranslator.services.recognition
{
    internal enum LocalAsrAudioSource
    {
        Microphone,
        SystemAudio
    }

    internal enum LocalAudioSampleEncoding
    {
        Pcm,
        IeeeFloat
    }

    internal sealed record LocalAudioFormat(
        int SampleRate,
        int Channels,
        int BitsPerSample,
        int BlockAlign,
        LocalAudioSampleEncoding Encoding);

    internal sealed class LocalAudioDataAvailableEventArgs : EventArgs
    {
        public LocalAudioDataAvailableEventArgs(byte[] buffer, int bytesRecorded)
        {
            Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            if (bytesRecorded < 0 || bytesRecorded > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(bytesRecorded));
            BytesRecorded = bytesRecorded;
        }

        public byte[] Buffer { get; }
        public int BytesRecorded { get; }
    }

    internal sealed class LocalAudioCaptureStoppedEventArgs : EventArgs
    {
        public LocalAudioCaptureStoppedEventArgs(Exception? exception = null)
        {
            Exception = exception;
        }

        public Exception? Exception { get; }
    }

    internal interface ILocalAudioCapture : IDisposable
    {
        LocalAudioFormat Format { get; }
        event EventHandler<LocalAudioDataAvailableEventArgs>? DataAvailable;
        event EventHandler<LocalAudioCaptureStoppedEventArgs>? Stopped;
        void Start();
        void Stop();
    }

    internal interface ILocalAudioCaptureFactory
    {
        ILocalAudioCapture Create(LocalAsrAudioSource source);
    }

    internal sealed record LocalAsrHypothesis(
        long SourceRevision,
        string Text,
        bool IsFinal,
        RecognitionEndpointReason EndpointReason = RecognitionEndpointReason.None);

    internal interface ILocalAsrRecognizer : IAsyncDisposable
    {
        IReadOnlyList<LocalAsrHypothesis> Process(
            int sampleRate,
            float[] monoSamples);

        LocalAsrHypothesis? Flush();
    }

    internal interface ILocalAsrRecognizerFactory
    {
        ValueTask<ILocalAsrRecognizer> CreateAsync(
            LocalAsrModelFiles modelFiles,
            CancellationToken token);
    }

    internal sealed record LocalAsrError(
        string Code,
        string? ExceptionType = null,
        long Count = 0,
        IReadOnlyList<string>? Items = null);
}
