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

        [Test]
        public void ParserTrimsLinesAndIgnoresMarkdownNotes()
        {
            DialogScriptParseResult parseResult = DialogScriptParser.Parse(
                "  Alex:  \n" +
                "# comment\n" +
                "  > Hello.  \n" +
                " --- \n" +
                "* note\n" +
                "(stage note)\n" +
                "\n",
                ScriptPath);

            Assert.IsFalse(parseResult.Diagnostics.Any(diagnostic => diagnostic.IsError));

            var sentence = (DialogScriptSentenceStatement)parseResult.Document.MainStatements.Single();

            Assert.AreEqual("Alex", sentence.Speaker);
            Assert.AreEqual("Hello.", sentence.Text);
            Assert.AreEqual(3, sentence.LineNumber);
        }

        [Test]
        public void ParserSilentlySkipsPlainTextAndStrayChoiceOptions()
        {
            DialogScriptParseResult parseResult = DialogScriptParser.Parse(
                "Alex\n" +
                "Plain text without marker\n" +
                "- orphan -> section\n" +
                "> Spoken.",
                ScriptPath);

            Assert.IsFalse(parseResult.Diagnostics.Any(diagnostic => diagnostic.IsError));

            var sentence = (DialogScriptSentenceStatement)parseResult.Document.MainStatements.Single();

            Assert.AreEqual(string.Empty, sentence.Speaker);
            Assert.AreEqual("Spoken.", sentence.Text);
        }

        [Test]
        public void ParserResetsSpeakerWhenEnteringSection()
        {
            DialogScriptParseResult parseResult = DialogScriptParser.Parse(
                "Alex:\n" +
                "> Main.\n" +
                "@section after_delivery\n" +
                "> After.",
                ScriptPath);

            Assert.IsTrue(parseResult.Document.TryGetSection("after_delivery", out IReadOnlyList<DialogScriptStatement> statements));

            var sentence = (DialogScriptSentenceStatement)statements.Single();

            Assert.AreEqual(string.Empty, sentence.Speaker);
            Assert.AreEqual("After.", sentence.Text);
        }

        [Test]
        public void ParserAppendsDuplicateSectionsAndMatchesSectionIdsCaseSensitively()
        {
            DialogScriptParseResult parseResult = DialogScriptParser.Parse(
                "@section branch\n" +
                "Alex:\n" +
                "> One.\n" +
                "@section Branch\n" +
                "> Other.\n" +
                "@section branch\n" +
                "> Two.",
                ScriptPath);

            Assert.IsTrue(parseResult.Document.TryGetSection("branch", out IReadOnlyList<DialogScriptStatement> branchStatements));
            Assert.IsTrue(parseResult.Document.TryGetSection("Branch", out IReadOnlyList<DialogScriptStatement> capitalizedBranchStatements));

            Assert.AreEqual(2, branchStatements.Count);
            Assert.AreEqual(1, capitalizedBranchStatements.Count);
            Assert.AreEqual("One.", ((DialogScriptSentenceStatement)branchStatements[0]).Text);
            Assert.AreEqual("Two.", ((DialogScriptSentenceStatement)branchStatements[1]).Text);
        }

        [Test]
        public void ParserReadsChoiceOptionsThroughIgnoredLinesAndSplitsAtLastArrow()
        {
            DialogScriptParseResult parseResult = DialogScriptParser.Parse(
                "@choice\n" +
                "# comment\n" +
                "- Ask about A -> B -> branch\n" +
                "\n" +
                "@section branch\n" +
                "Alex:\n" +
                "> Branch.",
                ScriptPath);

            var choice = (DialogScriptChoiceStatement)parseResult.Document.MainStatements.Single();
            DialogScriptChoiceOption option = choice.Choices.Single();

            Assert.AreEqual("Ask about A -> B", option.Text);
            Assert.AreEqual("branch", option.TargetSection);
            Assert.AreEqual(3, option.LineNumber);
            Assert.IsTrue(parseResult.Document.TryGetSection("branch", out _));
        }

        [Test]
        public void ParserIgnoresChoicePayload()
        {
            DialogScriptParseResult parseResult = DialogScriptParser.Parse(
                "@choice ignored payload\n" +
                "- Continue -> branch\n" +
                "@section branch\n" +
                "> Branch.",
                ScriptPath);

            var choice = (DialogScriptChoiceStatement)parseResult.Document.MainStatements.Single();

            Assert.AreEqual(1, choice.Choices.Count);
            Assert.AreEqual("Continue", choice.Choices[0].Text);
            Assert.AreEqual("branch", choice.Choices[0].TargetSection);
        }

        [Test]
        public void ParserCreatesEffectAndFunctionExternalFunctionStatements()
        {
            DialogScriptParseResult parseResult = DialogScriptParser.Parse(
                "@effect customer.show_qr\n" +
                "@function SomeExternalFunction",
                ScriptPath);

            List<DialogScriptExternalFunctionStatement> externalFunctions = parseResult.Document.MainStatements
                .OfType<DialogScriptExternalFunctionStatement>()
                .ToList();

            Assert.AreEqual(2, externalFunctions.Count);
            Assert.AreEqual("effect:customer.show_qr", externalFunctions[0].FunctionName);
            Assert.AreEqual("SomeExternalFunction", externalFunctions[1].FunctionName);
        }

        [Test]
        public void ParserAcceptsDirectivePayloadAfterTab()
        {
            DialogScriptParseResult parseResult = DialogScriptParser.Parse(
                "@section\tbranch\n" +
                "@function\tSomeExternalFunction",
                ScriptPath);

            Assert.IsTrue(parseResult.Document.TryGetSection("branch", out IReadOnlyList<DialogScriptStatement> statements));

            var externalFunction = (DialogScriptExternalFunctionStatement)statements.Single();

            Assert.AreEqual("SomeExternalFunction", externalFunction.FunctionName);
        }

        [Test]
        public void ValidateSourceReportsMissingDirectivePayloads()
        {
            IReadOnlyList<DialogScriptDiagnostic> diagnostics = DialogScriptCompiler.ValidateSource(
                "@section\n" +
                "@effect\n" +
                "@function\n" +
                "@pause",
                ScriptPath);

            List<DialogScriptDiagnostic> missingPayloads = diagnostics
                .Where(diagnostic => diagnostic.Code == "DIALOG_SCRIPT_MISSING_DIRECTIVE_PAYLOAD")
                .ToList();

            Assert.AreEqual(4, missingPayloads.Count);
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, missingPayloads.Select(diagnostic => diagnostic.LineNumber));
        }

        [Test]
        public void ValidateSourceReportsMalformedChoiceOptions()
        {
            IReadOnlyList<DialogScriptDiagnostic> diagnostics = DialogScriptCompiler.ValidateSource(
                "@choice\n" +
                "- Missing arrow\n" +
                "- -> branch\n" +
                "- Text ->\n" +
                "@section branch\n" +
                "> Branch.",
                ScriptPath);

            List<DialogScriptDiagnostic> malformedChoices = diagnostics
                .Where(diagnostic => diagnostic.Code == "DIALOG_SCRIPT_MALFORMED_CHOICE")
                .ToList();

            Assert.AreEqual(3, malformedChoices.Count);
            CollectionAssert.AreEqual(new[] { 2, 3, 4 }, malformedChoices.Select(diagnostic => diagnostic.LineNumber));
        }

        [Test]
        public void ValidateSourceReportsMissingChoiceTarget()
        {
            IReadOnlyList<DialogScriptDiagnostic> diagnostics = DialogScriptCompiler.ValidateSource(
                "@choice\n" +
                "- Leave -> missing_section",
                ScriptPath);

            DialogScriptDiagnostic diagnostic = diagnostics.Single(item => item.Code == "DIALOG_SCRIPT_MISSING_CHOICE_TARGET");

            Assert.IsTrue(diagnostic.IsError);
            Assert.AreEqual(2, diagnostic.LineNumber);
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
