using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    internal enum CredentialStoreReadStatus
    {
        Missing,
        Success,
        RecoveredFromBackup,
        Unreadable
    }

    internal sealed record CredentialStoreReadResult(
        CredentialStoreReadStatus Status,
        IReadOnlyDictionary<string, string> Credentials,
        Exception? Error = null);

    internal interface ICredentialProtector
    {
        byte[] Protect(byte[] plaintext);
        byte[] Unprotect(byte[] ciphertext);
    }

    internal sealed class CurrentUserDpapiProtector : ICredentialProtector
    {
        private static readonly byte[] AdditionalEntropy =
            Encoding.UTF8.GetBytes("LectureCopilot.Dev/CredentialStore/v1");

        public byte[] Protect(byte[] plaintext)
        {
            return ProtectedData.Protect(
                plaintext,
                AdditionalEntropy,
                DataProtectionScope.CurrentUser);
        }

        public byte[] Unprotect(byte[] ciphertext)
        {
            return ProtectedData.Unprotect(
                ciphertext,
                AdditionalEntropy,
                DataProtectionScope.CurrentUser);
        }
    }

    internal sealed class CredentialStore
    {
        internal const string ProtectionName = "DPAPI-CurrentUser";
        private const int CurrentVersion = 1;

        private readonly string filePath;
        private readonly ICredentialProtector protector;

        public CredentialStore(string filePath, ICredentialProtector? protector = null)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("Credential file path cannot be empty.", nameof(filePath));

            this.filePath = Path.GetFullPath(filePath);
            this.protector = protector ?? new CurrentUserDpapiProtector();
        }

        public CredentialStoreReadResult Read()
        {
            CredentialStoreReadResult primary = ReadFile(filePath);
            if (primary.Status == CredentialStoreReadStatus.Success)
                return primary;

            CredentialStoreReadResult backup = ReadFile(filePath + ".bak");
            if (backup.Status == CredentialStoreReadStatus.Success)
            {
                return new CredentialStoreReadResult(
                    CredentialStoreReadStatus.RecoveredFromBackup,
                    backup.Credentials);
            }

            if (primary.Status == CredentialStoreReadStatus.Missing &&
                backup.Status == CredentialStoreReadStatus.Missing)
            {
                return new CredentialStoreReadResult(
                    CredentialStoreReadStatus.Missing,
                    new Dictionary<string, string>(StringComparer.Ordinal));
            }

            return new CredentialStoreReadResult(
                CredentialStoreReadStatus.Unreadable,
                new Dictionary<string, string>(StringComparer.Ordinal),
                primary.Error ?? backup.Error);
        }

        public void Save(IReadOnlyDictionary<string, string> credentials)
        {
            ArgumentNullException.ThrowIfNull(credentials);

            var normalized = credentials
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(normalized);
            byte[]? ciphertext = null;
            string? tempPath = null;

            try
            {
                ciphertext = protector.Protect(plaintext);
                var envelope = new CredentialEnvelope
                {
                    Version = CurrentVersion,
                    Protection = ProtectionName,
                    Ciphertext = Convert.ToBase64String(ciphertext)
                };

                string? directory = Path.GetDirectoryName(filePath);
                if (directory != null)
                    Directory.CreateDirectory(directory);
                if (Directory.Exists(filePath))
                    throw new IOException("Credential file path points to a directory.");

                tempPath = filePath + $".tmp-{Guid.NewGuid():N}";
                WriteEnvelope(tempPath, envelope);
                EnsureMatches(ReadFile(tempPath), normalized);

                if (File.Exists(filePath))
                    File.Replace(tempPath, filePath, filePath + ".bak", ignoreMetadataErrors: true);
                else
                    File.Move(tempPath, filePath);
                tempPath = null;

                EnsureMatches(ReadFile(filePath), normalized);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                if (ciphertext != null)
                    CryptographicOperations.ZeroMemory(ciphertext);
                if (tempPath != null && File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        private CredentialStoreReadResult ReadFile(string path)
        {
            if (Directory.Exists(path))
            {
                return new CredentialStoreReadResult(
                    CredentialStoreReadStatus.Unreadable,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    new IOException("Credential file path points to a directory."));
            }
            if (!File.Exists(path))
            {
                return new CredentialStoreReadResult(
                    CredentialStoreReadStatus.Missing,
                    new Dictionary<string, string>(StringComparer.Ordinal));
            }

            byte[]? plaintext = null;
            byte[]? ciphertext = null;
            try
            {
                using FileStream stream = File.Open(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                var envelope = JsonSerializer.Deserialize<CredentialEnvelope>(stream)
                    ?? throw new JsonException("Credential envelope is empty.");
                if (envelope.Version != CurrentVersion ||
                    !string.Equals(envelope.Protection, ProtectionName, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(envelope.Ciphertext))
                {
                    throw new JsonException("Credential envelope format is unsupported.");
                }

                ciphertext = Convert.FromBase64String(envelope.Ciphertext);
                plaintext = protector.Unprotect(ciphertext);
                var credentials = JsonSerializer.Deserialize<Dictionary<string, string>>(plaintext)
                    ?? new Dictionary<string, string>();
                if (credentials.Any(entry => entry.Key == null || entry.Value == null))
                    throw new JsonException("Credential payload contains null entries.");

                return new CredentialStoreReadResult(
                    CredentialStoreReadStatus.Success,
                    new Dictionary<string, string>(credentials, StringComparer.Ordinal));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or CryptographicException or
                JsonException or FormatException or NotSupportedException)
            {
                return new CredentialStoreReadResult(
                    CredentialStoreReadStatus.Unreadable,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    exception);
            }
            finally
            {
                if (plaintext != null)
                    CryptographicOperations.ZeroMemory(plaintext);
                if (ciphertext != null)
                    CryptographicOperations.ZeroMemory(ciphertext);
            }
        }

        private static void WriteEnvelope(string path, CredentialEnvelope envelope)
        {
            using FileStream stream = new(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            JsonSerializer.Serialize(stream, envelope, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(flushToDisk: true);
        }

        private static void EnsureMatches(
            CredentialStoreReadResult result,
            IReadOnlyDictionary<string, string> expected)
        {
            if (result.Status != CredentialStoreReadStatus.Success ||
                result.Credentials.Count != expected.Count ||
                expected.Any(entry =>
                    !result.Credentials.TryGetValue(entry.Key, out string? value) ||
                    !string.Equals(entry.Value, value, StringComparison.Ordinal)))
            {
                throw new CryptographicException("Credential store verification failed.");
            }
        }

        private sealed class CredentialEnvelope
        {
            public int Version { get; set; }
            public string Protection { get; set; } = string.Empty;
            public string Ciphertext { get; set; } = string.Empty;
        }
    }

    internal static class SettingCredentialMap
    {
        private static readonly IReadOnlyDictionary<string, string[]> ProviderFields =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                { "OpenAI", [nameof(OpenAIConfig.ApiKey)] },
                { "OpenRouter", [nameof(OpenRouterConfig.ApiKey)] },
                { "DeepL", [nameof(DeepLConfig.ApiKey)] },
                { "Youdao", [nameof(YoudaoConfig.AppKey), nameof(YoudaoConfig.AppSecret)] },
                { "MTranServer", [nameof(MTranServerConfig.ApiKey)] },
                { "Baidu", [nameof(BaiduConfig.AppId), nameof(BaiduConfig.AppSecret)] },
                { "LibreTranslate", [nameof(LibreTranslateConfig.ApiKey)] }
            };

        private const string SummaryApiKey = "Summary.ApiKey";

        public static Dictionary<string, string> Capture(Setting setting)
        {
            var credentials = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (provider, fields) in ProviderFields)
            {
                if (!setting.Configs.TryGetValue(provider, out var configs))
                    continue;

                for (int index = 0; index < configs.Count; index++)
                {
                    foreach (string field in fields)
                    {
                        string? value = ReadProperty(configs[index], field);
                        if (!string.IsNullOrWhiteSpace(value))
                            credentials[BuildPath(provider, index, field)] = value;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(setting.Summary.ApiKey))
                credentials[SummaryApiKey] = setting.Summary.ApiKey;
            return credentials;
        }

        public static void Apply(Setting setting, IReadOnlyDictionary<string, string> credentials)
        {
            foreach (var (provider, fields) in ProviderFields)
            {
                if (!setting.Configs.TryGetValue(provider, out var configs))
                    continue;

                for (int index = 0; index < configs.Count; index++)
                {
                    foreach (string field in fields)
                    {
                        credentials.TryGetValue(BuildPath(provider, index, field), out string? value);
                        WriteProperty(configs[index], field, value ?? string.Empty);
                    }
                }
            }

            setting.Summary.ApiKey = credentials.TryGetValue(SummaryApiKey, out string? summaryKey)
                ? summaryKey
                : string.Empty;
        }

        public static bool HasSelected(Setting setting, string provider)
        {
            if (provider == "Summary")
                return !string.IsNullOrWhiteSpace(setting.Summary.ApiKey);
            if (!TryGetSelectedConfig(setting, provider, out TranslateAPIConfig config) ||
                !ProviderFields.TryGetValue(provider, out string[]? fields))
            {
                return false;
            }

            return fields.Any(field => !string.IsNullOrWhiteSpace(ReadProperty(config, field)));
        }

        public static bool ClearSelected(Setting setting, string provider)
        {
            if (provider == "Summary")
            {
                if (string.IsNullOrWhiteSpace(setting.Summary.ApiKey))
                    return false;
                setting.Summary.ApiKey = string.Empty;
                return true;
            }

            if (!TryGetSelectedConfig(setting, provider, out TranslateAPIConfig config) ||
                !ProviderFields.TryGetValue(provider, out string[]? fields))
            {
                return false;
            }

            bool changed = false;
            foreach (string field in fields)
            {
                if (string.IsNullOrWhiteSpace(ReadProperty(config, field)))
                    continue;
                WriteProperty(config, field, string.Empty);
                changed = true;
            }
            return changed;
        }

        public static Dictionary<string, string> ExtractLegacyCredentials(string json)
        {
            JsonNode root = JsonNode.Parse(json)
                ?? throw new JsonException("Settings document is empty.");
            var credentials = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root["Configs"] is JsonObject configsObject)
            {
                foreach (var (provider, fields) in ProviderFields)
                {
                    if (configsObject[provider] is not JsonArray configs)
                        continue;

                    for (int index = 0; index < configs.Count; index++)
                    {
                        if (configs[index] is not JsonObject config)
                            continue;
                        foreach (string field in fields)
                        {
                            string? value = config[field]?.GetValue<string>();
                            if (!string.IsNullOrWhiteSpace(value))
                                credentials[BuildPath(provider, index, field)] = value;
                        }
                    }
                }
            }

            string? summaryKey = root["Summary"]?["ApiKey"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(summaryKey))
                credentials[SummaryApiKey] = summaryKey;
            return credentials;
        }

        public static int RemoveLegacyFields(JsonNode root)
        {
            int removed = 0;
            if (root["Configs"] is JsonObject configsObject)
            {
                foreach (var (provider, fields) in ProviderFields)
                {
                    if (configsObject[provider] is not JsonArray configs)
                        continue;
                    foreach (JsonNode? node in configs)
                    {
                        if (node is not JsonObject config)
                            continue;
                        foreach (string field in fields)
                        {
                            if (config.Remove(field))
                                removed++;
                        }
                    }
                }
            }

            if (root["Summary"] is JsonObject summary && summary.Remove("ApiKey"))
                removed++;
            return removed;
        }

        public static int CountLegacyFields(JsonNode root)
        {
            int count = 0;
            if (root["Configs"] is JsonObject configsObject)
            {
                foreach (var (provider, fields) in ProviderFields)
                {
                    if (configsObject[provider] is not JsonArray configs)
                        continue;
                    foreach (JsonNode? node in configs)
                    {
                        if (node is not JsonObject config)
                            continue;
                        count += fields.Count(config.ContainsKey);
                    }
                }
            }

            if (root["Summary"] is JsonObject summary && summary.ContainsKey("ApiKey"))
                count++;
            return count;
        }

        private static string BuildPath(string provider, int index, string field)
        {
            return $"Configs.{provider}[{index}].{field}";
        }

        private static bool TryGetSelectedConfig(
            Setting setting,
            string provider,
            out TranslateAPIConfig config)
        {
            config = null!;
            if (!setting.Configs.TryGetValue(provider, out List<TranslateAPIConfig>? configs) ||
                !setting.ConfigIndices.TryGetValue(provider, out int index) ||
                index < 0 || index >= configs.Count)
            {
                return false;
            }

            config = configs[index];
            return true;
        }

        private static string? ReadProperty(TranslateAPIConfig config, string propertyName)
        {
            PropertyInfo? property = config.GetType().GetProperty(propertyName);
            return property?.PropertyType == typeof(string)
                ? property.GetValue(config) as string
                : null;
        }

        private static void WriteProperty(
            TranslateAPIConfig config,
            string propertyName,
            string value)
        {
            PropertyInfo? property = config.GetType().GetProperty(propertyName);
            if (property?.PropertyType == typeof(string) && property.CanWrite)
                property.SetValue(config, value);
        }
    }

    internal static class LegacySettingSanitizer
    {
        public static int SanitizeApplicationFiles(
            string settingsPath,
            string backupsDirectory)
        {
            string fullSettingsPath = Path.GetFullPath(settingsPath);
            IReadOnlyList<string> paths = GetManagedFiles(
                fullSettingsPath,
                backupsDirectory,
                includeActiveSettings: false);

            int changedFiles = 0;
            foreach (string path in paths)
                changedFiles += SanitizeFile(path) ? 1 : 0;

            // Keep the active file until every backup has been handled. If a backup
            // cannot be sanitized, the original settings remain recoverable.
            if (File.Exists(fullSettingsPath))
                changedFiles += SanitizeFile(fullSettingsPath) ? 1 : 0;
            return changedFiles;
        }

        public static void VerifyApplicationFilesSanitized(
            string settingsPath,
            string backupsDirectory)
        {
            foreach (string path in GetManagedFiles(
                         settingsPath,
                         backupsDirectory,
                         includeActiveSettings: true))
            {
                string json;
                using (FileStream stream = File.Open(
                           path,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    json = reader.ReadToEnd();
                }

                JsonNode root = JsonNode.Parse(json)
                    ?? throw new JsonException("Managed settings file is empty.");
                if (SettingCredentialMap.CountLegacyFields(root) != 0)
                {
                    throw new InvalidDataException(
                        "Managed settings still contain credential fields after migration.");
                }
            }
        }

        private static IReadOnlyList<string> GetManagedFiles(
            string settingsPath,
            string backupsDirectory,
            bool includeActiveSettings)
        {
            string fullSettingsPath = Path.GetFullPath(settingsPath);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string automaticBackup = fullSettingsPath + ".bak";
            if (File.Exists(automaticBackup))
                paths.Add(automaticBackup);

            if (Directory.Exists(backupsDirectory))
            {
                foreach (string backup in Directory.GetFiles(
                             backupsDirectory,
                             "setting*.json",
                             SearchOption.TopDirectoryOnly))
                {
                    paths.Add(Path.GetFullPath(backup));
                }
            }

            if (includeActiveSettings && File.Exists(fullSettingsPath))
                paths.Add(fullSettingsPath);

            return paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static bool SanitizeFile(string path)
        {
            string json;
            using (FileStream stream = File.Open(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                json = reader.ReadToEnd();
            }

            JsonNode root = JsonNode.Parse(json)
                ?? throw new JsonException("Settings backup is empty.");
            if (SettingCredentialMap.RemoveLegacyFields(root) == 0)
                return false;

            string fullPath = Path.GetFullPath(path);
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
                using (var writer = new Utf8JsonWriter(
                           stream,
                           new JsonWriterOptions { Indented = true }))
                {
                    root.WriteTo(writer);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                string verifiedJson = File.ReadAllText(tempPath);
                JsonNode verified = JsonNode.Parse(verifiedJson)
                    ?? throw new JsonException("Sanitized settings backup is empty.");
                if (SettingCredentialMap.CountLegacyFields(verified) != 0)
                    throw new InvalidDataException("Sanitized settings still contain credential fields.");

                File.Replace(tempPath, fullPath, null, ignoreMetadataErrors: true);
                return true;
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }
    }
}
