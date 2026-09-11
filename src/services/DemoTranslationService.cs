using System.Diagnostics;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.services
{
    public sealed class DemoTranslationService : ITranslationService
    {
        private static readonly IReadOnlyDictionary<string, string> translations =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Welcome to Translate Live."] = "欢迎使用 Translate Live。",
                ["Today we will learn how state machines work."] = "今天我们将学习状态机的工作原理。",
                ["A draft caption can change while the speaker is talking."] = "说话过程中，字幕草稿可能会发生变化。",
                ["Once a sentence is final, it is queued for translation."] = "句子定稿后会进入翻译队列。",
                ["Each translation is matched to its original sentence."] = "每条译文都会与对应原句匹配。",
                ["The timeline stays ordered even when results arrive out of order."] =
                    "即使翻译结果乱序返回，时间轴仍保持有序。"
            };

        private readonly TimeSpan delay;

        public DemoTranslationService(TimeSpan? delay = null)
        {
            this.delay = delay ?? TimeSpan.FromMilliseconds(250);
        }

        public async Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken token = default)
        {
            var stopwatch = Stopwatch.StartNew();
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, token);

            stopwatch.Stop();
            if (translations.TryGetValue(request.SourceText, out var translatedText))
            {
                return new TranslationResult(
                    request.SegmentId,
                    request.Sequence,
                    request.Revision,
                    translatedText,
                    null,
                    stopwatch.Elapsed);
            }

            return new TranslationResult(
                request.SegmentId,
                request.Sequence,
                request.Revision,
                null,
                "The sentence is not part of the offline demo.",
                stopwatch.Elapsed);
        }
    }
}
