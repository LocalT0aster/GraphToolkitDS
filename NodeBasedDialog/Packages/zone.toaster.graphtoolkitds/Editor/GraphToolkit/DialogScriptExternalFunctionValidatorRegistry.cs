using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace cherrydev.Editor.GraphToolkit
{
    internal static class DialogScriptExternalFunctionValidatorRegistry
    {
        public static IReadOnlyList<DialogScriptDiagnostic> Validate(DialogScriptExternalFunctionValidationContext context)
        {
            var diagnostics = new List<DialogScriptDiagnostic>();

            foreach (Type validatorType in TypeCache.GetTypesDerivedFrom<IDialogScriptExternalFunctionValidator>())
            {
                if (validatorType.IsAbstract || validatorType.IsInterface || validatorType.GetConstructor(Type.EmptyTypes) == null)
                    continue;

                IDialogScriptExternalFunctionValidator validator;

                try
                {
                    validator = (IDialogScriptExternalFunctionValidator)Activator.CreateInstance(validatorType);
                }
                catch (Exception exception)
                {
                    diagnostics.Add(DialogScriptDiagnostic.Warning(
                        "DIALOG_SCRIPT_VALIDATOR_CREATE_FAILED",
                        $"Could not create external function validator '{validatorType.FullName}': {exception.Message}",
                        context.ScriptPath,
                        context.LineNumber));
                    continue;
                }

                try
                {
                    IEnumerable<DialogScriptDiagnostic> validatorDiagnostics = validator.Validate(context);

                    if (validatorDiagnostics != null)
                        diagnostics.AddRange(validatorDiagnostics.Where(diagnostic => diagnostic != null));
                }
                catch (Exception exception)
                {
                    diagnostics.Add(DialogScriptDiagnostic.Warning(
                        "DIALOG_SCRIPT_VALIDATOR_FAILED",
                        $"External function validator '{validatorType.FullName}' failed: {exception.Message}",
                        context.ScriptPath,
                        context.LineNumber));
                }
            }

            return diagnostics;
        }
    }
}
