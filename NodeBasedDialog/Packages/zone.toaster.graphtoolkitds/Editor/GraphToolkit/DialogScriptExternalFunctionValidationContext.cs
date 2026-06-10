namespace cherrydev.Editor.GraphToolkit
{
    public readonly struct DialogScriptExternalFunctionValidationContext
    {
        public DialogScriptExternalFunctionValidationContext(string scriptPath, int lineNumber, string functionName)
        {
            ScriptPath = scriptPath ?? string.Empty;
            LineNumber = lineNumber;
            FunctionName = functionName ?? string.Empty;
        }

        public string ScriptPath { get; }
        public int LineNumber { get; }
        public string FunctionName { get; }
    }
}
