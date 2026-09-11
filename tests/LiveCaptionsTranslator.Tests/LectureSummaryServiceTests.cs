using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.services;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class LectureSummaryServiceTests
    {
        [TestMethod]
        public void BuildTranscriptOrdersSegmentsAndIncludesAvailableTranslations()
        {
            var later = new TranscriptSegment(
                Guid.NewGuid(), 2, 0, "Second point.", "第二点。",
                SegmentState.Translated, new DateTimeOffset(2026, 9, 10, 10, 0, 2, TimeSpan.Zero));
            var earlier = new TranscriptSegment(
                Guid.NewGuid(), 1, 0, "First point.", null,
                SegmentState.Committed, new DateTimeOffset(2026, 9, 10, 10, 0, 1, TimeSpan.Zero));

            string transcript = LectureSummaryService.BuildTranscript([later, earlier]);

            Assert.IsTrue(transcript.IndexOf("First point.", StringComparison.Ordinal) <
                          transcript.IndexOf("Second point.", StringComparison.Ordinal));
            StringAssert.Contains(transcript, "译文：第二点。");
        }
    }
}
