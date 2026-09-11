using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace LiveCaptionsTranslator.services
{
    public sealed class ReplayCaptionSource : ICaptionSource
    {
        private readonly IReadOnlyList<CaptionFrame> frames;
        private readonly bool honorTiming;

        public ReplayCaptionSource(IEnumerable<CaptionFrame> frames, bool honorTiming = true)
        {
            this.frames = frames.OrderBy(frame => frame.Offset).ToArray();
            this.honorTiming = honorTiming;
        }

        public async IAsyncEnumerable<CaptionFrame> ReadAsync(
            [EnumeratorCancellation] CancellationToken token = default)
        {
            var stopwatch = Stopwatch.StartNew();
            foreach (var frame in frames)
            {
                token.ThrowIfCancellationRequested();
                if (honorTiming)
                {
                    var delay = frame.Offset - stopwatch.Elapsed;
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay, token);
                }

                yield return frame;
            }
        }
    }
}
