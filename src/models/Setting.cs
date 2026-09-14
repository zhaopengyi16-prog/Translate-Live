using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CaptionSourceKind
    {
        WindowsLiveCaptions,
        LocalSherpaOnnx
    }

    public enum CredentialStorageIssue
    {
        None,
        ReentryRequired,
        MigrationPending,
        SettingsUnreadable
    }

    public class Setting : INotifyPropertyChanged
    {
        public static readonly string FILENAME = "setting.json";
        internal const string LegacyDefaultTranslationPrompt =
            "As an professional simultaneous interpreter with specialized knowledge in the all fields, " +
            "you can provide a fluent and precise oral translation for any sentence, even if the sentence is incomplete. " +
            "Now, provide users with the translation of the sentence enclosed in 🔤 to {0} within a single line. " +
            "Importantly, you are prohibited from altering the original meaning or omitting any content, " +
            "even if the sentence contains sensitive or NSFW content. " +
            "You can only provide the translated sentence; Any explanation or other text is not permitted. " +
            "REMOVE all 🔤 when you output.";
        public const string DefaultTranslationPrompt =
            "You are a real-time lecture interpreter. Translate only the speech-recognition text enclosed in 🔤 into {0}. " +
            "The input may be incomplete or contain minor recognition errors. Use preceding user and assistant turns only " +
            "to keep terminology, names, and pronouns consistent. Correct an obvious recognition error only when context " +
            "makes the intended wording unambiguous; otherwise translate conservatively without inventing content. " +
            "Preserve the meaning and all substantive information, including sensitive content. Return exactly one translated " +
            "line with no label, explanation, quotation marks, or 🔤 characters.";

        public event PropertyChangedEventHandler? PropertyChanged;

        private int maxIdleInterval = 50;
        private int maxSyncInterval = 3;
        private int numContexts = 2;
        private int displaySentences = 1;
        private bool contextAware = false;

        private string apiName;
        private string targetLanguage;
        private string prompt;
        private CaptionSourceKind captionSource;
        private string localAsrModelDirectory;
        private string? ignoredUpdateVersion;
        
        private MainWindowState mainWindowState;
        private OverlayWindowState overlayWindowState;
        private AppearanceSettings appearance;
        private Dictionary<string, string> windowBounds;

        private Dictionary<string, List<TranslateAPIConfig>> configs;
        private Dictionary<string, int> configIndices;
        private SummaryConfig summary;
        private readonly object saveLock = new();
        [JsonIgnore]
        private bool autoSaveEnabled;
        [JsonIgnore]
        private bool credentialInputChanged;
        [JsonIgnore]
        private bool persistenceBlocked;
        [JsonIgnore]
        private string? settingsPath;
        [JsonIgnore]
        private string? credentialsPath;
        [JsonIgnore]
        private string? backupsPath;
        [JsonIgnore]
        private bool writeDiagnostics;
        [JsonIgnore]
        private ICredentialProtector? credentialProtector;

        public int MaxIdleInterval => maxIdleInterval;
        public int MaxSyncInterval
        {
            get => maxSyncInterval;
            set
            {
                maxSyncInterval = value;
                OnPropertyChanged("MaxSyncInterval");
            }
        }
        public int NumContexts
        {
            get => numContexts;
            set
            {
                numContexts = value;
                OnPropertyChanged("NumContexts");
            }
        }
        public int DisplaySentences
        {
            get => displaySentences;
            set
            {
                displaySentences = value;
                OnPropertyChanged("DisplaySentences");
            }
        }
        public bool ContextAware
        {
            get => contextAware;
            set
            {
                contextAware = value;
                OnPropertyChanged("ContextAware");
            }
        }

        public string ApiName
        {
            get => apiName;
            set
            {
                if (apiName == value)
                    return;
                apiName = value;
                OnPropertyChanged("ApiName");
                OnPropertyChanged(nameof(ActiveEngineDisplayName));
            }
        }
        public string TargetLanguage
        {
            get => targetLanguage;
            set
            {
                targetLanguage = value;
                OnPropertyChanged("TargetLanguage");
            }
        }
        public string Prompt
        {
            get => prompt;
            set
            {
                prompt = value;
                OnPropertyChanged("Prompt");
            }
        }
        public CaptionSourceKind CaptionSource
        {
            get => captionSource;
            set
            {
                if (captionSource == value)
                    return;
                captionSource = value;
                OnPropertyChanged(nameof(CaptionSource));
                OnPropertyChanged(nameof(CaptionSourceDisplayName));
            }
        }
        public string LocalAsrModelDirectory
        {
            get => localAsrModelDirectory;
            set
            {
                string normalized = value?.Trim() ?? string.Empty;
                if (string.Equals(localAsrModelDirectory, normalized, StringComparison.Ordinal))
                    return;
                localAsrModelDirectory = normalized;
                OnPropertyChanged(nameof(LocalAsrModelDirectory));
            }
        }
        [JsonIgnore]
        public string CaptionSourceDisplayName => CaptionSource switch
        {
            CaptionSourceKind.LocalSherpaOnnx => "本地 ASR · sherpa-onnx",
            _ => "Windows Live Captions"
        };
        public string? IgnoredUpdateVersion
        {
            get => ignoredUpdateVersion;
            set
            {
                ignoredUpdateVersion = value;
                OnPropertyChanged("IgnoredUpdateVersion");
            }
        }

        public MainWindowState MainWindow
        {
            get => mainWindowState;
            set
            {
                mainWindowState = value;
                mainWindowState.Owner = this;
                OnPropertyChanged("MainWindow");
            }
        }
        public OverlayWindowState OverlayWindow
        {
            get => overlayWindowState;
            set
            {
                overlayWindowState = value;
                overlayWindowState.Owner = this;
                OnPropertyChanged("OverlayWindow");
            }
        }
        public AppearanceSettings Appearance
        {
            get => appearance;
            set
            {
                appearance = value ?? new AppearanceSettings();
                appearance.Owner = this;
                OnPropertyChanged(nameof(Appearance));
            }
        }
        public Dictionary<string, string> WindowBounds
        {
            get => windowBounds;
            set
            {
                windowBounds = value;
                OnPropertyChanged("WindowBounds");
            }
        }

        [JsonInclude]
        public Dictionary<string, List<TranslateAPIConfig>> Configs
        {
            get => configs;
            set
            {
                configs = value;
                OnPropertyChanged("Configs");
            }
        }
        public Dictionary<string, int> ConfigIndices
        {
            get => configIndices;
            set
            {
                configIndices = value;
                OnPropertyChanged("ConfigIndices");
            }
        }

        public SummaryConfig Summary
        {
            get => summary;
            set
            {
                summary = value ?? new SummaryConfig();
                summary.Owner = this;
                OnPropertyChanged(nameof(Summary));
                OnPropertyChanged(nameof(SummaryEngineDisplayName));
            }
        }

        [JsonIgnore]
        public CredentialStorageIssue CredentialStorageIssue { get; private set; }

        [JsonIgnore]
        public string? CredentialStorageWarning => CredentialStorageIssue switch
        {
            CredentialStorageIssue.ReentryRequired =>
                "无法读取或安全保存 API 凭据。普通设置和课堂记录已保留，旧明文不会被静默使用。请在设置中重新输入需要的凭据。",
            CredentialStorageIssue.MigrationPending =>
                "API 凭据已进入安全存储，但旧设置或备份尚未全部完成脱敏。普通设置和课堂记录已保留，请关闭旧版本后重试。",
            CredentialStorageIssue.SettingsUnreadable =>
                "普通设置文件无法读取，应用未覆盖原文件。课堂记录已保留，请先修复或备份设置文件。",
            _ => null
        };

        public TranslateAPIConfig this[string key] =>
            configs.ContainsKey(key) && configIndices.ContainsKey(key)
                ? configs[key][configIndices[key]]
                : new TranslateAPIConfig();

        [JsonIgnore]
        public string ActiveEngineDisplayName
        {
            get
            {
                if (!configs.TryGetValue(ApiName, out var apiConfigs) ||
                    !configIndices.TryGetValue(ApiName, out int index) ||
                    index < 0 || index >= apiConfigs.Count)
                {
                    return ApiName;
                }

                string modelName = (apiConfigs[index] as BaseLLMConfig)?.ModelName?.Trim() ?? string.Empty;
                return string.IsNullOrEmpty(modelName) ? ApiName : $"{ApiName} / {modelName}";
            }
        }

        [JsonIgnore]
        public string SummaryEngineDisplayName => string.IsNullOrWhiteSpace(Summary.ModelName)
            ? "未配置"
            : $"OpenAI-compatible / {Summary.ModelName.Trim()}";

        public Setting()
        {
            apiName = "Google";
            targetLanguage = "zh-CN";
            prompt = DefaultTranslationPrompt;
            captionSource = CaptionSourceKind.WindowsLiveCaptions;
            localAsrModelDirectory = string.Empty;

            mainWindowState = new MainWindowState();
            overlayWindowState = new OverlayWindowState();
            appearance = new AppearanceSettings();

            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;
            windowBounds = new Dictionary<string, string>
            {
                {
                    "MainWindow", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}, {1}, {2}, {3}", (screenWidth - 775) / 2, screenHeight * 3 / 4 - 167, 775, 167)
                },
                {
                    "OverlayWindow", string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "{0}, {1}, {2}, {3}", (screenWidth - 650) / 2, screenHeight * 5 / 6 - 135, 650, 135)
                },
            };

            configs = new Dictionary<string, List<TranslateAPIConfig>>
            {
                { "Google", [new TranslateAPIConfig()] },
                { "Google2", [new TranslateAPIConfig()] },
                { "Ollama", [new OllamaConfig()] },
                { "OpenAI", [new OpenAIConfig()] },
                { "OpenRouter", [new OpenRouterConfig()] },
                { "DeepL", [new DeepLConfig()] },
                { "Youdao", [new YoudaoConfig()] },
                { "Baidu", [new BaiduConfig()] },
                { "MTranServer", [new MTranServerConfig()] },
                { "LibreTranslate", [new LibreTranslateConfig()] }
            };
            configIndices = new Dictionary<string, int>
            {
                { "Google", 0 },
                { "Google2", 0 },
                { "Ollama", 0 },
                { "OpenAI", 0 },
                { "OpenRouter", 0 },
                { "DeepL", 0 },
                { "Youdao", 0 },
                { "Baidu", 0 },
                { "MTranServer", 0 },
                { "LibreTranslate", 0 }
            };
            summary = new SummaryConfig();
            AttachConfigOwners();
            AttachStateOwners();
        }

        public static Setting Load()
        {
            AppPaths.EnsureCreated();
            var setting = Load(
                AppPaths.Current.SettingsFile,
                AppPaths.Current.CredentialsFile,
                AppPaths.Current.BackupsDirectory,
                writeDiagnostics: true,
                credentialProtector: null);
            setting.autoSaveEnabled = true;
            return setting;
        }

        public static Setting Load(string jsonPath)
        {
            string fullPath = Path.GetFullPath(jsonPath);
            string directory = Path.GetDirectoryName(fullPath)
                ?? throw new ArgumentException("Settings path must include a directory.", nameof(jsonPath));
            return Load(
                fullPath,
                Path.Combine(directory, "credentials.dat"),
                Path.Combine(directory, "Backups"),
                writeDiagnostics: false,
                credentialProtector: null);
        }

        internal static Setting Load(string jsonPath, ICredentialProtector credentialProtector)
        {
            ArgumentNullException.ThrowIfNull(credentialProtector);
            string fullPath = Path.GetFullPath(jsonPath);
            string directory = Path.GetDirectoryName(fullPath)
                ?? throw new ArgumentException("Settings path must include a directory.", nameof(jsonPath));
            return Load(
                fullPath,
                Path.Combine(directory, "credentials.dat"),
                Path.Combine(directory, "Backups"),
                writeDiagnostics: false,
                credentialProtector);
        }

        private static Setting Load(
            string jsonPath,
            string credentialsPath,
            string backupsPath,
            bool writeDiagnostics,
            ICredentialProtector? credentialProtector)
        {
            string? json = null;
            Setting setting;
            try
            {
                if (File.Exists(jsonPath))
                {
                    using FileStream stream = File.Open(
                        jsonPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    json = reader.ReadToEnd();
                    setting = JsonSerializer.Deserialize<Setting>(json, CreateJsonOptions())
                        ?? new Setting();
                }
                else
                    setting = new Setting();
            }
            catch (JsonException exception)
            {
                // Do not copy a malformed settings file into Backups: it can contain
                // legacy plaintext credentials that cannot be safely parsed.
                setting = new Setting();
                setting.ConfigurePersistence(
                    jsonPath,
                    credentialsPath,
                    backupsPath,
                    writeDiagnostics,
                    credentialProtector);
                setting.CredentialStorageIssue = CredentialStorageIssue.SettingsUnreadable;
                setting.persistenceBlocked = true;
                setting.WriteDiagnostic("settings.load.invalid-json", exception);
                return setting;
            }

            // Ensure all required API configs are present
            foreach (string key in TranslateAPI.TRANSLATE_FUNCTIONS.Keys)
            {
                if (setting.Configs.ContainsKey(key))
                    continue;
                var configType = Type.GetType($"LiveCaptionsTranslator.models.{key}Config");
                if (configType != null && typeof(TranslateAPIConfig).IsAssignableFrom(configType))
                    setting.Configs[key] = [(TranslateAPIConfig)Activator.CreateInstance(configType)];
                else
                    setting.Configs[key] = [new TranslateAPIConfig()];
            }

            if (!setting.Configs.ContainsKey(setting.apiName))
                setting.apiName = "Google";

            foreach (var (key, apiConfigs) in setting.Configs)
            {
                if (apiConfigs.Count == 0)
                    apiConfigs.Add(new TranslateAPIConfig());

                if (!setting.ConfigIndices.TryGetValue(key, out int index) ||
                    index < 0 || index >= apiConfigs.Count)
                {
                    setting.ConfigIndices[key] = 0;
                }
            }

            if (string.IsNullOrWhiteSpace(setting.prompt) ||
                string.Equals(
                    setting.prompt,
                    LegacyDefaultTranslationPrompt,
                    StringComparison.Ordinal))
            {
                setting.prompt = DefaultTranslationPrompt;
            }

            setting.AttachConfigOwners();
            setting.AttachStateOwners();
            setting.ConfigurePersistence(
                jsonPath,
                credentialsPath,
                backupsPath,
                writeDiagnostics,
                credentialProtector);
            setting.LoadCredentials(json);
            return setting;
        }

        public void Save()
        {
            AppPaths.EnsureCreated();
            ConfigurePersistence(
                AppPaths.Current.SettingsFile,
                AppPaths.Current.CredentialsFile,
                AppPaths.Current.BackupsDirectory,
                writeDiagnostics: true,
                credentialProtector: null);
            SaveCore();
        }

        public void Save(string jsonPath)
        {
            string fullPath = Path.GetFullPath(jsonPath);
            string directory = Path.GetDirectoryName(fullPath)
                ?? throw new ArgumentException("Settings path must include a directory.", nameof(jsonPath));
            ConfigurePersistence(
                fullPath,
                Path.Combine(directory, "credentials.dat"),
                Path.Combine(directory, "Backups"),
                writeDiagnostics: false,
                credentialProtector: null);
            SaveCore();
        }

        internal void Save(string jsonPath, ICredentialProtector credentialProtector)
        {
            ArgumentNullException.ThrowIfNull(credentialProtector);
            string fullPath = Path.GetFullPath(jsonPath);
            string directory = Path.GetDirectoryName(fullPath)
                ?? throw new ArgumentException("Settings path must include a directory.", nameof(jsonPath));
            ConfigurePersistence(
                fullPath,
                Path.Combine(directory, "credentials.dat"),
                Path.Combine(directory, "Backups"),
                writeDiagnostics: false,
                credentialProtector);
            SaveCore();
        }

        private void SaveCore()
        {
            lock (saveLock)
            {
                if (settingsPath == null || credentialsPath == null || backupsPath == null)
                    throw new InvalidOperationException("Settings persistence paths are not configured.");
                if (persistenceBlocked && !credentialInputChanged)
                {
                    WriteDiagnostic("credentials.save.blocked");
                    return;
                }

                var store = new CredentialStore(credentialsPath, credentialProtector);
                Dictionary<string, string> credentials = SettingCredentialMap.Capture(this);
                try
                {
                    store.Save(credentials);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or
                    System.Security.Cryptography.CryptographicException or JsonException)
                {
                    CredentialStorageIssue = CredentialStorageIssue.ReentryRequired;
                    persistenceBlocked = true;
                    WriteDiagnostic("credentials.save.failed", exception);
                    return;
                }

                try
                {
                    LegacySettingSanitizer.SanitizeApplicationFiles(settingsPath, backupsPath);
                    WriteSettingsFile(settingsPath);
                    LegacySettingSanitizer.VerifyApplicationFilesSanitized(
                        settingsPath,
                        backupsPath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or JsonException or
                    InvalidDataException)
                {
                    CredentialStorageIssue = CredentialStorageIssue.MigrationPending;
                    persistenceBlocked = true;
                    WriteDiagnostic("credentials.settings-sanitization.failed", exception);
                    return;
                }

                credentialInputChanged = false;
                persistenceBlocked = false;
                CredentialStorageIssue = CredentialStorageIssue.None;
            }
        }

        private void LoadCredentials(string? settingsJson)
        {
            if (credentialsPath == null || settingsPath == null || backupsPath == null)
                throw new InvalidOperationException("Settings persistence paths are not configured.");

            var store = new CredentialStore(credentialsPath, credentialProtector);
            CredentialStoreReadResult stored = store.Read();
            if (stored.Status == CredentialStoreReadStatus.Unreadable)
            {
                SettingCredentialMap.Apply(
                    this,
                    new Dictionary<string, string>(StringComparer.Ordinal));
                credentialInputChanged = false;
                persistenceBlocked = true;
                CredentialStorageIssue = CredentialStorageIssue.ReentryRequired;
                WriteDiagnostic("credentials.load.failed", stored.Error);
                return;
            }

            var credentials = new Dictionary<string, string>(
                stored.Credentials,
                StringComparer.Ordinal);
            Dictionary<string, string> legacyCredentials = settingsJson == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : SettingCredentialMap.ExtractLegacyCredentials(settingsJson);

            foreach (var credential in legacyCredentials)
                credentials[credential.Key] = credential.Value;
            SettingCredentialMap.Apply(this, credentials);
            credentialInputChanged = false;

            if (legacyCredentials.Count > 0 ||
                stored.Status == CredentialStoreReadStatus.RecoveredFromBackup)
            {
                try
                {
                    store.Save(credentials);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or
                    System.Security.Cryptography.CryptographicException or JsonException)
                {
                    if (stored.Status is CredentialStoreReadStatus.Success or
                        CredentialStoreReadStatus.RecoveredFromBackup)
                    {
                        SettingCredentialMap.Apply(this, stored.Credentials);
                        CredentialStorageIssue = CredentialStorageIssue.MigrationPending;
                    }
                    else
                    {
                        SettingCredentialMap.Apply(
                            this,
                            new Dictionary<string, string>(StringComparer.Ordinal));
                        CredentialStorageIssue = CredentialStorageIssue.ReentryRequired;
                    }

                    credentialInputChanged = false;
                    persistenceBlocked = true;
                    WriteDiagnostic("credentials.migration.store-failed", exception);
                    return;
                }
            }

            try
            {
                int sanitizedFiles = LegacySettingSanitizer.SanitizeApplicationFiles(
                    settingsPath,
                    backupsPath);
                if (legacyCredentials.Count > 0 && settingsJson != null)
                    WriteSettingsFile(settingsPath);
                LegacySettingSanitizer.VerifyApplicationFilesSanitized(
                    settingsPath,
                    backupsPath);
                if (sanitizedFiles > 0 || legacyCredentials.Count > 0)
                    WriteDiagnostic("credentials.migration.completed");
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException)
            {
                persistenceBlocked = true;
                CredentialStorageIssue = CredentialStorageIssue.MigrationPending;
                WriteDiagnostic("credentials.migration.cleanup-failed", exception);
                return;
            }

            persistenceBlocked = false;
            CredentialStorageIssue = CredentialStorageIssue.None;
        }

        private void ConfigurePersistence(
            string settingsPath,
            string credentialsPath,
            string backupsPath,
            bool writeDiagnostics,
            ICredentialProtector? credentialProtector)
        {
            this.settingsPath = Path.GetFullPath(settingsPath);
            this.credentialsPath = Path.GetFullPath(credentialsPath);
            this.backupsPath = Path.GetFullPath(backupsPath);
            this.writeDiagnostics = writeDiagnostics;
            if (credentialProtector != null)
                this.credentialProtector = credentialProtector;
        }

        private void WriteSettingsFile(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (directory != null)
                Directory.CreateDirectory(directory);

            string tempPath = fullPath + $".tmp-{Guid.NewGuid():N}";
            try
            {
                using (FileStream stream = new(
                           tempPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 4096,
                           FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, this, CreateJsonOptions());
                    stream.Flush(flushToDisk: true);
                }

                JsonNode verified = JsonNode.Parse(File.ReadAllText(tempPath))
                    ?? throw new JsonException("Serialized settings are empty.");
                if (SettingCredentialMap.CountLegacyFields(verified) != 0)
                    throw new InvalidDataException("Serialized settings contain credential fields.");

                if (File.Exists(fullPath))
                    File.Replace(tempPath, fullPath, fullPath + ".bak", ignoreMetadataErrors: true);
                else
                    File.Move(tempPath, fullPath);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        private static JsonSerializerOptions CreateJsonOptions()
        {
            return new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new ConfigDictConverter() }
            };
        }

        private void WriteDiagnostic(string eventName, Exception? exception = null)
        {
            if (writeDiagnostics)
                ProductDiagnostics.Write(eventName, exception);
        }

        public void NotifyConfigurationChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Configs)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveEngineDisplayName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SummaryEngineDisplayName)));
            if (autoSaveEnabled)
                Save();
        }

        public void NotifyCredentialChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Configs)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
            credentialInputChanged = true;
            if (autoSaveEnabled)
                Save();
        }

        public bool HasCredentials(string provider)
        {
            return SettingCredentialMap.HasSelected(this, provider);
        }

        public bool ClearCredentials(string provider)
        {
            bool previousAutoSave = autoSaveEnabled;
            bool changed;
            autoSaveEnabled = false;
            try
            {
                changed = SettingCredentialMap.ClearSelected(this, provider);
                if (changed)
                {
                    credentialInputChanged = true;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Configs)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
                }
            }
            finally
            {
                autoSaveEnabled = previousAutoSave;
            }

            if (changed && previousAutoSave)
                Save();
            return changed;
        }

        public void OnPropertyChanged([CallerMemberName] string? propName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            if (autoSaveEnabled)
                Save();
        }

        private void AttachConfigOwners()
        {
            foreach (var apiConfigs in configs.Values)
            {
                foreach (var config in apiConfigs)
                    config.Owner = this;
            }

            summary.Owner = this;
        }

        internal void AttachConfigOwner(TranslateAPIConfig config)
        {
            config.Owner = this;
        }

        internal void SaveIfEnabled()
        {
            if (autoSaveEnabled)
                Save();
        }

        private void AttachStateOwners()
        {
            mainWindowState.Owner = this;
            overlayWindowState.Owner = this;
            appearance ??= new AppearanceSettings();
            appearance.Owner = this;
        }

        public static bool IsConfigExist()
        {
            return File.Exists(AppPaths.Current.SettingsFile);
        }
    }
}
