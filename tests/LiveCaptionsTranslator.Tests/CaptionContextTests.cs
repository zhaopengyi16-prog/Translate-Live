using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class CaptionContextTests
    {
        [TestMethod]
        public void CompletedCorrectionReplacesItsContextWithoutReorderingHistory()
        {
            Caption caption = Caption.GetInstance();
            caption.ClearContextHistory();

            try
            {
                caption.UpsertContext(Entry(1, "First sentence.", "第一句。"));
                caption.UpsertContext(Entry(2, "The model is fast.", "模型很快。"));
                caption.UpsertContext(
                    Entry(3, "The model is faster.", "模型更快。"),
                    replacedEntryId: 2);

                TranslationHistoryEntry[] contexts = caption.Contexts.ToArray();
                Assert.HasCount(2, contexts);
                CollectionAssert.AreEqual(
                    new long[] { 1, 3 },
                    contexts.Select(entry => entry.Id).ToArray());
                Assert.AreEqual("The model is faster.", contexts[1].SourceText);
            }
            finally
            {
                caption.ClearContextHistory();
            }
        }

        [TestMethod]
        public void OverlayDoesNotRepeatTheCurrentAcceptedTranslation()
        {
            Caption caption = Caption.GetInstance();
            caption.ClearContextHistory();
            var identity = new TranslationTaskIdentity(
                Guid.NewGuid(),
                1,
                0,
                true,
                DateTimeOffset.UtcNow);
            caption.BeginCurrentSegment(identity);
            caption.TryApplyCurrentTranslation(identity, "当前译文。");

            try
            {
                caption.UpsertContext(
                    Entry(1, "Current sentence.", "当前译文。"),
                    segmentId: identity.SegmentId);

                string visible = caption.OverlayPreviousTranslation +
                                 caption.OverlayCurrentTranslation;
                Assert.AreEqual(1, CountOccurrences(visible, "当前译文。"));
            }
            finally
            {
                caption.ClearCurrentSegment();
                caption.ClearContextHistory();
            }
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }
            return count;
        }

        private static TranslationHistoryEntry Entry(
            long id,
            string source,
            string translation)
        {
            return new TranslationHistoryEntry
            {
                Id = id,
                Timestamp = "00:00:00",
                TimestampFull = "2026-01-01T00:00:00Z",
                SourceText = source,
                TranslatedText = translation,
                TargetLanguage = "zh-CN",
                ApiUsed = "Test"
            };
        }
    }
}
