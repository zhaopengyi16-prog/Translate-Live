using LiveCaptionsTranslator.apis;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class OpenAICompatibleResponseParserTests
    {
        [TestMethod]
        public void ParsesStandardChatCompletion()
        {
            const string json =
                """
                {"choices":[{"message":{"content":"标准译文"},"finish_reason":"stop"}]}
                """;

            OpenAICompatibleResponse result =
                OpenAICompatibleResponseParser.Parse(json);

            Assert.IsTrue(result.IsValidJson);
            Assert.AreEqual("标准译文", result.Text);
            Assert.AreEqual("stop", result.FinishReason);
        }

        [TestMethod]
        public void ParsesArrayBasedMessageContentFromCompatibleGateways()
        {
            const string json =
                """
                {"choices":[{"message":{"content":[{"type":"text","text":"第一段"},{"type":"text","text":"第二段"}]},"finish_reason":"stop"}]}
                """;

            OpenAICompatibleResponse result =
                OpenAICompatibleResponseParser.Parse(json);

            Assert.IsTrue(result.IsValidJson);
            Assert.AreEqual($"第一段{Environment.NewLine}第二段", result.Text);
        }

        [TestMethod]
        public void FallsBackToLegacyChoiceText()
        {
            const string json =
                """
                {"choices":[{"text":"兼容译文","finish_reason":"stop"}]}
                """;

            OpenAICompatibleResponse result =
                OpenAICompatibleResponseParser.Parse(json);

            Assert.AreEqual("兼容译文", result.Text);
        }

        [TestMethod]
        public void ParsesResponsesStyleOutputContent()
        {
            const string json =
                """
                {"output":[{"content":[{"type":"output_text","text":"响应式译文"}]}]}
                """;

            OpenAICompatibleResponse result =
                OpenAICompatibleResponseParser.Parse(json);

            Assert.AreEqual("响应式译文", result.Text);
        }

        [TestMethod]
        public void InvalidJsonIsReportedWithoutThrowing()
        {
            OpenAICompatibleResponse result =
                OpenAICompatibleResponseParser.Parse("{not-json");

            Assert.IsFalse(result.IsValidJson);
            Assert.AreEqual(string.Empty, result.Text);
        }

        [TestMethod]
        public void ParsesStreamingContentDelta()
        {
            const string json =
                """
                {"choices":[{"delta":{"content":"实时"},"finish_reason":null}]}
                """;

            OpenAICompatibleStreamDelta result =
                OpenAICompatibleResponseParser.ParseStreamData(json);

            Assert.IsTrue(result.IsValidJson);
            Assert.AreEqual("实时", result.Text);
            Assert.AreEqual(string.Empty, result.FinishReason);
        }

        [TestMethod]
        public void ParsesStreamingFinishReasonWithoutContent()
        {
            const string json =
                """
                {"choices":[{"delta":{"content":""},"finish_reason":"stop"}]}
                """;

            OpenAICompatibleStreamDelta result =
                OpenAICompatibleResponseParser.ParseStreamData(json);

            Assert.IsTrue(result.IsValidJson);
            Assert.AreEqual(string.Empty, result.Text);
            Assert.AreEqual("stop", result.FinishReason);
        }
    }
}
