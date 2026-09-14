using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LiveCaptionsTranslator.services.recognition
{
    internal sealed class WasapiLocalAudioCaptureFactory : ILocalAudioCaptureFactory
    {
        private const int CaptureBufferMilliseconds = 50;
        private readonly bool includeCurrentProcessAudio;

        public WasapiLocalAudioCaptureFactory(
            bool includeCurrentProcessAudio = false)
        {
            this.includeCurrentProcessAudio = includeCurrentProcessAudio;
        }

        public ILocalAudioCapture Create(LocalAsrAudioSource source)
        {
            var builder = new WasapiRecorderBuilder()
                .WithSharedMode()
                .WithEventSync()
                .WithBufferLength(CaptureBufferMilliseconds)
                .WithMmcssThreadPriority("Audio");
            if (source == LocalAsrAudioSource.SystemAudio)
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                {
                    throw new PlatformNotSupportedException(
                        "Local system-audio ASR requires Windows 10 version 2004 or later.");
                }
                builder.WithProcessLoopback(
                    checked((uint)Environment.ProcessId),
                    includeCurrentProcessAudio
                        ? ProcessLoopbackMode.IncludeTargetProcessTree
                        : ProcessLoopbackMode.ExcludeTargetProcessTree);
            }
            else if (source == LocalAsrAudioSource.Microphone)
            {
                builder.WithFormat(
                    WaveFormat.CreateIeeeFloatWaveFormat(16000, 1));
            }
            else
                throw new ArgumentOutOfRangeException(nameof(source));

            WasapiRecorder recorder = source == LocalAsrAudioSource.SystemAudio
                ? builder.BuildAsync().GetAwaiter().GetResult()
                : builder.Build();
            return new WasapiLocalAudioCapture(recorder);
        }
    }

    internal sealed class WasapiLocalAudioCapture : ILocalAudioCapture
    {
        private readonly WasapiRecorder capture;
        private bool disposed;

        public WasapiLocalAudioCapture(WasapiRecorder capture)
        {
            this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
            Format = LocalAudioSampleConverter.Describe(capture.WaveFormat);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;
        }

        public LocalAudioFormat Format { get; }

        public event EventHandler<LocalAudioDataAvailableEventArgs>? DataAvailable;
        public event EventHandler<LocalAudioCaptureStoppedEventArgs>? Stopped;

        public void Start()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            capture.StartRecording();
        }

        public void Stop()
        {
            if (!disposed)
                capture.StopRecording();
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            capture.Dispose();
        }

        private void OnDataAvailable(
            ReadOnlySpan<byte> buffer,
            AudioClientBufferFlags flags,
            long devicePosition,
            long qpcPosition)
        {
            byte[] copy = buffer.ToArray();
            DataAvailable?.Invoke(
                this,
                new LocalAudioDataAvailableEventArgs(copy, copy.Length));
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs args)
        {
            Stopped?.Invoke(
                this,
                new LocalAudioCaptureStoppedEventArgs(args.Exception));
        }
    }
}
