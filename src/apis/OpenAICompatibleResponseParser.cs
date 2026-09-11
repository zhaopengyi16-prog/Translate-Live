using System.Text;
using System.Text.Json;

namespace LiveCaptionsTranslator.apis
{
    internal readonly record struct OpenAICompatibleResponse(
        bool IsValidJson,
        string Text,
        string FinishReason);

    internal readonly record struct OpenAICompatibleStreamDelta(
        bool IsValidJson,
        string Text,
        string FinishReason);

    internal static class OpenAICompatibleResponseParser
    {
        public static OpenAICompatibleResponse Parse(string responseBody)
        {
            try
            {
                using var document = JsonDocument.Parse(responseBody);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return new OpenAICompatibleResponse(true, string.Empty, string.Empty);

                string finishReason = string.Empty;
                string text = string.Empty;

                if (TryGetFirstArrayItem(root, "choices", out JsonElement choice))
                {
                    finishReason = GetString(choice, "finish_reason");
                    if (choice.TryGetProperty("message", out JsonElement message) &&
                        message.TryGetProperty("content", out JsonElement content))
                    {
                        text = ReadContent(content);
                    }

                    if (string.IsNullOrWhiteSpace(text))
                        text = GetString(choice, "text");
                }

                if (string.IsNullOrWhiteSpace(text))
                    text = GetString(root, "output_text");

                if (string.IsNullOrWhiteSpace(text) &&
                    root.TryGetProperty("output", out JsonElement output) &&
                    output.ValueKind == JsonValueKind.Array)
                {
                    var builder = new StringBuilder();
                    foreach (JsonElement item in output.EnumerateArray())
                    {
                        if (!item.TryGetProperty("content", out JsonElement content))
                            continue;
                        AppendContent(builder, content);
                    }
                    text = builder.ToString();
                }

                return new OpenAICompatibleResponse(
                    true,
                    text.Trim(),
                    finishReason);
            }
            catch (JsonException)
            {
                return new OpenAICompatibleResponse(false, string.Empty, string.Empty);
            }
        }

        public static OpenAICompatibleStreamDelta ParseStreamData(string responseData)
        {
            try
            {
                using var document = JsonDocument.Parse(responseData);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !TryGetFirstArrayItem(root, "choices", out JsonElement choice))
                {
                    return new OpenAICompatibleStreamDelta(true, string.Empty, string.Empty);
                }

                string finishReason = GetString(choice, "finish_reason");
                string text = string.Empty;
                if (choice.TryGetProperty("delta", out JsonElement delta) &&
                    delta.ValueKind == JsonValueKind.Object &&
                    delta.TryGetProperty("content", out JsonElement content))
                {
                    text = ReadContent(content);
                }

                return new OpenAICompatibleStreamDelta(true, text, finishReason);
            }
            catch (JsonException)
            {
                return new OpenAICompatibleStreamDelta(false, string.Empty, string.Empty);
            }
        }

        private static string ReadContent(JsonElement content)
        {
            if (content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? string.Empty;

            var builder = new StringBuilder();
            AppendContent(builder, content);
            return builder.ToString();
        }

        private static void AppendContent(StringBuilder builder, JsonElement content)
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                builder.Append(content.GetString());
                return;
            }
            if (content.ValueKind == JsonValueKind.Object)
            {
                string value = GetString(content, "text");
                if (string.IsNullOrWhiteSpace(value))
                    value = GetString(content, "content");
                builder.Append(value);
                return;
            }
            if (content.ValueKind != JsonValueKind.Array)
                return;

            foreach (JsonElement part in content.EnumerateArray())
            {
                var partBuilder = new StringBuilder();
                AppendContent(partBuilder, part);
                string value = partBuilder.ToString();

                if (string.IsNullOrWhiteSpace(value))
                    continue;
                if (builder.Length > 0)
                    builder.AppendLine();
                builder.Append(value);
            }
        }

        private static string GetString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out JsonElement property) &&
                   property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
        }

        private static bool TryGetFirstArrayItem(
            JsonElement element,
            string propertyName,
            out JsonElement item)
        {
            item = default;
            if (!element.TryGetProperty(propertyName, out JsonElement array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var enumerator = array.EnumerateArray();
            if (!enumerator.MoveNext())
                return false;
            item = enumerator.Current;
            return true;
        }
    }
}
