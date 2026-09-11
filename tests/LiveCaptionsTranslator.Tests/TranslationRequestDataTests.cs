using System.Text.Json;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class TranslationRequestDataTests
    {
        [TestMethod]
        public void GenericOpenAiCompatibleRequestLeavesRoomForTranslation()
        {
            var request = new BaseLLMRequestData(
                "reasoning-model",
                [new BaseLLMConfig.Message { role = "user", content = "Hello" }],
                0.2);

            Assert.IsGreaterThanOrEqualTo(512, request.max_tokens);
            Assert.IsFalse(request.stream);
        }

        [TestMethod]
        public void DeepSeekOpenAiCompatibleRequestDisablesThinking()
        {
            var request = LLMRequestDataFactory.CreateForOpenAICompatible(
                "https://api.deepseek.com/chat/completions",
                "deepseek-v4-flash",
                [new BaseLLMConfig.Message { role = "user", content = "Hello" }],
                0.2);

            Assert.AreEqual(typeof(DeepSeekLLMRequestData), request.GetType());
            var deepSeekRequest = (DeepSeekLLMRequestData)request;
            Assert.AreEqual("disabled", deepSeekRequest.thinking.type);
            Assert.IsTrue(deepSeekRequest.stream);
            Assert.AreEqual(192, deepSeekRequest.max_tokens);
            CollectionAssert.AreEqual(new[] { "\n" }, deepSeekRequest.stop);
            StringAssert.Contains(
                JsonSerializer.Serialize(request, request.GetType()),
                "\"thinking\":{\"type\":\"disabled\"}");
        }

        [TestMethod]
        public void GenericOpenAiCompatibleRequestDoesNotReceiveProviderSpecificFields()
        {
            var request = LLMRequestDataFactory.CreateForOpenAICompatible(
                "https://api.openai.com/v1/chat/completions",
                "gpt-compatible-model",
                [new BaseLLMConfig.Message { role = "user", content = "Hello" }],
                0.2);

            Assert.AreEqual(typeof(BaseLLMRequestData), request.GetType());
            Assert.IsFalse(JsonSerializer.Serialize(request, request.GetType())
                .Contains("\"thinking\"", StringComparison.Ordinal));
        }

        [TestMethod]
        public void GoogleProvidersUsePlainTargetTextForRealtimeTranslation()
        {
            Assert.IsTrue(TranslateAPI.RequiresPlainTextInput("Google"));
            Assert.IsTrue(TranslateAPI.RequiresPlainTextInput("Google2"));
            Assert.IsFalse(TranslateAPI.RequiresPlainTextInput("DeepL"));
        }

        [TestMethod]
        public void TranslationContextRemainsChronologicalAndCurrentTextIsLast()
        {
            var contexts = new[]
            {
                Entry(1, "First source.", "第一条。"),
                Entry(2, "Second source.", "第二条。"),
                Entry(3, "Failed source.", "[ERROR] unavailable")
            };

            var messages = TranslationMessageFactory.Create(
                "Translate to {0}.",
                "Chinese",
                "Current source.",
                contexts);

            CollectionAssert.AreEqual(
                new[] { "system", "user", "assistant", "user", "assistant", "user" },
                messages.Select(message => message.role).ToArray());
            CollectionAssert.AreEqual(
                new[]
                {
                    "Translate to Chinese.",
                    "🔤 First source. 🔤",
                    "第一条。",
                    "🔤 Second source. 🔤",
                    "第二条。",
                    "🔤 Current source. 🔤"
                },
                messages.Select(message => message.content).ToArray());
        }

        [TestMethod]
        public void LectureSessionEntryPresentsOneReadableClassRecord()
        {
            var start = new DateTimeOffset(2026, 9, 10, 8, 30, 0, TimeSpan.Zero);
            var session = new LectureSessionEntry
            {
                Id = 7,
                StartedAt = start,
                EndedAt = start.AddMinutes(45),
                Mode = "在线课程 · 电脑声音",
                ApiUsed = "OpenAI / deepseek-v4-flash",
                TargetLanguage = "zh-CN",
                EntryCount = 12
            };

            StringAssert.Contains(session.Title, "在线课程");
            StringAssert.Contains(session.Detail, "12 条");
            Assert.AreNotEqual("进行中", session.Duration);
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
