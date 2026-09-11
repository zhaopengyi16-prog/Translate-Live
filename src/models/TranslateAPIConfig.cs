using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace LiveCaptionsTranslator.models
{
    public class TranslateAPIConfig : INotifyPropertyChanged
    {
        [JsonIgnore]
        internal Setting? Owner { get; set; }

        /*
         * The key of this property is used as the content for `targetLangBox` in the `SettingPage`.
         * Its purpose is to standardize the language selection interface.
         * Therefore, if your API doesn't follow the key format, please override (use `new`) this property.
         * (See the definition of `DeepLConfig` for an example)
         */
        [JsonIgnore]
        public static Dictionary<string, string> SupportedLanguages => new()
        {
            { "zh-CN", "zh-CN" },
            { "zh-TW", "zh-TW" },
            { "en-US", "en-US" },
            { "en-GB", "en-GB" },
            { "ja-JP", "ja-JP" },
            { "ko-KR", "ko-KR" },
            { "fr-FR", "fr-FR" },
            { "th-TH", "th-TH" },
            { "ru-RU", "ru-RU" },
            { "es-ES", "es-ES" },
            { "pt-BR", "pt-BR" },
            { "tr-TR", "tr-TR" },
            { "ar-SA", "ar-SA" },
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            Owner?.NotifyConfigurationChanged();
        }

        protected void OnCredentialChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            Owner?.NotifyCredentialChanged();
        }
    }

    public class BaseLLMConfig : TranslateAPIConfig
    {
        public class Message
        {
            public string role { get; set; }
            public string content { get; set; }
        }

        private string modelName = "";
        private double temperature = 1.0;

        public string ModelName
        {
            get => modelName;
            set
            {
                modelName = value;
                OnPropertyChanged("ModelName");
            }
        }
        public double Temperature
        {
            get => temperature;
            set
            {
                temperature = value;
                OnPropertyChanged("Temperature");
            }
        }
    }

    public class OllamaConfig : BaseLLMConfig
    {
        public class Response
        {
            public string model { get; set; }
            public DateTime created_at { get; set; }
            public Message message { get; set; }
            public bool done { get; set; }
            public long total_duration { get; set; }
            public int load_duration { get; set; }
            public int prompt_eval_count { get; set; }
            public long prompt_eval_duration { get; set; }
            public int eval_count { get; set; }
            public long eval_duration { get; set; }
        }

        private string apiUrl = "http://localhost:11434";

        public string ApiUrl
        {
            get => apiUrl;
            set
            {
                apiUrl = value;
                OnPropertyChanged("ApiUrl");
            }
        }
    }

    public class OpenAIConfig : BaseLLMConfig
    {
        public class Choice
        {
            public int index { get; set; }
            public Message message { get; set; }
            public string logprobs { get; set; }
            public string finish_reason { get; set; }
        }
        public class Usage
        {
            public int prompt_tokens { get; set; }
            public int completion_tokens { get; set; }
            public int total_tokens { get; set; }
            public int prompt_cache_hit_tokens { get; set; }
            public int prompt_cache_miss_tokens { get; set; }
        }
        public class Response
        {
            public string id { get; set; }
            public string @object { get; set; }
            public int created { get; set; }
            public string model { get; set; }
            public List<Choice> choices { get; set; }
            public Usage usage { get; set; }
            public string system_fingerprint { get; set; }
        }

        private string apiKey = "";
        private string apiUrl = "";

        [JsonIgnore]
        public string ApiKey
        {
            get => apiKey;
            set
            {
                apiKey = value;
                OnCredentialChanged("ApiKey");
            }
        }
        public string ApiUrl
        {
            get => apiUrl;
            set
            {
                apiUrl = value;
                OnPropertyChanged("ApiUrl");
            }
        }
    }

    public sealed class SummaryConfig : TranslateAPIConfig
    {
        private string modelName = "";
        private double temperature = 0.2;
        private string apiUrl = "";
        private string apiKey = "";
        private string prompt = "请根据课堂字幕生成中文课堂总结。输出应包含：主题概览、核心知识点、重要例子或结论、待确认问题和课后行动项。只依据提供的字幕，不要编造内容。";

        public string ModelName
        {
            get => modelName;
            set
            {
                modelName = value;
                OnPropertyChanged();
            }
        }

        public double Temperature
        {
            get => temperature;
            set
            {
                temperature = value;
                OnPropertyChanged();
            }
        }

        public string ApiUrl
        {
            get => apiUrl;
            set
            {
                apiUrl = value;
                OnPropertyChanged();
            }
        }

        [JsonIgnore]
        public string ApiKey
        {
            get => apiKey;
            set
            {
                apiKey = value;
                OnCredentialChanged();
            }
        }

        public string Prompt
        {
            get => prompt;
            set
            {
                prompt = value;
                OnPropertyChanged();
            }
        }
    }

    public class OpenRouterConfig : BaseLLMConfig
    {
        private string apiKey = "";
        [JsonIgnore]
        public string ApiKey
        {
            get => apiKey;
            set
            {
                apiKey = value;
                OnCredentialChanged();
            }
        }
    }

    public class DeepLConfig : TranslateAPIConfig
    {
        [JsonIgnore]
        public new static Dictionary<string, string> SupportedLanguages => new()
        {
            { "zh-CN", "ZH-HANS" },
            { "zh-TW", "ZH-HANT" },
            { "en-US", "EN-US" },
            { "en-GB", "EN-GB" },
            { "ja-JP", "JA" },
            { "ko-KR", "KO" },
            { "fr-FR", "FR" },
            { "th-TH", "TH" },
            { "ru-RU", "RU" },
            { "es-ES", "ES" },
            { "pt-BR", "PT-BR" },
            { "tr-TR", "TR" },
            { "ar-SA", "AR" },
        };

        private string apiKey = "";
        private string apiUrl = "https://api.deepl.com/v2/translate";

        [JsonIgnore]
        public string ApiKey
        {
            get => apiKey;
            set
            {
                apiKey = value;
                OnCredentialChanged("ApiKey");
            }
        }
        public string ApiUrl
        {
            get => apiUrl;
            set
            {
                apiUrl = value;
                OnPropertyChanged("ApiUrl");
            }
        }
    }
    public class YoudaoConfig : TranslateAPIConfig
    {
        public class TranslationResult
        {
            public string errorCode { get; set; }
            public string query { get; set; }
            public List<string> translation { get; set; }
            public string l { get; set; }
            public string tSpeakUrl { get; set; }
            public string speakUrl { get; set; }
        }

        [JsonIgnore]
        public new static Dictionary<string, string> SupportedLanguages => new()
        {
            { "zh-CN", "zh-CHS" },
            { "zh-TW", "zh-CHT" },
            { "en-US", "en" },
            { "en-GB", "en" },
            { "ja-JP", "ja" },
            { "ko-KR", "ko" },
            { "fr-FR", "fr" },
            { "th-TH", "th" },
            { "ru-RU", "ru" },
            { "es-ES", "es" },
            { "pt-BR", "pt" },
            { "tr-TR", "tr" },
            { "ar-SA", "ar" },
        };

        private string appKey = "";
        private string appSecret = "";
        private string apiUrl = "https://openapi.youdao.com/api";

        [JsonIgnore]
        public string AppKey
        {
            get => appKey;
            set
            {
                appKey = value;
                OnCredentialChanged("AppKey");
            }
        }

        [JsonIgnore]
        public string AppSecret
        {
            get => appSecret;
            set
            {
                appSecret = value;
                OnCredentialChanged("AppSecret");
            }
        }

        public string ApiUrl
        {
            get => apiUrl;
            set
            {
                apiUrl = value;
                OnPropertyChanged("ApiUrl");
            }
        }
    }

    public class MTranServerConfig : TranslateAPIConfig
    {
        [JsonIgnore]
        public new static Dictionary<string, string> SupportedLanguages => new()
        {
            { "zh-CN", "zh" },
            { "zh-TW", "zh" },
            { "en-US", "en" },
            { "en-GB", "en" },
            { "ja-JP", "ja" },
            { "ko-KR", "ko" },
            { "fr-FR", "fr" },
            { "th-TH", "th" },
            { "ru-RU", "ru" },
            { "es-ES", "es" },
            { "pt-BR", "pt" },
            { "tr-TR", "tr" },
            { "ar-SA", "ar" },
        };

        private string apiKey = "";
        private string apiUrl = "http://localhost:8989/translate";
        private string sourceLanguage = "en";

        [JsonIgnore]
        public string ApiKey
        {
            get => apiKey;
            set
            {
                apiKey = value;
                OnCredentialChanged("ApiKey");
            }
        }
        public string ApiUrl
        {
            get => apiUrl;
            set
            {
                apiUrl = value;
                OnPropertyChanged("ApiUrl");
            }
        }

        public string SourceLanguage
        {
            get => sourceLanguage;
            set
            {
                sourceLanguage = value;
                OnPropertyChanged("SourceLanguage");
            }
        }

        public class Response
        {
            public string result { get; set; }
        }
    }

    public class BaiduConfig : TranslateAPIConfig
    {
        public class TransResult
        {
            public string src { get; set; }
            public string dst { get; set; }
        }

        public class TranslationResult
        {
            public string error_code { get; set; }
            public string from { get; set; }
            public string to { get; set; }
            public List<TransResult> trans_result { get; set; }
        }

        [JsonIgnore]
        public new static Dictionary<string, string> SupportedLanguages => new()
        {
            { "zh-CN", "zh" },
            { "zh-TW", "cht" },
            { "en-US", "en" },
            { "en-GB", "en" },
            { "ja-JP", "jp" },
            { "ko-KR", "kor" },
            { "fr-FR", "fra" },
            { "th-TH", "th" },
            { "ru-RU", "ru" },
            { "es-ES", "spa" },
            { "pt-BR", "pt" },
            { "tr-TR", "tr" },
            { "ar-SA", "ara" },
        };

        private string appId = "";
        private string appSecret = "";
        private string apiUrl = "https://fanyi-api.baidu.com/api/trans/vip/translate";

        [JsonIgnore]
        public string AppId
        {
            get => appId;
            set
            {
                appId = value;
                OnCredentialChanged("AppId");
            }
        }

        [JsonIgnore]
        public string AppSecret
        {
            get => appSecret;
            set
            {
                appSecret = value;
                OnCredentialChanged("AppSecret");
            }
        }

        public string ApiUrl
        {
            get => apiUrl;
            set
            {
                apiUrl = value;
                OnPropertyChanged("ApiUrl");
            }
        }
    }

    public class LibreTranslateConfig : TranslateAPIConfig
    {
        [JsonIgnore]
        public new static Dictionary<string, string> SupportedLanguages => new()
        {
            { "zh-CN", "zh" },
            { "zh-TW", "zh" },
            { "en-US", "en" },
            { "en-GB", "en" },
            { "ja-JP", "ja" },
            { "ko-KR", "ko" },
            { "fr-FR", "fr" },
            { "th-TH", "th" },
            { "ru-RU", "ru" },
            { "es-ES", "es" },
            { "pt-BR", "pt" },
            { "tr-TR", "tr" },
            { "ar-SA", "ar" },
        };

        private string apiKey = "";
        private string apiUrl = "http://localhost:5000/translate";

        [JsonIgnore]
        public string ApiKey
        {
            get => apiKey;
            set
            {
                apiKey = value;
                OnCredentialChanged("ApiKey");
            }
        }
        public string ApiUrl
        {
            get => apiUrl;
            set
            {
                apiUrl = value;
                OnPropertyChanged("ApiUrl");
            }
        }

        public class Response
        {
            public string translatedText { get; set; }
        }
    }
}
