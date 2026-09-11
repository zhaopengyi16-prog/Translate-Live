using LiveCaptionsTranslator.services;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class ReplayCaptionSourceTests
    {
        [TestMethod]
        public async Task FramesAreReturnedInTimelineOrder()
        {
            var source = new ReplayCaptionSource(
            [
                new CaptionFrame(TimeSpan.FromMilliseconds(20), "final", true),
                new CaptionFrame(TimeSpan.Zero, "draft", false),
                new CaptionFrame(TimeSpan.FromMilliseconds(10), "revised", false)
            ], honorTiming: false);

            var received = new List<CaptionFrame>();
            await foreach (var frame in source.ReadAsync())
                received.Add(frame);

            CollectionAssert.AreEqual(
                new[] { "draft", "revised", "final" },
                received.Select(frame => frame.Text).ToArray());
            Assert.IsTrue(received[^1].IsFinal);
        }

        [TestMethod]
        public async Task CancellationStopsReplay()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var source = new ReplayCaptionSource(
                [new CaptionFrame(TimeSpan.Zero, "ignored", false)],
                honorTiming: false);

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in source.ReadAsync(cts.Token))
                {
                }
            });
        }
    }
}
