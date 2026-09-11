using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class AppPathSetTests
    {
        [TestMethod]
        public void PathsStayInsideExplicitRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "LectureCopilot.Tests", Guid.NewGuid().ToString("N"));
            var paths = new AppPathSet(root);

            StringAssert.StartsWith(paths.SettingsFile, paths.RootDirectory);
            StringAssert.StartsWith(paths.CredentialsFile, paths.RootDirectory);
            StringAssert.StartsWith(paths.HistoryDatabaseFile, paths.RootDirectory);
            Assert.AreNotEqual(Directory.GetCurrentDirectory(), paths.RootDirectory);
        }

        [TestMethod]
        public void EnsureDirectoriesCreatesExpectedLayout()
        {
            string root = Path.Combine(Path.GetTempPath(), "LectureCopilot.Tests", Guid.NewGuid().ToString("N"));
            var paths = new AppPathSet(root);

            try
            {
                paths.EnsureDirectories();

                Assert.IsTrue(Directory.Exists(paths.DataDirectory));
                Assert.IsTrue(Directory.Exists(paths.LogsDirectory));
                Assert.IsTrue(Directory.Exists(paths.BackupsDirectory));
                Assert.IsTrue(Directory.Exists(paths.RecoveryDirectory));
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        public void DataRootEnvironmentVariableEnablesIsolatedRuns()
        {
            string? original = Environment.GetEnvironmentVariable(
                AppPaths.DATA_ROOT_ENVIRONMENT_VARIABLE);
            string isolatedRoot = Path.Combine(
                Path.GetTempPath(),
                "LectureCopilot.Tests",
                Guid.NewGuid().ToString("N"));

            try
            {
                Environment.SetEnvironmentVariable(
                    AppPaths.DATA_ROOT_ENVIRONMENT_VARIABLE,
                    isolatedRoot);

                Assert.AreEqual(
                    Path.GetFullPath(isolatedRoot),
                    AppPaths.ResolveRootDirectory());
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    AppPaths.DATA_ROOT_ENVIRONMENT_VARIABLE,
                    original);
            }
        }
    }
}
