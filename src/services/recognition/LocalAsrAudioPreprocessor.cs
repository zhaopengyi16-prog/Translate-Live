namespace LiveCaptionsTranslator.services.recognition
{
    /// <summary>
    /// Applies bounded automatic gain to quiet speech without lifting digital
    /// silence or low-level device noise. The recognizer still receives the
    /// original timing and sample rate.
    /// </summary>
    internal sealed class LocalAsrAudioPreprocessor
    {
        internal const float MinimumActiveRms = 0.0008f;
        internal const float TargetRms = 0.045f;
        internal const float MaximumGain = 10f;

        private float currentGain = 1f;

        public float CurrentGain => currentGain;

        public void Reset() => currentGain = 1f;

        public void ProcessInPlace(float[] samples)
        {
            ArgumentNullException.ThrowIfNull(samples);
            if (samples.Length == 0)
                return;

            double sumSquares = 0;
            foreach (float sample in samples)
                sumSquares += sample * sample;
            float rms = (float)Math.Sqrt(sumSquares / samples.Length);
            if (!float.IsFinite(rms) || rms < MinimumActiveRms)
                return;

            float desiredGain = Math.Clamp(
                TargetRms / rms,
                0.65f,
                MaximumGain);
            float smoothing = desiredGain > currentGain ? 0.55f : 0.25f;
            currentGain += (desiredGain - currentGain) * smoothing;
            if (Math.Abs(currentGain - 1f) < 0.02f)
                return;

            for (int index = 0; index < samples.Length; index++)
            {
                samples[index] = Math.Clamp(
                    samples[index] * currentGain,
                    -1f,
                    1f);
            }
        }
    }
}
