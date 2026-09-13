using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.services
{
    public sealed class LectureSummaryService
    {
        public const int MaximumTranscriptSegments = 240;

        private static readonly HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        public async Task<string> GenerateAsync(
            SummaryConfig config,
            IReadOnlyCollection<TranscriptSegment> segments,
            CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(config.ApiUrl) ||
                string.IsNullOrWhiteSpace(config.ApiKey) ||
                string.IsNullOrWhiteSpace(config.ModelName))
            {
                throw new InvalidOperationException("请先在 API Setting → Summary 配置总结模型、地址和 API Key。");
            }

            string transcript = BuildTranscript(segments);
            if (string.IsNullOrWhiteSpace(transcript))
                throw new InvalidOperationException("当前课堂还没有可总结的字幕。");

            var requestData = new
            {
                model = config.ModelName,
                messages = new[]
                {
                    new { role = "system", content = config.Prompt },
                    new { role = "user", content = $"以下是本次课堂字幕：\n\n{transcript}" }
                },
                temperature = config.Temperature,
                max_tokens = 1600,
                stream = false
            };

            using var request = new HttpRequestMessage(
                HttpMethod.Post, TextUtil.NormalizeUrl(config.ApiUrl))
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(requestData), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

            using HttpResponseMessage response = await client.SendAsync(request, token);
            string responseText = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"总结接口返回 HTTP {(int)response.StatusCode}。");

            var responseObj = JsonSerializer.Deserialize<OpenAIConfig.Response>(responseText);
            string? output = responseObj?.choices?.FirstOrDefault()?.message?.content;
            if (string.IsNullOrWhiteSpace(output))
                throw new InvalidOperationException("总结接口没有返回文本。");

            return RegexPatterns.ModelThinking().Replace(output, string.Empty).Trim();
        }

        public static string BuildTranscript(IEnumerable<TranscriptSegment> segments)
        {
            var lines = segments
                .Where(segment => !string.IsNullOrWhiteSpace(segment.SourceText))
                .OrderBy(segment => segment.CapturedAt)
                .TakeLast(MaximumTranscriptSegments)
                .Select(segment => string.IsNullOrWhiteSpace(segment.TranslatedText)
                    ? $"[{segment.CapturedAt.LocalDateTime:HH:mm:ss}] {segment.SourceText.Trim()}"
                    : $"[{segment.CapturedAt.LocalDateTime:HH:mm:ss}] 原文：{segment.SourceText.Trim()}\n译文：{segment.TranslatedText.Trim()}");

            return string.Join("\n", lines);
        }
    }
}
