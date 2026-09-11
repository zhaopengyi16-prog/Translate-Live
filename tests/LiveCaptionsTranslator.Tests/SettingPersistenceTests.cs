using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class SettingPersistenceTests
    {
        private static readonly ICredentialProtector TestProtector = new TestCredentialProtector();

        [TestMethod]
        public void SaveCanAtomicallyReplaceExistingFile()
        {
            string root = Path.Combine(Path.GetTempPath(), "LectureCopilot.Tests", Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "setting.json");

            try
            {
                var setting = new Setting();
                setting.Save(path, TestProtector);
                setting.Save(path);

                Assert.IsTrue(File.Exists(path));
                Assert.IsTrue(File.Exists(path + ".bak"));
                Assert.AreEqual(setting.ApiName, Setting.Load(path, TestProtector).ApiName);
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public void SaveAndLoadPreservesSelectedApiAndModelDisplayName()
        {
            string root = Path.Combine(Path.GetTempPath(), "LectureCopilot.Tests", Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "setting.json");

            try
            {
                var setting = new Setting
                {
                    ApiName = "OpenAI",
                    TargetLanguage = "zh-CN"
                };
                var config = (OpenAIConfig)setting.Configs["OpenAI"][0];
                config.ModelName = "deepseek-v4-flash";
                config.ApiUrl = "https://api.deepseek.com/chat/completions";
                setting.Summary.ModelName = "deepseek-v4-pro";
                setting.Summary.ApiUrl = "https://api.deepseek.com/chat/completions";
                setting.Appearance.UiFontFamily = "Segoe UI";
                setting.Appearance.SubtitleFontSize = 24;
                setting.Appearance.ReduceMotion = true;
                setting.Save(path, TestProtector);

                var loaded = Setting.Load(path, TestProtector);

                Assert.AreEqual("OpenAI", loaded.ApiName);
                Assert.AreEqual("zh-CN", loaded.TargetLanguage);
                Assert.AreEqual("OpenAI / deepseek-v4-flash", loaded.ActiveEngineDisplayName);
                Assert.AreEqual("OpenAI-compatible / deepseek-v4-pro", loaded.SummaryEngineDisplayName);
                Assert.AreNotSame(loaded.Configs["OpenAI"][0], loaded.Summary);
                Assert.AreEqual("Segoe UI", loaded.Appearance.UiFontFamily);
                Assert.AreEqual(24, loaded.Appearance.SubtitleFontSize);
                Assert.IsTrue(loaded.Appearance.ReduceMotion);
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public void LoadUpgradesOnlyTheLegacyDefaultTranslationPrompt()
        {
            string root = Path.Combine(Path.GetTempPath(), "LectureCopilot.Tests", Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "setting.json");

            try
            {
                var setting = new Setting
                {
                    Prompt = Setting.LegacyDefaultTranslationPrompt
                };
                setting.Save(path, TestProtector);

                var loaded = Setting.Load(path, TestProtector);

                Assert.AreEqual(Setting.DefaultTranslationPrompt, loaded.Prompt);
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public void LoadPreservesACustomTranslationPrompt()
        {
            string root = Path.Combine(Path.GetTempPath(), "LectureCopilot.Tests", Guid.NewGuid().ToString("N"));
            string path = Path.Combine(root, "setting.json");
            const string customPrompt = "Translate the marked text to {0} using the team's glossary.";

            try
            {
                var setting = new Setting { Prompt = customPrompt };
                setting.Save(path, TestProtector);

                var loaded = Setting.Load(path, TestProtector);

                Assert.AreEqual(customPrompt, loaded.Prompt);
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }
    }
}
