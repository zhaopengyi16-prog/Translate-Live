using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class CredentialPersistenceTests
    {
        private const string OpenAiCredential = "unit-openai-credential";
        private const string SecondOpenAiCredential = "unit-openai-secondary-credential";
        private const string SummaryCredential = "unit-summary-credential";
        private const string OpenRouterCredential = "unit-openrouter-credential";
        private const string DeepLCredential = "unit-deepl-credential";
        private const string YoudaoAppKey = "unit-youdao-app-key";
        private const string YoudaoAppSecret = "unit-youdao-app-secret";
        private const string MTranCredential = "unit-mtran-credential";
        private const string BaiduAppId = "unit-baidu-app-id";
        private const string BaiduAppSecret = "unit-baidu-app-secret";
        private const string LibreCredential = "unit-libre-credential";
        private static readonly ICredentialProtector TestProtector = new TestCredentialProtector();

        private static readonly string[] AllCredentialValues =
        [
            OpenAiCredential,
            SecondOpenAiCredential,
            SummaryCredential,
            OpenRouterCredential,
            DeepLCredential,
            YoudaoAppKey,
            YoudaoAppSecret,
            MTranCredential,
            BaiduAppId,
            BaiduAppSecret,
            LibreCredential
        ];

        [TestMethod]
        public void CredentialStoreRoundTripsAndRecoversEncryptedBackup()
        {
            string root = CreateRoot();
            string path = Path.Combine(root, "credentials.dat");
            try
            {
                var store = new CredentialStore(path, TestProtector);
                store.Save(new Dictionary<string, string> { ["service"] = OpenAiCredential });
                store.Save(new Dictionary<string, string> { ["service"] = SummaryCredential });

                CredentialStoreReadResult current = store.Read();
                Assert.AreEqual(CredentialStoreReadStatus.Success, current.Status);
                Assert.AreEqual(SummaryCredential, current.Credentials["service"]);
                Assert.IsTrue(File.Exists(path + ".bak"));
                AssertFileDoesNotContain(path, OpenAiCredential, SummaryCredential);
                AssertFileDoesNotContain(path + ".bak", OpenAiCredential, SummaryCredential);

                File.WriteAllText(path, "{\"Version\":1,\"Protection\":\"DPAPI-CurrentUser\",\"Ciphertext\":\"invalid\"}");
                CredentialStoreReadResult recovered = store.Read();
                Assert.AreEqual(CredentialStoreReadStatus.RecoveredFromBackup, recovered.Status);
                Assert.AreEqual(OpenAiCredential, recovered.Credentials["service"]);
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        [TestMethod]
        public void SaveAndReloadKeepsAllCredentialFieldsOutOfOrdinarySettings()
        {
            string root = CreateRoot();
            string settingsPath = Path.Combine(root, "setting.json");
            string credentialsPath = Path.Combine(root, "credentials.dat");
            try
            {
                Setting setting = CreateSettingWithCredentials();
                setting.Save(settingsPath, TestProtector);
                setting.TargetLanguage = "zh-TW";
                setting.Save(settingsPath);

                Assert.AreEqual(CredentialStorageIssue.None, setting.CredentialStorageIssue);
                AssertNoCredentialFields(settingsPath);
                AssertNoCredentialFields(settingsPath + ".bak");
                AssertFileDoesNotContain(credentialsPath, AllCredentialValues);
                AssertFileDoesNotContain(credentialsPath + ".bak", AllCredentialValues);

                JsonNode envelope = JsonNode.Parse(File.ReadAllText(credentialsPath))!;
                Assert.AreEqual(CredentialStore.ProtectionName, envelope["Protection"]!.GetValue<string>());

                Setting loaded = Setting.Load(settingsPath, TestProtector);
                Assert.AreEqual(CredentialStorageIssue.None, loaded.CredentialStorageIssue);
                Assert.AreEqual("zh-TW", loaded.TargetLanguage);
                AssertCredentialsLoaded(loaded);
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        [TestMethod]
        public void ClearCredentialsOnlyClearsTheSelectedProviderConfiguration()
        {
            string root = CreateRoot();
            string settingsPath = Path.Combine(root, "setting.json");
            try
            {
                Setting setting = CreateSettingWithCredentials();
                setting.ConfigIndices["OpenAI"] = 1;

                Assert.IsTrue(setting.HasCredentials("OpenAI"));
                Assert.IsTrue(setting.ClearCredentials("OpenAI"));
                Assert.AreEqual(
                    OpenAiCredential,
                    ((OpenAIConfig)setting.Configs["OpenAI"][0]).ApiKey);
                Assert.AreEqual(
                    string.Empty,
                    ((OpenAIConfig)setting.Configs["OpenAI"][1]).ApiKey);
                Assert.AreEqual(SummaryCredential, setting.Summary.ApiKey);
                Assert.IsFalse(setting.ClearCredentials("Google"));

                Assert.IsTrue(setting.ClearCredentials("Summary"));
                Assert.AreEqual(string.Empty, setting.Summary.ApiKey);
                setting.Save(settingsPath, TestProtector);

                Setting reloaded = Setting.Load(settingsPath, TestProtector);
                Assert.AreEqual(
                    OpenAiCredential,
                    ((OpenAIConfig)reloaded.Configs["OpenAI"][0]).ApiKey);
                Assert.AreEqual(
                    string.Empty,
                    ((OpenAIConfig)reloaded.Configs["OpenAI"][1]).ApiKey);
                Assert.AreEqual(string.Empty, reloaded.Summary.ApiKey);
                Assert.AreEqual(OpenRouterCredential,
                    ((OpenRouterConfig)reloaded.Configs["OpenRouter"][0]).ApiKey);
                AssertNoCredentialFields(settingsPath);
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        [TestMethod]
        public void LegacyMigrationSanitizesManagedFilesAndIsIdempotent()
        {
            string root = CreateRoot();
            string settingsPath = Path.Combine(root, "setting.json");
            string credentialsPath = Path.Combine(root, "credentials.dat");
            string backups = Path.Combine(root, "Backups");
            try
            {
                CreateLegacySettings(settingsPath);
                Directory.CreateDirectory(backups);
                File.Copy(settingsPath, settingsPath + ".bak");
                string managedBackup = Path.Combine(backups, "setting.before-migration.json");
                File.Copy(settingsPath, managedBackup);

                Setting migrated = Setting.Load(settingsPath, TestProtector);
                Assert.AreEqual(CredentialStorageIssue.None, migrated.CredentialStorageIssue);
                AssertCredentialsLoaded(migrated);
                AssertNoCredentialFields(settingsPath);
                AssertNoCredentialFields(settingsPath + ".bak");
                AssertNoCredentialFields(managedBackup);
                Assert.AreEqual(
                    "zh-CN",
                    JsonNode.Parse(File.ReadAllText(managedBackup))!["TargetLanguage"]!.GetValue<string>());
                AssertFileDoesNotContain(credentialsPath, AllCredentialValues);

                string hashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(credentialsPath)));
                Setting loadedAgain = Setting.Load(settingsPath, TestProtector);
                string hashAfter = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(credentialsPath)));
                Assert.AreEqual(CredentialStorageIssue.None, loadedAgain.CredentialStorageIssue);
                AssertCredentialsLoaded(loadedAgain);
                Assert.AreEqual(hashBefore, hashAfter);
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        [TestMethod]
        public void ExistingProtectedStoreCleansReintroducedLegacyFields()
        {
            string root = CreateRoot();
            string settingsPath = Path.Combine(root, "setting.json");
            string backups = Path.Combine(root, "Backups");
            try
            {
                Setting secured = CreateSettingWithCredentials();
                secured.Save(settingsPath, TestProtector);

                JsonObject legacy = (JsonObject)JsonNode.Parse(File.ReadAllText(settingsPath))!;
                JsonObject configs = (JsonObject)legacy["Configs"]!;
                SetLegacy(configs, "OpenAI", 0, "ApiKey", OpenAiCredential);
                ((JsonObject)legacy["Summary"]!)["ApiKey"] = SummaryCredential;
                File.WriteAllText(
                    settingsPath,
                    legacy.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Copy(settingsPath, settingsPath + ".bak", overwrite: true);
                Directory.CreateDirectory(backups);
                string managedBackup = Path.Combine(backups, "setting.reintroduced.json");
                File.Copy(settingsPath, managedBackup);

                Setting migrated = Setting.Load(settingsPath, TestProtector);

                Assert.AreEqual(CredentialStorageIssue.None, migrated.CredentialStorageIssue);
                AssertCredentialsLoaded(migrated);
                AssertNoCredentialFields(settingsPath);
                AssertNoCredentialFields(settingsPath + ".bak");
                AssertNoCredentialFields(managedBackup);
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        [TestMethod]
        public void MigrationCleanupFailureKeepsSourceRecoverableAndCanRetry()
        {
            string root = CreateRoot();
            string settingsPath = Path.Combine(root, "setting.json");
            string credentialsPath = Path.Combine(root, "credentials.dat");
            string backups = Path.Combine(root, "Backups");
            string invalidBackup = Path.Combine(backups, "setting.invalid.json");
            try
            {
                CreateLegacySettings(settingsPath);
                Directory.CreateDirectory(backups);
                File.WriteAllText(invalidBackup, "{invalid-json");

                Setting pending = Setting.Load(settingsPath, TestProtector);
                Assert.AreEqual(CredentialStorageIssue.MigrationPending, pending.CredentialStorageIssue);
                AssertCredentialsLoaded(pending);
                Assert.IsTrue(SettingCredentialMap.CountLegacyFields(
                    JsonNode.Parse(File.ReadAllText(settingsPath))!) > 0);
                Assert.AreEqual(
                    CredentialStoreReadStatus.Success,
                    new CredentialStore(credentialsPath, TestProtector).Read().Status);

                File.WriteAllText(invalidBackup, "{}");
                Setting retried = Setting.Load(settingsPath, TestProtector);
                Assert.AreEqual(CredentialStorageIssue.None, retried.CredentialStorageIssue);
                AssertCredentialsLoaded(retried);
                AssertNoCredentialFields(settingsPath);
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        [TestMethod]
        public void InitialMigrationProtectionFailureKeepsLegacySourceUntouched()
        {
            string root = CreateRoot();
            string settingsPath = Path.Combine(root, "setting.json");
            string credentialsPath = Path.Combine(root, "credentials.dat");
            try
            {
                CreateLegacySettings(settingsPath);
                string hashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(settingsPath)));

                Setting blocked = Setting.Load(settingsPath, new ThrowingProtector());

                Assert.AreEqual(CredentialStorageIssue.ReentryRequired, blocked.CredentialStorageIssue);
                Assert.AreEqual(string.Empty, ((OpenAIConfig)blocked.Configs["OpenAI"][0]).ApiKey);
                Assert.AreEqual(hashBefore, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(settingsPath))));
                Assert.IsTrue(SettingCredentialMap.CountLegacyFields(
                    JsonNode.Parse(File.ReadAllText(settingsPath))!) > 0);
                Assert.IsFalse(File.Exists(credentialsPath));
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        [TestMethod]
        public void CorruptedCiphertextRequiresReentryWithoutOverwritingOrdinarySettings()
        {
            string root = CreateRoot();
            string settingsPath = Path.Combine(root, "setting.json");
            string credentialsPath = Path.Combine(root, "credentials.dat");
            try
            {
                Setting setting = CreateSettingWithCredentials();
                setting.TargetLanguage = "ja-JP";
                setting.Save(settingsPath, TestProtector);
                File.WriteAllText(
                    credentialsPath,
                    "{\"Version\":1,\"Protection\":\"DPAPI-CurrentUser\",\"Ciphertext\":\"invalid\"}");
                if (File.Exists(credentialsPath + ".bak"))
                    File.Delete(credentialsPath + ".bak");
                string settingsHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(settingsPath)));
                string corruptHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(credentialsPath)));

                Setting loaded = Setting.Load(settingsPath, TestProtector);
                Assert.AreEqual(CredentialStorageIssue.ReentryRequired, loaded.CredentialStorageIssue);
                Assert.AreEqual("ja-JP", loaded.TargetLanguage);
                Assert.AreEqual(string.Empty, ((OpenAIConfig)loaded.Configs["OpenAI"][0]).ApiKey);
                loaded.Save(settingsPath);
                Assert.AreEqual(settingsHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(settingsPath))));
                Assert.AreEqual(corruptHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(credentialsPath))));

                ((OpenAIConfig)loaded.Configs["OpenAI"][0]).ApiKey = OpenAiCredential;
                loaded.Save(settingsPath);
                Assert.AreEqual(CredentialStorageIssue.None, loaded.CredentialStorageIssue);
                Setting recovered = Setting.Load(settingsPath, TestProtector);
                Assert.AreEqual(OpenAiCredential, ((OpenAIConfig)recovered.Configs["OpenAI"][0]).ApiKey);
                Assert.AreEqual(string.Empty, recovered.Summary.ApiKey);
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        [TestMethod]
        public void CredentialStoreWriteFailureKeepsPreviousCiphertext()
        {
            string root = CreateRoot();
            string path = Path.Combine(root, "credentials.dat");
            try
            {
                var store = new CredentialStore(path, TestProtector);
                store.Save(new Dictionary<string, string> { ["service"] = OpenAiCredential });
                string hashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

                var failingStore = new CredentialStore(path, new ThrowingProtector());
                Assert.ThrowsExactly<CryptographicException>(() =>
                    failingStore.Save(new Dictionary<string, string> { ["service"] = SummaryCredential }));

                string hashAfter = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                Assert.AreEqual(hashBefore, hashAfter);
                Assert.AreEqual(OpenAiCredential, store.Read().Credentials["service"]);
            }
            finally
            {
                DeleteRoot(root);
            }
        }

        private static Setting CreateSettingWithCredentials()
        {
            var setting = new Setting
            {
                ApiName = "OpenAI",
                TargetLanguage = "zh-CN"
            };
            ((OpenAIConfig)setting.Configs["OpenAI"][0]).ApiKey = OpenAiCredential;
            var secondOpenAi = new OpenAIConfig { ApiKey = SecondOpenAiCredential };
            setting.Configs["OpenAI"].Add(secondOpenAi);
            ((OpenRouterConfig)setting.Configs["OpenRouter"][0]).ApiKey = OpenRouterCredential;
            ((DeepLConfig)setting.Configs["DeepL"][0]).ApiKey = DeepLCredential;
            ((YoudaoConfig)setting.Configs["Youdao"][0]).AppKey = YoudaoAppKey;
            ((YoudaoConfig)setting.Configs["Youdao"][0]).AppSecret = YoudaoAppSecret;
            ((MTranServerConfig)setting.Configs["MTranServer"][0]).ApiKey = MTranCredential;
            ((BaiduConfig)setting.Configs["Baidu"][0]).AppId = BaiduAppId;
            ((BaiduConfig)setting.Configs["Baidu"][0]).AppSecret = BaiduAppSecret;
            ((LibreTranslateConfig)setting.Configs["LibreTranslate"][0]).ApiKey = LibreCredential;
            setting.Summary.ApiKey = SummaryCredential;
            return setting;
        }

        private static void CreateLegacySettings(string settingsPath)
        {
            Setting setting = CreateSettingWithCredentials();
            setting.Save(settingsPath, TestProtector);
            string credentialsPath = Path.Combine(Path.GetDirectoryName(settingsPath)!, "credentials.dat");
            if (File.Exists(credentialsPath))
                File.Delete(credentialsPath);
            if (File.Exists(credentialsPath + ".bak"))
                File.Delete(credentialsPath + ".bak");

            JsonObject root = (JsonObject)JsonNode.Parse(File.ReadAllText(settingsPath))!;
            JsonObject configs = (JsonObject)root["Configs"]!;
            SetLegacy(configs, "OpenAI", 0, "ApiKey", OpenAiCredential);
            SetLegacy(configs, "OpenAI", 1, "ApiKey", SecondOpenAiCredential);
            SetLegacy(configs, "OpenRouter", 0, "ApiKey", OpenRouterCredential);
            SetLegacy(configs, "DeepL", 0, "ApiKey", DeepLCredential);
            SetLegacy(configs, "Youdao", 0, "AppKey", YoudaoAppKey);
            SetLegacy(configs, "Youdao", 0, "AppSecret", YoudaoAppSecret);
            SetLegacy(configs, "MTranServer", 0, "ApiKey", MTranCredential);
            SetLegacy(configs, "Baidu", 0, "AppId", BaiduAppId);
            SetLegacy(configs, "Baidu", 0, "AppSecret", BaiduAppSecret);
            SetLegacy(configs, "LibreTranslate", 0, "ApiKey", LibreCredential);
            ((JsonObject)root["Summary"]!)["ApiKey"] = SummaryCredential;
            File.WriteAllText(
                settingsPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void SetLegacy(
            JsonObject configs,
            string provider,
            int index,
            string field,
            string value)
        {
            ((JsonObject)((JsonArray)configs[provider]!)[index]!)[field] = value;
        }

        private static void AssertCredentialsLoaded(Setting setting)
        {
            Assert.AreEqual(OpenAiCredential, ((OpenAIConfig)setting.Configs["OpenAI"][0]).ApiKey);
            Assert.AreEqual(SecondOpenAiCredential, ((OpenAIConfig)setting.Configs["OpenAI"][1]).ApiKey);
            Assert.AreEqual(OpenRouterCredential, ((OpenRouterConfig)setting.Configs["OpenRouter"][0]).ApiKey);
            Assert.AreEqual(DeepLCredential, ((DeepLConfig)setting.Configs["DeepL"][0]).ApiKey);
            Assert.AreEqual(YoudaoAppKey, ((YoudaoConfig)setting.Configs["Youdao"][0]).AppKey);
            Assert.AreEqual(YoudaoAppSecret, ((YoudaoConfig)setting.Configs["Youdao"][0]).AppSecret);
            Assert.AreEqual(MTranCredential, ((MTranServerConfig)setting.Configs["MTranServer"][0]).ApiKey);
            Assert.AreEqual(BaiduAppId, ((BaiduConfig)setting.Configs["Baidu"][0]).AppId);
            Assert.AreEqual(BaiduAppSecret, ((BaiduConfig)setting.Configs["Baidu"][0]).AppSecret);
            Assert.AreEqual(LibreCredential, ((LibreTranslateConfig)setting.Configs["LibreTranslate"][0]).ApiKey);
            Assert.AreEqual(SummaryCredential, setting.Summary.ApiKey);
        }

        private static void AssertNoCredentialFields(string path)
        {
            JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.AreEqual(0, SettingCredentialMap.CountLegacyFields(root));
            AssertFileDoesNotContain(path, AllCredentialValues);
        }

        private static void AssertFileDoesNotContain(string path, params string[] values)
        {
            byte[] bytes = File.ReadAllBytes(path);
            string utf8 = Encoding.UTF8.GetString(bytes);
            string utf16 = Encoding.Unicode.GetString(bytes);
            foreach (string value in values)
            {
                Assert.IsFalse(utf8.Contains(value, StringComparison.Ordinal));
                Assert.IsFalse(utf16.Contains(value, StringComparison.Ordinal));
            }
        }

        private static string CreateRoot()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "LectureCopilot.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void DeleteRoot(string root)
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }

        private sealed class ThrowingProtector : ICredentialProtector
        {
            public byte[] Protect(byte[] plaintext)
            {
                throw new CryptographicException("Simulated protection failure.");
            }

            public byte[] Unprotect(byte[] ciphertext)
            {
                throw new CryptographicException("Simulated unprotection failure.");
            }
        }

    }
}
