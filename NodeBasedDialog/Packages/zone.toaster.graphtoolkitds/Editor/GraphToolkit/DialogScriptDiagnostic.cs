namespace cherrydev.Editor.GraphToolkit
{
    public sealed class DialogScriptDiagnostic
    {
        public DialogScriptDiagnostic(
            DialogScriptDiagnosticSeverity severity,
            string code,
            string message,
            string scriptPath,
            int lineNumber)
        {
            Severity = severity;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            ScriptPath = scriptPath ?? string.Empty;
            LineNumber = lineNumber;
        }

        public DialogScriptDiagnosticSeverity Severity { get; }
        public string Code { get; }
        public string Message { get; }
        public string ScriptPath { get; }
        public int LineNumber { get; }
        public bool IsError => Severity == DialogScriptDiagnosticSeverity.Error;

        public string FormatMessage()
        {
            string location = string.IsNullOrWhiteSpace(ScriptPath)
                ? string.Empty
                : LineNumber > 0
                    ? $"{ScriptPath}:{LineNumber}: "
                    : $"{ScriptPath}: ";

            return $"{location}{Code}: {Message}";
        }

        public static DialogScriptDiagnostic Warning(string code, string message, string scriptPath, int lineNumber)
        {
            return new DialogScriptDiagnostic(DialogScriptDiagnosticSeverity.Warning, code, message, scriptPath, lineNumber);
        }

        public static DialogScriptDiagnostic Error(string code, string message, string scriptPath, int lineNumber)
        {
            return new DialogScriptDiagnostic(DialogScriptDiagnosticSeverity.Error, code, message, scriptPath, lineNumber);
        }
    }
}
