using System.Collections.Generic;

namespace cherrydev.Editor.GraphToolkit
{
    public interface IDialogScriptExternalFunctionValidator
    {
        IEnumerable<DialogScriptDiagnostic> Validate(DialogScriptExternalFunctionValidationContext context);
    }
}
