using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.services
{
    public enum CredentialConnectionState
    {
        Success,
        MissingConfiguration,
        Rejected,
        TimedOut,
        Failed,
        Unsupported
    }

    public readonly record struct CredentialConnectionResult(
        CredentialConnectionState State,
        string Message);

    public sealed class CredentialConnectionTester
    {
        private static readonly HttpClient client = new()
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        public async Task<CredentialConnectionResult> TestAsync(
            string provider,
            Setting setting,
            CancellationToken token)
        {
            if (!HasRequiredConfiguration(provider, setting))
            {
                return new CredentialConnectionResult(
                    CredentialConnectionState.MissingConfiguration,
                    "请先填写该服务所需的地址、模型和凭据。");
            }

            try
            {
                bool succeeded;
                if (provider == "Summary")
                {
                    succeeded = await ProbeOpenAiCompatibleAsync(
                        setting.Summary.ApiUrl,
                        setting.Summary.ApiKey,
                        setting.Summary.ModelName,
                        token);
                }
                else if (provider == "OpenAI")
                {
                    var config = (OpenAIConfig)setting[provider];
                    succeeded = await ProbeOpenAiCompatibleAsync(
                        config.ApiUrl,
                        config.ApiKey,
                        config.ModelName,
                        token);
                }
                else if (provider == "OpenRouter")
                {
                    var config = (OpenRouterConfig)setting[provider];
                    succeeded = await ProbeOpenAiCompatibleAsync(
                        "https://openrouter.ai/api/v1/chat/completions",
                        config.ApiKey,
                        config.ModelName,
                        token);
                }
                else if (TranslateAPI.TRANSLATE_FUNCTIONS.ContainsKey(provider))
                {
                    string response = await TranslateAPI.GetFunction(provider)(
                        "Connection check.",
                        token);
                    succeeded = IsSuccessfulProviderResponse(response);
                }
                else
                {
                    return new CredentialConnectionResult(
                        CredentialConnectionState.Unsupported,
                        "当前项目不支持测试此服务。");
                }

                return succeeded
                    ? new CredentialConnectionResult(
                        CredentialConnectionState.Success,
                        "连接成功，凭据与当前配置可用。")
                    : new CredentialConnectionResult(
                        CredentialConnectionState.Rejected,
                        "连接未通过，请检查凭据、地址、模型或服务状态。");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return new CredentialConnectionResult(
                    CredentialConnectionState.TimedOut,
                    "连接测试已超时或取消。");
            }
            catch (Exception exception)
            {
                ProductDiagnostics.Write(
                    $"credentials.connection-test.{NormalizeProvider(provider)}.failed",
                    exception);
                return new CredentialConnectionResult(
                    CredentialConnectionState.Failed,
                    "连接测试失败，请检查网络与服务配置。");
            }
        }

        internal static bool HasRequiredConfiguration(string provider, Setting setting)
        {
            return provider switch
            {
                "Summary" =>
                    HasText(setting.Summary.ApiUrl) &&
                    HasText(setting.Summary.ApiKey) &&
                    HasText(setting.Summary.ModelName),
                "OpenAI" => setting[provider] is OpenAIConfig openAi &&
                    HasText(openAi.ApiUrl) && HasText(openAi.ApiKey) && HasText(openAi.ModelName),
                "OpenRouter" => setting[provider] is OpenRouterConfig openRouter &&
                    HasText(openRouter.ApiKey) && HasText(openRouter.ModelName),
                "DeepL" => setting[provider] is DeepLConfig deepL &&
                    HasText(deepL.ApiUrl) && HasText(deepL.ApiKey),
                "Youdao" => setting[provider] is YoudaoConfig youdao &&
                    HasText(youdao.ApiUrl) && HasText(youdao.AppKey) && HasText(youdao.AppSecret),
                "MTranServer" => setting[provider] is MTranServerConfig mTran &&
                    HasText(mTran.ApiUrl),
                "Baidu" => setting[provider] is BaiduConfig baidu &&
                    HasText(baidu.ApiUrl) && HasText(baidu.AppId) && HasText(baidu.AppSecret),
                "LibreTranslate" => setting[provider] is LibreTranslateConfig libre &&
                    HasText(libre.ApiUrl),
                _ => false
            };
        }

        internal static bool IsSuccessfulProviderResponse(string? response)
        {
            if (string.IsNullOrWhiteSpace(response))
                return false;
            string normalized = response.TrimStart();
            return !normalized.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase) &&
                   !normalized.StartsWith("[WARNING]", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<bool> ProbeOpenAiCompatibleAsync(
            string apiUrl,
            string apiKey,
            string modelName,
            CancellationToken token)
        {
            var requestData = new
            {
                model = modelName,
                messages = new[] { new { role = "user", content = "Reply with OK." } },
                temperature = 0,
                max_tokens = 8,
                stream = false
            };

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                TextUtil.NormalizeUrl(apiUrl))
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(requestData),
                    Encoding.UTF8,
                    "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using HttpResponseMessage response = await client.SendAsync(request, token);
            return response.IsSuccessStatusCode;
        }

        private static bool HasText(string? value) =>
            !string.IsNullOrWhiteSpace(value);

        private static string NormalizeProvider(string provider) =>
            new string(provider.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
