using System.IO;

namespace LiveCaptionsTranslator.services.recognition
{
    internal sealed record LocalAsrModelFiles(
        string ModelDirectory,
        string Encoder,
        string Decoder,
        string Joiner,
        string Tokens);

    internal sealed class LocalAsrModelValidationException : Exception
    {
        public LocalAsrModelValidationException(
            string modelDirectory,
            IReadOnlyList<string> invalidFiles)
            : base("The local ASR model is incomplete or unreadable.")
        {
            ModelDirectory = modelDirectory;
            InvalidFiles = invalidFiles;
        }

        public string ModelDirectory { get; }
        public IReadOnlyList<string> InvalidFiles { get; }
    }

    internal static class LocalAsrModelLocator
    {
        public const string DefaultModelDirectoryName =
            "sherpa-onnx-streaming-zipformer-en-20M-2023-02-17";
        public const string EncoderFileName =
            "encoder-epoch-99-avg-1.int8.onnx";
        public const string DecoderFileName =
            "decoder-epoch-99-avg-1.onnx";
        public const string JoinerFileName =
            "joiner-epoch-99-avg-1.int8.onnx";
        public const string TokensFileName = "tokens.txt";

        private static readonly string[] RequiredFileNames =
        [
            EncoderFileName,
            DecoderFileName,
            JoinerFileName,
            TokensFileName
        ];

        public static LocalAsrModelFiles Resolve(string modelRoot)
        {
            if (string.IsNullOrWhiteSpace(modelRoot))
                throw new ArgumentException("Model root is required.", nameof(modelRoot));

            string root = Path.GetFullPath(modelRoot.Trim());
            string leaf = Path.GetFileName(
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            bool isDirectModelDirectory = string.Equals(
                    leaf,
                    DefaultModelDirectoryName,
                    StringComparison.OrdinalIgnoreCase) ||
                RequiredFileNames.Any(fileName => File.Exists(Path.Combine(root, fileName)));
            string modelDirectory = isDirectModelDirectory
                ? root
                : Path.Combine(root, DefaultModelDirectoryName);

            var invalidFiles = new List<string>();
            foreach (string fileName in RequiredFileNames)
            {
                string path = Path.Combine(modelDirectory, fileName);
                try
                {
                    if (!File.Exists(path) || new FileInfo(path).Length == 0)
                        invalidFiles.Add(fileName);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    invalidFiles.Add(fileName);
                }
            }

            if (invalidFiles.Count > 0)
            {
                throw new LocalAsrModelValidationException(
                    modelDirectory,
                    invalidFiles.AsReadOnly());
            }

            return new LocalAsrModelFiles(
                modelDirectory,
                Path.Combine(modelDirectory, EncoderFileName),
                Path.Combine(modelDirectory, DecoderFileName),
                Path.Combine(modelDirectory, JoinerFileName),
                Path.Combine(modelDirectory, TokensFileName));
        }
    }
}
