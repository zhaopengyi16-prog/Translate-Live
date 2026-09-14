using System.Buffers.Binary;

using NAudio.Wave;

namespace LiveCaptionsTranslator.services.recognition
{
    internal static class LocalAudioSampleConverter
    {
        public static LocalAudioFormat Describe(WaveFormat waveFormat)
        {
            ArgumentNullException.ThrowIfNull(waveFormat);

            LocalAudioSampleEncoding encoding = waveFormat.Encoding switch
            {
                WaveFormatEncoding.Pcm => LocalAudioSampleEncoding.Pcm,
                WaveFormatEncoding.IeeeFloat => LocalAudioSampleEncoding.IeeeFloat,
                WaveFormatEncoding.Extensible when waveFormat is WaveFormatExtensible extensible &&
                    extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM =>
                    LocalAudioSampleEncoding.Pcm,
                WaveFormatEncoding.Extensible when waveFormat is WaveFormatExtensible extensible &&
                    extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT =>
                    LocalAudioSampleEncoding.IeeeFloat,
                _ => throw new NotSupportedException(
                    $"Unsupported local ASR audio encoding: {waveFormat.Encoding}.")
            };

            var format = new LocalAudioFormat(
                waveFormat.SampleRate,
                waveFormat.Channels,
                waveFormat.BitsPerSample,
                waveFormat.BlockAlign,
                encoding);
            Validate(format);
            return format;
        }

        public static float[] ConvertToMonoFloat(
            ReadOnlySpan<byte> source,
            LocalAudioFormat format)
        {
            ArgumentNullException.ThrowIfNull(format);
            Validate(format);

            int bytesPerSample = format.BitsPerSample / 8;
            int minimumFrameSize = bytesPerSample * format.Channels;
            if (format.BlockAlign < minimumFrameSize)
                throw new NotSupportedException("Audio block alignment is smaller than one frame.");

            int frameCount = source.Length / format.BlockAlign;
            if (frameCount == 0)
                return [];

            var samples = new float[frameCount];
            for (int frame = 0; frame < frameCount; frame++)
            {
                int frameOffset = frame * format.BlockAlign;
                double sum = 0;
                for (int channel = 0; channel < format.Channels; channel++)
                {
                    int sampleOffset = frameOffset + channel * bytesPerSample;
                    sum += ReadSample(source, sampleOffset, format);
                }

                samples[frame] = Math.Clamp(
                    (float)(sum / format.Channels),
                    -1f,
                    1f);
            }
            return samples;
        }

        private static float ReadSample(
            ReadOnlySpan<byte> source,
            int offset,
            LocalAudioFormat format)
        {
            if (format.Encoding == LocalAudioSampleEncoding.Pcm)
            {
                return format.BitsPerSample switch
                {
                    8 => (source[offset] - 128) / 128f,
                    16 => BinaryPrimitives.ReadInt16LittleEndian(source[offset..]) / 32768f,
                    24 => ReadInt24(source, offset) / 8388608f,
                    32 => BinaryPrimitives.ReadInt32LittleEndian(source[offset..]) / 2147483648f,
                    _ => throw new NotSupportedException(
                        $"Unsupported PCM sample width: {format.BitsPerSample}.")
                };
            }

            double value = format.BitsPerSample switch
            {
                32 => BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(source[offset..])),
                64 => BitConverter.Int64BitsToDouble(
                    BinaryPrimitives.ReadInt64LittleEndian(source[offset..])),
                _ => throw new NotSupportedException(
                    $"Unsupported IEEE float sample width: {format.BitsPerSample}.")
            };
            return double.IsFinite(value) ? (float)Math.Clamp(value, -1d, 1d) : 0f;
        }

        private static int ReadInt24(ReadOnlySpan<byte> source, int offset)
        {
            int value = source[offset] |
                source[offset + 1] << 8 |
                source[offset + 2] << 16;
            if ((value & 0x00800000) != 0)
                value |= unchecked((int)0xFF000000);
            return value;
        }

        private static void Validate(LocalAudioFormat format)
        {
            if (format.SampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(format.SampleRate));
            if (format.Channels <= 0)
                throw new ArgumentOutOfRangeException(nameof(format.Channels));
            if (format.BitsPerSample <= 0 || format.BitsPerSample % 8 != 0)
                throw new NotSupportedException("Audio samples must use whole bytes.");
            if (format.BlockAlign <= 0)
                throw new ArgumentOutOfRangeException(nameof(format.BlockAlign));

            bool supported = format.Encoding switch
            {
                LocalAudioSampleEncoding.Pcm =>
                    format.BitsPerSample is 8 or 16 or 24 or 32,
                LocalAudioSampleEncoding.IeeeFloat =>
                    format.BitsPerSample is 32 or 64,
                _ => false
            };
            if (!supported)
            {
                throw new NotSupportedException(
                    $"Unsupported local ASR sample format: " +
                    $"{format.Encoding}/{format.BitsPerSample}.");
            }
        }
    }
}
