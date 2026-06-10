using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace cherrydev.Editor.GraphToolkit.Tests
{
    public sealed class DialogScriptCompilerTests
    {
        const string ScriptPath = "Assets/Dialogues/Test.ds.md";

        [Test]
        public void ValidateSourceReportsUnknownDirectivesWithLineNumbers()
        {
            IReadOnlyList<DialogScriptDiagnostic> diagnostics = DialogScriptCompiler.ValidateSource(
                "Alex:\n> Hello.\n@unknown value",
                ScriptPath);

            DialogScriptDiagnostic diagnostic = diagnostics.Single(item => item.Code == "DIALOG_SCRIPT_UNKNOWN_DIRECTIVE");

            Assert.IsTrue(diagnostic.IsError);
            Assert.AreEqual(ScriptPath, diagnostic.ScriptPath);
            Assert.AreEqual(3, diagnostic.LineNumber);
        }

        [Test]
        public void ValidateSourceReportsMissingPauseTarget()
        {
            IReadOnlyList<DialogScriptDiagnostic> diagnostics = DialogScriptCompiler.ValidateSource(
                "Alex:\n> Before.\n@pause after_delivery",
                ScriptPath);

            DialogScriptDiagnostic diagnostic = diagnostics.Single(item => item.Code == "DIALOG_SCRIPT_MISSING_PAUSE_TARGET");

            Assert.IsTrue(diagnostic.IsError);
            Assert.AreEqual(3, diagnostic.LineNumber);
        }

        [Test]
        public void ParserKeepsChoicesInsidePauseTargetSection()
        {
            const string source =
                "Alex:\n" +
                "> Before.\n" +
                "@pause after_delivery\n" +
                "\n" +
                "@section after_delivery\n" +
                "Alex:\n" +
                "> After.\n" +
                "@choice\n" +
                "- Good -> good\n" +
                "- Bad -> bad\n" +
                "\n" +
                "@section good\n" +
                "Alex:\n" +
                "> Good.\n" +
                "\n" +
                "@section bad\n" +
                "Alex:\n" +
                "> Bad.";

            DialogScriptParseResult parseResult = DialogScriptParser.Parse(source, ScriptPath);

            Assert.IsFalse(parseResult.Diagnostics.Any(diagnostic => diagnostic.IsError));
            Assert.AreEqual(1, parseResult.Document.Pauses.Count);
            Assert.AreEqual("after_delivery", parseResult.Document.Pauses[0].TargetSectionId);
            Assert.IsTrue(parseResult.Document.TryGetSection("after_delivery", out IReadOnlyList<DialogScriptStatement> statements));
            Assert.IsInstanceOf<DialogScriptChoiceStatement>(statements.Last());

            var choice = (DialogScriptChoiceStatement)statements.Last();
            Assert.AreEqual(2, choice.Choices.Count);
            Assert.AreEqual("good", choice.Choices[0].TargetSection);
            Assert.AreEqual("bad", choice.Choices[1].TargetSection);
        }

        [Test]
        public void ValidateSourceUsesExternalFunctionValidatorProviders()
        {
            IReadOnlyList<DialogScriptDiagnostic> diagnostics = DialogScriptCompiler.ValidateSource(
                "@effect test.invalid",
                ScriptPath);

            DialogScriptDiagnostic diagnostic = diagnostics.Single(item => item.Code == TestExternalFunctionValidator.Code);

            Assert.IsTrue(diagnostic.IsError);
            Assert.AreEqual(1, diagnostic.LineNumber);
        }
    }

    public sealed class TestExternalFunctionValidator : IDialogScriptExternalFunctionValidator
    {
        public const string Code = "TEST_EXTERNAL_FUNCTION_INVALID";

        public IEnumerable<DialogScriptDiagnostic> Validate(DialogScriptExternalFunctionValidationContext context)
        {
            if (context.FunctionName == "effect:test.invalid")
            {
                yield return DialogScriptDiagnostic.Error(
                    Code,
                    "Test validator rejected this external function.",
                    context.ScriptPath,
                    context.LineNumber);
            }
        }
    }
}
