using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.apis
{
    public static class TranslateAPI
    {
        /*
         * The key of this field is used as the content for `translateAPIBox` in the `SettingPage`.
         * If you'd like to add a new API, please insert the key-value pair here.
         */
        public static readonly Dictionary<string, Func<string, CancellationToken, Task<string>>>
            TRANSLATE_FUNCTIONS = new()
        {
            { "Google", Google },
            { "Google2", Google2 },
            { "Ollama", Ollama },
            { "OpenAI", OpenAI },
            { "DeepL", DeepL },
            { "OpenRouter", OpenRouter },
            { "Youdao", Youdao },
            { "MTranServer", MTranServer },
            { "Baidu", Baidu },
            { "LibreTranslate", LibreTranslate },
        };
        public static readonly List<string> LLM_BASED_APIS = new()
        {
            "Ollama", "OpenAI", "OpenRouter"
        };
        public static readonly List<string> NO_CONFIG_APIS = new()
        {
            "Google", "Google2"
        };

        public static Func<string, CancellationToken, Task<string>> TranslateFunction =>
            TRANSLATE_FUNCTIONS[Translator.Setting.ApiName];
        public static bool IsLLMBased => LLM_BASED_APIS.Contains(Translator.Setting.ApiName);
        public static Func<string, CancellationToken, Task<string>> GetFunction(string apiName) =>
            TRANSLATE_FUNCTIONS.TryGetValue(apiName, out var function)
                ? function
                : throw new InvalidOperationException($"Unknown translation provider: {apiName}");
        public static bool IsLLMBasedProvider(string apiName) => LLM_BASED_APIS.Contains(apiName);
        public static bool RequiresPlainTextInput(string apiName) =>
            apiName is "Google" or "Google2";
        public static string Prompt => Translator.Setting.Prompt;

        private const int OPENAI_MAX_ATTEMPTS = 2;
        private const int OPENAI_RETRY_MAX_TOKENS = 1024;
        private static readonly TimeSpan RETRY_DELAY = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan REALTIME_LLM_TIMEOUT = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan GOOGLE_TIMEOUT = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan GOOGLE2_TIMEOUT = TimeSpan.FromSeconds(2);
        private static readonly AsyncLocal<string?> targetLanguageOverride = new();
        private static readonly HttpClient client = new HttpClient()
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        private static string CurrentTargetLanguage =>
            targetLanguageOverride.Value ?? Translator.Setting?.TargetLanguage ?? "zh-CN";

        internal static async Task<string> Execute(
            string apiName,
            Func<string, CancellationToken, Task<string>> translateFunction,
            string text,
            string targetLanguage,
            Action<string>? partialOutput,
            CancellationToken token)
        {
            string? previousLanguage = targetLanguageOverride.Value;
            targetLanguageOverride.Value = targetLanguage;
            try
            {
                return apiName == "OpenAI"
                    ? await OpenAI(text, partialOutput, token)
                    : await translateFunction(text, token);
            }
            finally
            {
                targetLanguageOverride.Value = previousLanguage;
            }
        }

        public static Task<string> OpenAI(string text, CancellationToken token = default)
        {
            return OpenAI(text, null, token);
        }

        internal static async Task<string> OpenAI(
            string text,
            Action<string>? partialOutput,
            CancellationToken token = default)
        {
            var config = Translator.Setting["OpenAI"] as OpenAIConfig;
            if (config == null || string.IsNullOrWhiteSpace(config.ApiUrl) ||
                string.IsNullOrWhiteSpace(config.ApiKey) || string.IsNullOrWhiteSpace(config.ModelName))
            {
                return "[ERROR] Translation Failed: OpenAI-compatible API configuration is incomplete.";
            }

            string language = OpenAIConfig.SupportedLanguages.TryGetValue(
                CurrentTargetLanguage, out var langValue) ? langValue : CurrentTargetLanguage;

            var messages = TranslationMessageFactory.Create(
                Prompt,
                language,
                text,
                Translator.Setting.ContextAware
                    ? Translator.Caption?.AwareContexts
                    : null);

            int maxAttempts = TranslationTextPolicy.GetRealtimeAttemptLimit(
                text,
                OPENAI_MAX_ATTEMPTS);
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var requestData = LLMRequestDataFactory.CreateForOpenAICompatible(
                    config.ApiUrl, config.ModelName, messages, config.Temperature);
                if (attempt > 1)
                    requestData.max_tokens = OPENAI_RETRY_MAX_TOKENS;

                string jsonContent = JsonSerializer.Serialize(requestData, requestData.GetType());
                using var request = new HttpRequestMessage(
                    HttpMethod.Post, TextUtil.NormalizeUrl(config.ApiUrl))
                {
                    Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                requestTimeout.CancelAfter(REALTIME_LLM_TIMEOUT);

                HttpResponseMessage response;
                try
                {
                    response = await client.SendAsync(
                        request,
                        requestData.stream
                            ? HttpCompletionOption.ResponseHeadersRead
                            : HttpCompletionOption.ResponseContentRead,
                        requestTimeout.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    ProductDiagnostics.Write("translation.openai.timeout");
                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(RETRY_DELAY, token);
                        continue;
                    }

                    return "[ERROR] Translation Failed: The request timed out after 10 seconds. " +
                           "Please check the network or use a faster model.";
                }
                catch (HttpRequestException ex) when (attempt < maxAttempts)
                {
                    ProductDiagnostics.Write("translation.openai.network.retry", ex);
                    await Task.Delay(RETRY_DELAY, token);
                    continue;
                }
                catch (Exception ex)
                {
                    return $"[ERROR] Translation Failed: {ex.Message}";
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        ProductDiagnostics.Write($"translation.openai.http.{response.StatusCode}");
                        if (attempt < maxAttempts && IsTransientStatusCode(response.StatusCode))
                        {
                            await Task.Delay(RETRY_DELAY, token);
                            continue;
                        }

                        return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
                    }

                    OpenAICompatibleResponse responseObj;
                    try
                    {
                        if (requestData.stream)
                        {
                            responseObj = await ReadStreamingResponseAsync(
                                response,
                                partialOutput,
                                requestTimeout.Token);
                        }
                        else
                        {
                            string responseString = await response.Content
                                .ReadAsStringAsync(requestTimeout.Token);
                            responseObj = OpenAICompatibleResponseParser.Parse(responseString);
                        }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        ProductDiagnostics.Write("translation.openai.timeout");
                        if (attempt < maxAttempts)
                        {
                            await Task.Delay(RETRY_DELAY, token);
                            continue;
                        }

                        return "[ERROR] Translation Failed: The request timed out after 10 seconds. " +
                               "Please check the network or use a faster model.";
                    }
                    catch (IOException ex) when (attempt < maxAttempts)
                    {
                        ProductDiagnostics.Write("translation.openai.stream.retry", ex);
                        await Task.Delay(RETRY_DELAY, token);
                        continue;
                    }
                    if (!responseObj.IsValidJson)
                    {
                        ProductDiagnostics.Write("translation.openai.invalid-json");
                        if (attempt < maxAttempts)
                        {
                            await Task.Delay(RETRY_DELAY, token);
                            continue;
                        }

                        return "[ERROR] Translation Failed: The API returned an invalid response.";
                    }

                    string output = RegexPatterns.ModelThinking()
                        .Replace(responseObj.Text, string.Empty)
                        .Trim();
                    bool truncated = string.Equals(
                        responseObj.FinishReason, "length", StringComparison.OrdinalIgnoreCase);

                    if (!string.IsNullOrWhiteSpace(output) && !truncated)
                        return output;

                    string failureKind = truncated ? "length" : "empty";
                    ProductDiagnostics.Write(
                        $"translation.openai.{failureKind}-response.attempt-{attempt}");
                    if (attempt < maxAttempts)
                    {
                        await Task.Delay(RETRY_DELAY, token);
                        continue;
                    }

                    return truncated
                        ? "[ERROR] Translation Failed: The API response was truncated."
                        : TranslationTextPolicy.EmptyResponseError;
                }
            }

            return TranslationTextPolicy.EmptyResponseError;
        }

        private static async Task<OpenAICompatibleResponse> ReadStreamingResponseAsync(
            HttpResponseMessage response,
            Action<string>? partialOutput,
            CancellationToken token)
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(token);
            using var reader = new StreamReader(stream);
            var output = new StringBuilder();
            string finishReason = string.Empty;
            bool sawValidData = false;
            bool sawInvalidData = false;
            long lastPublishedTimestamp = 0;

            while (await reader.ReadLineAsync(token) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    continue;

                string data = line[5..].TrimStart();
                if (data.Length == 0)
                    continue;
                if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                    break;

                OpenAICompatibleStreamDelta delta =
                    OpenAICompatibleResponseParser.ParseStreamData(data);
                if (!delta.IsValidJson)
                {
                    sawInvalidData = true;
                    continue;
                }

                sawValidData = true;
                if (!string.IsNullOrEmpty(delta.Text))
                    output.Append(delta.Text);
                if (!string.IsNullOrEmpty(delta.FinishReason))
                    finishReason = delta.FinishReason;

                if (partialOutput == null || output.Length == 0)
                    continue;

                long now = Stopwatch.GetTimestamp();
                if (lastPublishedTimestamp != 0 &&
                    Stopwatch.GetElapsedTime(lastPublishedTimestamp, now) <
                        TimeSpan.FromMilliseconds(60) &&
                    string.IsNullOrEmpty(finishReason))
                {
                    continue;
                }

                string partial = RegexPatterns.ModelThinking()
                    .Replace(output.ToString(), string.Empty)
                    .Trim();
                if (partial.Length > 0)
                {
                    partialOutput(partial);
                    lastPublishedTimestamp = now;
                }
            }

            string translatedText = RegexPatterns.ModelThinking()
                .Replace(output.ToString(), string.Empty)
                .Trim();
            return new OpenAICompatibleResponse(
                sawValidData || !sawInvalidData,
                translatedText,
                finishReason);
        }

        private static bool IsTransientStatusCode(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.RequestTimeout ||
                   statusCode == HttpStatusCode.TooManyRequests ||
                   statusCode == HttpStatusCode.InternalServerError ||
                   statusCode == HttpStatusCode.BadGateway ||
                   statusCode == HttpStatusCode.ServiceUnavailable ||
                   statusCode == HttpStatusCode.GatewayTimeout;
        }

        public static async Task<string> Ollama(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["Ollama"] as OllamaConfig;
            string language = OllamaConfig.SupportedLanguages.TryGetValue(
                CurrentTargetLanguage, out var langValue) ? langValue : CurrentTargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl + "/api/chat");

            var messages = TranslationMessageFactory.Create(
                Prompt,
                language,
                text,
                Translator.Setting.ContextAware
                    ? Translator.Caption?.AwareContexts
                    : null);

            var requestData = LLMRequestDataFactory.Create("Ollama", config.ModelName, messages, config.Temperature);

            string jsonContent = JsonSerializer.Serialize(requestData, requestData.GetType());
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = content
            };

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<OllamaConfig.Response>(responseString);
                var output = responseObj.message.content;
                return RegexPatterns.ModelThinking().Replace(output, "");
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }

        public static async Task<string> OpenRouter(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["OpenRouter"] as OpenRouterConfig;
            string language = OpenRouterConfig.SupportedLanguages.TryGetValue(
                CurrentTargetLanguage, out var langValue) ? langValue : CurrentTargetLanguage;
            string apiUrl = "https://openrouter.ai/api/v1/chat/completions";

            var messages = TranslationMessageFactory.Create(
                Prompt,
                language,
                text,
                Translator.Setting.ContextAware
                    ? Translator.Caption?.AwareContexts
                    : null);

            var requestData = LLMRequestDataFactory.Create("OpenRouter", config.ModelName, messages, config.Temperature);

            string jsonContent = JsonSerializer.Serialize(requestData, requestData.GetType());
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = content
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config?.ApiKey);

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
                var output = jsonResponse.GetProperty("choices")[0]
                                         .GetProperty("message")
                                         .GetProperty("content")
                                         .GetString() ?? string.Empty;
                return RegexPatterns.ModelThinking().Replace(output, "");
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }

        public static async Task<string> Google(string text, CancellationToken token = default)
        {
            string language = CurrentTargetLanguage;

            string encodedText = Uri.EscapeDataString(text);
            var url = $"https://clients5.google.com/translate_a/t?" +
                      $"client=dict-chrome-ex&sl=auto&" +
                      $"tl={language}&" +
                      $"q={encodedText}";

            try
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                requestTimeout.CancelAfter(GOOGLE_TIMEOUT);
                using var response = await client.GetAsync(url, requestTimeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    ProductDiagnostics.Write($"translation.google.http.{response.StatusCode}");
                    return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
                }

                string responseString = await response.Content.ReadAsStringAsync(requestTimeout.Token);
                var responseObj = JsonSerializer.Deserialize<List<List<string>>>(responseString);
                string? translatedText = responseObj?.FirstOrDefault()?.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(translatedText))
                {
                    ProductDiagnostics.Write("translation.google.empty-response");
                    return TranslationTextPolicy.EmptyResponseError;
                }

                return translatedText;
            }
            catch (OperationCanceledException)
            {
                if (!token.IsCancellationRequested)
                {
                    ProductDiagnostics.Write("translation.google.timeout");
                    return "[ERROR] Translation Failed: The request timed out after 5 seconds. " +
                           "Please check the network connection.";
                }
                throw;
            }
            catch (JsonException ex)
            {
                ProductDiagnostics.Write("translation.google.invalid-json", ex);
                return "[ERROR] Translation Failed: Google returned an invalid response.";
            }
            catch (Exception ex)
            {
                ProductDiagnostics.Write("translation.google.request-failed", ex);
                return $"[ERROR] Translation Failed: {ex.Message}";
            }
        }

        public static async Task<string> Google2(string text, CancellationToken token = default)
        {
            string? apiKey = Environment.GetEnvironmentVariable("LECTURE_COPILOT_GOOGLE_TRANSLATE_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                ProductDiagnostics.Write("translation.google2.key-not-configured");
                return await Google(text, token);
            }

            string language = CurrentTargetLanguage;
            string strategy = "2";

            string encodedText = Uri.EscapeDataString(text);
            string url = $"https://dictionaryextension-pa.googleapis.com/v1/dictionaryExtensionData?" +
                         $"language={language}&" +
                         $"key={apiKey}&" +
                         $"term={encodedText}&" +
                         $"strategy={strategy}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("x-referer", "chrome-extension://mgijmajocgfcbeboacabfgobmjgjcoja");

            HttpResponseMessage response;
            try
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                requestTimeout.CancelAfter(GOOGLE2_TIMEOUT);
                response = await client.SendAsync(request, requestTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!token.IsCancellationRequested)
                {
                    ProductDiagnostics.Write("translation.google2.timeout-fallback");
                    return await Google(text, token);
                }
                throw;
            }
            catch (Exception)
            {
                ProductDiagnostics.Write("translation.google2.request-fallback");
                return await Google(text, token);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    string responseBody = await response.Content.ReadAsStringAsync(token);

                    try
                    {
                        using var jsonDoc = JsonDocument.Parse(responseBody);
                        var root = jsonDoc.RootElement;

                        if (root.TryGetProperty("translateResponse", out JsonElement translateResponse) &&
                            translateResponse.TryGetProperty("translateText", out JsonElement translateText))
                        {
                            string? result = translateText.GetString();
                            if (!string.IsNullOrWhiteSpace(result))
                                return result;
                        }
                    }
                    catch (JsonException ex)
                    {
                        ProductDiagnostics.Write("translation.google2.invalid-json", ex);
                    }
                }

                ProductDiagnostics.Write($"translation.google2.fallback.{response.StatusCode}");
                return await Google(text, token);
            }
        }

        public static async Task<string> DeepL(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["DeepL"] as DeepLConfig;
            string language = DeepLConfig.SupportedLanguages.TryGetValue(
                CurrentTargetLanguage, out var langValue) ? langValue : CurrentTargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl);

            var requestData = new
            {
                text = new[] { text },
                target_lang = language
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = content
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {config?.ApiKey}");

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseString);

                if (doc.RootElement.TryGetProperty("translations", out var translations) &&
                    translations.ValueKind == JsonValueKind.Array && translations.GetArrayLength() > 0)
                {
                    return translations[0].GetProperty("text").GetString();
                }
                return "[ERROR] Translation Failed: No valid feedback";
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }


        public static async Task<string> Youdao(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["Youdao"] as YoudaoConfig;
            string language = YoudaoConfig.SupportedLanguages.TryGetValue(
                CurrentTargetLanguage, out var langValue) ? langValue : CurrentTargetLanguage;

            string salt = DateTime.Now.Millisecond.ToString();
            string sign = BitConverter.ToString(
                MD5.Create().ComputeHash(
                    Encoding.UTF8.GetBytes($"{config.AppKey}{text}{salt}{config.AppSecret}"))).Replace("-", "").ToLower();

            var parameters = new Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = "auto",
                ["to"] = language,
                ["appKey"] = config.AppKey,
                ["salt"] = salt,
                ["sign"] = sign
            };

            var content = new FormUrlEncodedContent(parameters);
            using var request = new HttpRequestMessage(HttpMethod.Post, config.ApiUrl)
            {
                Content = content
            };

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<YoudaoConfig.TranslationResult>(responseString);

                if (responseObj.errorCode != "0")
                    return $"[ERROR] Translation Failed: Youdao Error - {responseObj.errorCode}";

                return responseObj.translation?.FirstOrDefault() ?? "[ERROR] Translation Failed: No content";
            }
            else
            {
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
            }
        }

        public static async Task<string> MTranServer(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["MTranServer"] as MTranServerConfig;
            string targetLanguage = MTranServerConfig.SupportedLanguages.TryGetValue(
                CurrentTargetLanguage, out var langValue) ? langValue : CurrentTargetLanguage;
            string sourceLanguage = config.SourceLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl);

            var requestData = new
            {
                text = text,
                to = targetLanguage,
                from = sourceLanguage
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = content
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config?.ApiKey);

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<MTranServerConfig.Response>(responseString);
                return responseObj.result;
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }

        public static async Task<string> Baidu(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["Baidu"] as BaiduConfig;
            string language = BaiduConfig.SupportedLanguages.TryGetValue(
                CurrentTargetLanguage, out var langValue) ? langValue : CurrentTargetLanguage;

            string salt = DateTime.Now.Millisecond.ToString();
            string sign = BitConverter.ToString(
                MD5.Create().ComputeHash(
                    Encoding.UTF8.GetBytes($"{config.AppId}{text}{salt}{config.AppSecret}"))).Replace("-", "").ToLower();

            var parameters = new Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = "auto",
                ["to"] = language,
                ["appid"] = config.AppId,
                ["salt"] = salt,
                ["sign"] = sign
            };

            var content = new FormUrlEncodedContent(parameters);
            using var request = new HttpRequestMessage(HttpMethod.Post, config.ApiUrl)
            {
                Content = content
            };

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<BaiduConfig.TranslationResult>(responseString);

                if (responseObj.error_code is not null && responseObj.error_code != "0")
                    return $"[ERROR] Translation Failed: Baidu Error - {responseObj.error_code}";

                return responseObj.trans_result?.FirstOrDefault()?.dst ?? "[ERROR] Translation Failed: No content";
            }
            else
            {
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
            }
        }

        public static async Task<string> LibreTranslate(string text, CancellationToken token = default)
        {
            var config = Translator.Setting["LibreTranslate"] as LibreTranslateConfig;
            string targetLanguage = LibreTranslateConfig.SupportedLanguages.TryGetValue(
                CurrentTargetLanguage, out var langValue) ? langValue : CurrentTargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl);

            var requestData = new
            {
                q = text,
                target = targetLanguage,
                source = "auto",
                format = "text",
                api_key = config?.ApiKey
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = content
            };

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, token);
            }
            catch (OperationCanceledException ex)
            {
                if (ex.Message.StartsWith("The request"))
                    return $"[ERROR] Translation Failed: The request was canceled due to timeout (> 8 seconds), " +
                           $"please use a faster API or check network connection.";
                throw;
            }
            catch (Exception ex)
            {
                return $"[ERROR] Translation Failed: {ex.Message}";
            }

            if (response.IsSuccessStatusCode)
            {
                string responseString = await response.Content.ReadAsStringAsync();
                var responseObj = JsonSerializer.Deserialize<LibreTranslateConfig.Response>(responseString);
                return responseObj.translatedText;
            }
            else
                return $"[ERROR] Translation Failed: HTTP Error - {response.StatusCode}";
        }
    }

    public class ConfigDictConverter : JsonConverter<Dictionary<string, List<TranslateAPIConfig>>>
    {
        public override Dictionary<string, List<TranslateAPIConfig>> Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected a StartObject token.");
            var configs = new Dictionary<string, List<TranslateAPIConfig>>();

            reader.Read();
            while (reader.TokenType == JsonTokenType.PropertyName)
            {
                string key = reader.GetString();
                reader.Read();

                var configType = Type.GetType($"LiveCaptionsTranslator.models.{key}Config");
                TranslateAPIConfig config;

                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    var list = new List<TranslateAPIConfig>();
                    reader.Read();

                    while (reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (configType != null && typeof(TranslateAPIConfig).IsAssignableFrom(configType))
                            config = (TranslateAPIConfig)JsonSerializer.Deserialize(ref reader, configType, options);
                        else
                            config = (TranslateAPIConfig)JsonSerializer.Deserialize(ref reader, typeof(TranslateAPIConfig), options);

                        list.Add(config);
                        reader.Read();
                    }
                    configs[key] = list;
                }
                else
                    throw new JsonException("Expected a StartObject token or a StartArray token.");

                reader.Read();
            }

            if (reader.TokenType != JsonTokenType.EndObject)
                throw new JsonException("Expected an EndObject token.");
            return configs;
        }

        public override void Write(
            Utf8JsonWriter writer, Dictionary<string, List<TranslateAPIConfig>> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var kvp in value)
            {
                writer.WritePropertyName(kvp.Key);
                var configType = Type.GetType($"LiveCaptionsTranslator.models.{kvp.Key}Config");

                if (kvp.Value is IEnumerable<TranslateAPIConfig> configList)
                {
                    writer.WriteStartArray();
                    foreach (var config in configList)
                    {
                        if (configType != null && typeof(TranslateAPIConfig).IsAssignableFrom(configType))
                            JsonSerializer.Serialize(writer, config, configType, options);
                        else
                            JsonSerializer.Serialize(writer, config, typeof(TranslateAPIConfig), options);
                    }
                    writer.WriteEndArray();
                }
                else
                    throw new JsonException($"Unsupported config type: {kvp.Value.GetType()}");
            }
            writer.WriteEndObject();
        }
    }
}
