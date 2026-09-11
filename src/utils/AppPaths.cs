using System.IO;

namespace LiveCaptionsTranslator.utils
{
    public sealed class AppPathSet
    {
        public string RootDirectory { get; }
        public string DataDirectory => Path.Combine(RootDirectory, "Data");
        public string LogsDirectory => Path.Combine(RootDirectory, "Logs");
        public string BackupsDirectory => Path.Combine(RootDirectory, "Backups");
        public string RecoveryDirectory => Path.Combine(RootDirectory, "Recovery");
        public string SettingsFile => Path.Combine(DataDirectory, "setting.json");
        public string CredentialsFile => Path.Combine(DataDirectory, "credentials.dat");
        public string HistoryDatabaseFile => Path.Combine(DataDirectory, "translation_history.db");

        public AppPathSet(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
                throw new ArgumentException("Application data root cannot be empty.", nameof(rootDirectory));

            RootDirectory = Path.GetFullPath(rootDirectory);
        }

        public void EnsureDirectories()
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(LogsDirectory);
            Directory.CreateDirectory(BackupsDirectory);
            Directory.CreateDirectory(RecoveryDirectory);
        }
    }

    public static class AppPaths
    {
        public const string PRODUCT_DIRECTORY_NAME = "LectureCopilot.Dev";
        public const string DATA_ROOT_ENVIRONMENT_VARIABLE = "LECTURE_COPILOT_DATA_ROOT";

        public static AppPathSet Current { get; } = new(ResolveRootDirectory());

        public static void EnsureCreated()
        {
            Current.EnsureDirectories();
        }

        internal static string ResolveRootDirectory()
        {
            string? isolatedRoot = Environment.GetEnvironmentVariable(
                DATA_ROOT_ENVIRONMENT_VARIABLE);
            return string.IsNullOrWhiteSpace(isolatedRoot)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    PRODUCT_DIRECTORY_NAME)
                : isolatedRoot;
        }
    }
}
