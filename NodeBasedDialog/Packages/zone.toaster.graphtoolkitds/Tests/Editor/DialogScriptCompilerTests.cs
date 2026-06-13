using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace cherrydev.Editor.GraphToolkit.Tests
{
    public sealed class DialogScriptCompilerTests
    {
        const string ScriptPath = "Assets/Dialogues/Test.ds.md";
        const string GeneratedAssetTestRoot = "Assets/__DialogCompilerTests";

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

        [Test]
        public void ConditionExpressionEvaluatesAndOrNotAliases()
        {
            VariablesConfig config = CreateVariables(
                new Variable("psyche", VariableType.Int, false),
                new Variable("can_tolerate", VariableType.Bool, false));
            config.GetVariable("psyche").SetValue(45);
            config.GetVariable("can_tolerate").SetValue(false);

            Assert.IsTrue(DialogConditionExpression.TryParse(
                "psyche < 50 and not can_tolerate",
                out DialogConditionExpression expression,
                out string error), error);

            var handler = new DialogVariablesHandler(config);

            Assert.IsTrue(expression.Evaluate(handler));
            Assert.IsTrue(expression.Validate(config, out error), error);
        }

        [Test]
        public void DialogVariablesHandlerUsesRuntimeCopyOfVariables()
        {
            VariablesConfig config = CreateVariables(new Variable("can_tolerate", VariableType.Bool, false));
            config.GetVariable("can_tolerate").SetValue(true);

            var firstHandler = new DialogVariablesHandler(config);
            firstHandler.SetVariableValue("can_tolerate", false);

            var secondHandler = new DialogVariablesHandler(config);

            Assert.IsTrue(secondHandler.GetVariableValue<bool>("can_tolerate"));
            Assert.IsTrue(config.GetVariable("can_tolerate").GetBoolValue());
        }

        [Test]
        public void ParserReadsVariablesAndInlineConditionalBlocks()
        {
            const string source =
                "@var psyche:int = 100\n" +
                "@if psyche < 50\n" +
                "Alex:\n" +
                "> Low.\n" +
                "@else\n" +
                "Alex:\n" +
                "> Fine.\n" +
                "@endif";

            DialogScriptParseResult parseResult = DialogScriptParser.Parse(source, ScriptPath);

            Assert.IsFalse(parseResult.Diagnostics.Any(diagnostic => diagnostic.IsError));
            Assert.AreEqual(1, parseResult.Document.Variables.Count);
            Assert.IsInstanceOf<DialogScriptConditionalStatement>(parseResult.Document.MainStatements.Single());

            var conditional = (DialogScriptConditionalStatement)parseResult.Document.MainStatements.Single();

            Assert.AreEqual("psyche < 50", conditional.ConditionExpression);
            Assert.AreEqual(1, conditional.TrueStatements.OfType<DialogScriptSentenceStatement>().Count());
            Assert.AreEqual(1, conditional.FalseStatements.OfType<DialogScriptSentenceStatement>().Count());
        }

        [Test]
        public void ParserReadsConditionalChoiceGuards()
        {
            const string source =
                "@var can_tolerate:bool = true\n" +
                "@choice\n" +
                "- [if can_tolerate] Tolerate -> tolerate\n" +
                "- Snap -> snap\n" +
                "@section tolerate\n" +
                "> Tolerated.\n" +
                "@section snap\n" +
                "> Snapped.";

            DialogScriptParseResult parseResult = DialogScriptParser.Parse(source, ScriptPath);
            var choice = (DialogScriptChoiceStatement)parseResult.Document.MainStatements.Single();

            Assert.AreEqual("can_tolerate", choice.Choices[0].ConditionExpression);
            Assert.AreEqual("Tolerate", choice.Choices[0].Text);
            Assert.AreEqual(string.Empty, choice.Choices[1].ConditionExpression);
            Assert.IsFalse(DialogScriptCompiler.ValidateSource(source, ScriptPath).Any(diagnostic => diagnostic.IsError));
        }

        [Test]
        public void ParserReadsConditionalSectionJump()
        {
            const string source =
                "@var questStage:int = 2\n" +
                "@if questStage == 2 -> stage_2 else fallback\n" +
                "@section stage_2\n" +
                "> Stage 2.\n" +
                "@section fallback\n" +
                "> Fallback.";

            DialogScriptParseResult parseResult = DialogScriptParser.Parse(source, ScriptPath);
            var conditional = (DialogScriptConditionalStatement)parseResult.Document.MainStatements.Single();

            Assert.IsTrue(conditional.UsesSectionTargets);
            Assert.AreEqual("questStage == 2", conditional.ConditionExpression);
            Assert.AreEqual("stage_2", conditional.TrueTargetSection);
            Assert.AreEqual("fallback", conditional.FalseTargetSection);
            Assert.IsFalse(DialogScriptCompiler.ValidateSource(source, ScriptPath).Any(diagnostic => diagnostic.IsError));
        }

        [Test]
        public void ValidateSourceReportsUnknownConditionVariables()
        {
            IReadOnlyList<DialogScriptDiagnostic> diagnostics = DialogScriptCompiler.ValidateSource(
                "@var psyche:int = 100\n" +
                "@if missing_flag\n" +
                "> Hidden.\n" +
                "@endif",
                ScriptPath);

            DialogScriptDiagnostic diagnostic = diagnostics.Single(item => item.Code == "DIALOG_SCRIPT_INVALID_CONDITION");

            Assert.IsTrue(diagnostic.IsError);
            Assert.AreEqual(2, diagnostic.LineNumber);
        }

        [Test]
        public void CompileToAssetsDoesNotRewriteGeneratedAssetsWhenSourceIsUnchanged()
        {
            const string source =
                "Sergey:\n" +
                "> Before.\n" +
                "@effect review:+5\n" +
                "@pause after_delivery\n" +
                "\n" +
                "@section after_delivery\n" +
                "Sergey:\n" +
                "> After.";

            string scriptPath = WriteGeneratedAssetTestScript("Stable.ds.md", source);

            try
            {
                DialogScriptCompilationResult result = DialogScriptCompiler.CompileToAssets(scriptPath);
                AssetDatabase.SaveAssets();
                Dictionary<string, string> snapshots = CaptureGeneratedAssetSnapshots(result);

                DialogScriptCompilationResult secondResult = DialogScriptCompiler.CompileToAssets(scriptPath);
                AssetDatabase.SaveAssets();

                CollectionAssert.AreEquivalent(snapshots.Keys, CaptureGeneratedAssetSnapshots(secondResult).Keys);

                foreach (KeyValuePair<string, string> snapshot in snapshots)
                    Assert.AreEqual(snapshot.Value, File.ReadAllText(snapshot.Key), snapshot.Key);
            }
            finally
            {
                AssetDatabase.DeleteAsset(GeneratedAssetTestRoot);
            }
        }

        [Test]
        public void GraphAutoCompilerDoesNotRewriteRuntimeGraphOwnedByDialogScript()
        {
            const string source =
                "Sergey:\n" +
                "> Before.\n" +
                "@effect review:+5\n" +
                "@pause after_delivery\n" +
                "\n" +
                "@section after_delivery\n" +
                "Sergey:\n" +
                "> After.";

            string scriptPath = WriteGeneratedAssetTestScript("AutoCompileLoop.ds.md", source);

            try
            {
                DialogScriptCompilationResult result = DialogScriptCompiler.CompileToAssets(scriptPath);
                AssetDatabase.SaveAssets();
                Dictionary<string, string> snapshots = CaptureGeneratedAssetSnapshots(result);

                Assert.IsFalse(DialogGraphAutoCompiler.CompileGraphIfPresent(result.AuthoringGraphPath));
                AssetDatabase.SaveAssets();

                foreach (KeyValuePair<string, string> snapshot in snapshots)
                    Assert.AreEqual(snapshot.Value, File.ReadAllText(snapshot.Key), snapshot.Key);
            }
            finally
            {
                AssetDatabase.DeleteAsset(GeneratedAssetTestRoot);
            }
        }

        [Test]
        public void CompileToAssetsPreservesRuntimeSubAssetIdsForStableSourceKeys()
        {
            string scriptPath = WriteGeneratedAssetTestScript(
                "Reuse.ds.md",
                "Alex:\n" +
                "> First.\n" +
                "> Second.\n" +
                "@effect review:+5");

            try
            {
                DialogScriptCompilationResult result = DialogScriptCompiler.CompileToAssets(scriptPath);
                AssetDatabase.SaveAssets();
                Dictionary<string, long> beforeIds = CaptureRuntimeNodeLocalIds(result.RuntimeGraphPath);

                File.WriteAllText(
                    scriptPath,
                    "Alex:\n" +
                    "> First.\n" +
                    "> Second changed.\n" +
                    "@effect review:+5");
                AssetDatabase.ImportAsset(scriptPath);

                DialogScriptCompilationResult updatedResult = DialogScriptCompiler.CompileToAssets(scriptPath);
                AssetDatabase.SaveAssets();
                Dictionary<string, long> afterIds = CaptureRuntimeNodeLocalIds(updatedResult.RuntimeGraphPath);

                Assert.GreaterOrEqual(beforeIds.Count, 3);
                CollectionAssert.AreEquivalent(beforeIds.Keys, afterIds.Keys);

                foreach (KeyValuePair<string, long> beforeId in beforeIds)
                    Assert.AreEqual(beforeId.Value, afterIds[beforeId.Key], beforeId.Key);
            }
            finally
            {
                AssetDatabase.DeleteAsset(GeneratedAssetTestRoot);
            }
        }

        static VariablesConfig CreateVariables(params Variable[] variables)
        {
            VariablesConfig config = ScriptableObject.CreateInstance<VariablesConfig>();

            foreach (Variable variable in variables)
                config.AddVariable(variable);

            return config;
        }

        static string WriteGeneratedAssetTestScript(string fileName, string source)
        {
            if (!AssetDatabase.IsValidFolder(GeneratedAssetTestRoot))
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(GeneratedAssetTestRoot));

            string scriptPath = $"{GeneratedAssetTestRoot}/{fileName}";
            File.WriteAllText(scriptPath, source);
            AssetDatabase.ImportAsset(scriptPath);
            return scriptPath;
        }

        static Dictionary<string, string> CaptureGeneratedAssetSnapshots(DialogScriptCompilationResult result)
        {
            var paths = new List<string>
            {
                result.AuthoringGraphPath,
                result.RuntimeGraphPath
            };

            foreach (DialogScriptPauseCompilationResult continuation in result.PauseContinuations)
            {
                paths.Add(continuation.AuthoringGraphPath);
                paths.Add(continuation.RuntimeGraphPath);
            }

            return paths.ToDictionary(path => path, File.ReadAllText);
        }

        static Dictionary<string, long> CaptureRuntimeNodeLocalIds(string runtimeGraphPath)
        {
            DialogNodeGraph runtimeGraph = AssetDatabase.LoadAssetAtPath<DialogNodeGraph>(runtimeGraphPath);
            Assert.NotNull(runtimeGraph);

            return runtimeGraph.NodesList.ToDictionary(
                node => node.CompilerSourceKey,
                GetLocalFileId);
        }

        static long GetLocalFileId(Object asset)
        {
            Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out long localId));
            return localId;
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
