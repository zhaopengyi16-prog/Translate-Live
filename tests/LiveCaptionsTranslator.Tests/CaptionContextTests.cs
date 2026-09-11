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
