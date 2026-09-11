using System.Collections.Concurrent;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.services;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class TranscriptCoordinatorTests
    {
        [TestMethod]
        public async Task DraftRevisionsReplaceTheSameSegment()
        {
            var service = new DelegateTranslationService(request => Success(request, "译文"));
            var coordinator = new TranscriptCoordinator(service);
            var drafts = new List<TranscriptSegment>();
            coordinator.DraftChanged += draft =>
            {
                if (draft != null)
                    drafts.Add(draft);
            };

            await coordinator.RunAsync(new ReplayCaptionSource(
            [
                new CaptionFrame(TimeSpan.Zero, "draft", false),
                new CaptionFrame(TimeSpan.Zero, "revised", false),
                new CaptionFrame(TimeSpan.Zero, "final", true)
            ], honorTiming: false));

            Assert.HasCount(1, coordinator.Segments);
            Assert.HasCount(2, drafts);
            Assert.AreEqual(drafts[0].Id, drafts[1].Id);
            Assert.AreEqual(drafts[0].Sequence, drafts[1].Sequence);
            Assert.AreEqual(drafts[0].Revision + 1, drafts[1].Revision);

            var segment = coordinator.Segments[0];
            Assert.AreEqual(drafts[0].Id, segment.Id);
            Assert.AreEqual(2, segment.Revision);
            Assert.AreEqual("final", segment.SourceText);
            Assert.AreEqual("译文", segment.TranslatedText);
            Assert.AreEqual(SegmentState.Translated, segment.State);
        }

        [TestMethod]
        public async Task ConsecutiveFinalFramesCreateIncreasingSequences()
        {
            var service = new DelegateTranslationService(request => Success(request, request.SourceText));
            var coordinator = new TranscriptCoordinator(service);

            await coordinator.RunAsync(new ReplayCaptionSource(
            [
                new CaptionFrame(TimeSpan.Zero, "first", true),
                new CaptionFrame(TimeSpan.Zero, "second", true),
                new CaptionFrame(TimeSpan.Zero, "third", true)
            ], honorTiming: false));

            CollectionAssert.AreEqual(
                new long[] { 1, 2, 3 },
                coordinator.Segments.Select(segment => segment.Sequence).ToArray());
            CollectionAssert.AreEqual(
                new[] { "first", "second", "third" },
                coordinator.Segments.Select(segment => segment.SourceText).ToArray());
        }

        [TestMethod]
        public async Task OutOfOrderResultsAreAppliedBySegmentId()
        {
            var gates = new ConcurrentDictionary<long, TaskCompletionSource<TranslationResult>>();
            var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new DelegateTranslationService(async (request, token) =>
            {
                var gate = new TaskCompletionSource<TranslationResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                gates[request.Sequence] = gate;
                if (gates.Count == 2)
                    allStarted.TrySetResult();

                return await gate.Task.WaitAsync(token);
            });
            var coordinator = new TranscriptCoordinator(service);

            var runTask = coordinator.RunAsync(new ReplayCaptionSource(
            [
                new CaptionFrame(TimeSpan.Zero, "first", true),
                new CaptionFrame(TimeSpan.Zero, "second", true)
            ], honorTiming: false));

            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var second = coordinator.Segments.Single(segment => segment.Sequence == 2);
            gates[2].SetResult(new TranslationResult(
                second.Id, second.Sequence, second.Revision, "第二", null, TimeSpan.Zero));
            var first = coordinator.Segments.Single(segment => segment.Sequence == 1);
            gates[1].SetResult(new TranslationResult(
                first.Id, first.Sequence, first.Revision, "第一", null, TimeSpan.Zero));
            await runTask;

            CollectionAssert.AreEqual(
                new[] { "第一", "第二" },
                coordinator.Segments.Select(segment => segment.TranslatedText).ToArray());
        }

        [TestMethod]
        public async Task TranslationFailureIsPublished()
        {
            var service = new DelegateTranslationService(request =>
                new TranslationResult(
                    request.SegmentId,
                    request.Sequence,
                    request.Revision,
                    null,
                    "simulated failure",
                    TimeSpan.Zero));
            var coordinator = new TranscriptCoordinator(service);
            var states = new List<SegmentState>();
            coordinator.SegmentChanged += segment => states.Add(segment.State);

            await coordinator.RunAsync(new ReplayCaptionSource(
                [new CaptionFrame(TimeSpan.Zero, "failure", true)],
                honorTiming: false));

            CollectionAssert.AreEqual(
                new[]
                {
                    SegmentState.Committed,
                    SegmentState.Queued,
                    SegmentState.Translating,
                    SegmentState.TranslationFailed
                },
                states);
            Assert.AreEqual(SegmentState.TranslationFailed, coordinator.Segments[0].State);
            Assert.IsNull(coordinator.Segments[0].TranslatedText);
        }

        [TestMethod]
        public async Task StaleResultDoesNotOverwriteTheCurrentSegment()
        {
            var service = new DelegateTranslationService(request =>
                new TranslationResult(
                    request.SegmentId,
                    request.Sequence,
                    request.Revision + 1,
                    "stale",
                    null,
                    TimeSpan.Zero));
            var coordinator = new TranscriptCoordinator(service);

            await coordinator.RunAsync(new ReplayCaptionSource(
                [new CaptionFrame(TimeSpan.Zero, "current", true)],
                honorTiming: false));

            var segment = coordinator.Segments[0];
            Assert.AreEqual(SegmentState.Translating, segment.State);
            Assert.IsNull(segment.TranslatedText);
        }

        [TestMethod]
        public async Task CancellationStopsTheRun()
        {
            var service = new DelegateTranslationService(request => Success(request, "unused"));
            var coordinator = new TranscriptCoordinator(service);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                await coordinator.RunAsync(new ReplayCaptionSource(
                    [new CaptionFrame(TimeSpan.FromSeconds(5), "late", true)],
                    honorTiming: true), cts.Token);
            });

            Assert.IsEmpty(coordinator.Segments);
        }

        [TestMethod]
        public async Task OfflineDemoContainsSixTranslatedSegments()
        {
            var coordinator = new TranscriptCoordinator(
                new DemoTranslationService(TimeSpan.Zero));

            await coordinator.RunAsync(DemoCaptionScenario.Create(honorTiming: false));

            Assert.HasCount(6, coordinator.Segments);
            Assert.IsTrue(coordinator.Segments.All(segment =>
                segment.State == SegmentState.Translated &&
                !string.IsNullOrWhiteSpace(segment.TranslatedText)));
        }

        private static TranslationResult Success(
            TranslationRequest request,
            string translatedText)
        {
            return new TranslationResult(
                request.SegmentId,
                request.Sequence,
                request.Revision,
                translatedText,
                null,
                TimeSpan.Zero);
        }

        private sealed class DelegateTranslationService : ITranslationService
        {
            private readonly Func<TranslationRequest, CancellationToken, Task<TranslationResult>> handler;

            public DelegateTranslationService(Func<TranslationRequest, TranslationResult> handler)
            {
                this.handler = (request, _) => Task.FromResult(handler(request));
            }

            public DelegateTranslationService(
                Func<TranslationRequest, CancellationToken, Task<TranslationResult>> handler)
            {
                this.handler = handler;
            }

            public Task<TranslationResult> TranslateAsync(
                TranslationRequest request,
                CancellationToken token = default)
            {
                return handler(request, token);
            }
        }
    }
}
