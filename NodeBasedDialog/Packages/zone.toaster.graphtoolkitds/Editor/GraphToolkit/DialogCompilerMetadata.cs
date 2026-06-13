using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace cherrydev.Editor.GraphToolkit
{
    internal static class DialogCompilerMetadata
    {
        public const int SchemaVersion = 2;
        public const string DialogScriptInputKind = "dialog-script";
        public const string AuthoringGraphInputKind = "authoring-graph";

        public static DialogCompilerInputMetadata ForDialogScriptSource(string source) =>
            new(DialogScriptInputKind, ComputeTextHash(source ?? string.Empty));

        public static DialogCompilerInputMetadata ForAuthoringGraph(string graphPath) =>
            new(AuthoringGraphInputKind, ComputeFileHash(graphPath));

        public static string ComputeFileHash(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return string.Empty;

            using SHA256 sha256 = SHA256.Create();
            return ToHexString(sha256.ComputeHash(File.ReadAllBytes(path)));
        }

        private static string ComputeTextHash(string text)
        {
            using SHA256 sha256 = SHA256.Create();
            return ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        private static string ToHexString(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);

            foreach (byte value in bytes)
                builder.Append(value.ToString("x2"));

            return builder.ToString();
        }
    }

    internal readonly struct DialogCompilerInputMetadata
    {
        public DialogCompilerInputMetadata(string inputKind, string inputHash)
        {
            InputKind = inputKind ?? string.Empty;
            InputHash = inputHash ?? string.Empty;
        }

        public string InputKind { get; }
        public string InputHash { get; }
        public bool HasValue => !string.IsNullOrWhiteSpace(InputKind) && !string.IsNullOrWhiteSpace(InputHash);
    }
}
