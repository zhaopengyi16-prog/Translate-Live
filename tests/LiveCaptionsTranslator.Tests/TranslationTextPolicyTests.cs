using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class TranslationTextPolicyTests
    {
        [TestMethod]
        public void ProviderErrorIsPreservedInsteadOfBeingExtractedAsBlank()
        {
            const string error = "[ERROR] Translation Failed: timeout";

            Assert.IsTrue(TranslationTextPolicy.IsProviderNotice(error));
            Assert.IsFalse(TranslationTextPolicy.TryExtractTargetSentence(error, out var translatedText));
            Assert.AreEqual(string.Empty, translatedText);
            Assert.AreEqual(error, TranslationTextPolicy.EnsureUsable(error));
        }

        [TestMethod]
        public void MarkedTargetSentenceIsExtracted()
        {
            Assert.IsTrue(TranslationTextPolicy.TryExtractTargetSentence(
                "上下文。 🔤 当前翻译。 🔤", out var translatedText));

            Assert.AreEqual("当前翻译。", translatedText);
        }

        [TestMethod]
        public void BlankProviderResponseBecomesAnError()
        {
            Assert.AreEqual(
                TranslationTextPolicy.EmptyResponseError,
                TranslationTextPolicy.EnsureUsable("   "));
        }

        [TestMethod]
        public void SentenceCompletionRecognizesWesternAndChinesePunctuation()
        {
            Assert.IsTrue(TranslationTextPolicy.IsCompleteSentence("Finished."));
            Assert.IsTrue(TranslationTextPolicy.IsCompleteSentence("完成。"));
            Assert.IsFalse(TranslationTextPolicy.IsCompleteSentence("Still speaking"));
        }

        [TestMethod]
        public void DraftRequestsDoNotRetryWhileFinalSentencesKeepReliabilityRetry()
        {
            Assert.AreEqual(
                1,
                TranslationTextPolicy.GetRealtimeAttemptLimit("Still speaking", 2));
            Assert.AreEqual(
                2,
                TranslationTextPolicy.GetRealtimeAttemptLimit("Finished.", 2));
        }
    }
}
