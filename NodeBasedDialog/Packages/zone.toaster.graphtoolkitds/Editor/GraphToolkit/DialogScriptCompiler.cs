using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEngine;
using GtkNode = Unity.GraphToolkit.Editor.Node;

namespace cherrydev.Editor.GraphToolkit
{
    internal static class DialogScriptCompiler
    {
        public const string SourceExtension = "ds.md";

        private const string CompileMenuPath = "Tools/Dialog System/Compile Selected Dialog Scripts";
        private const string DialoguesRoot = "Assets/Dialogues/";
        private const string AuthoringGraphsDirectoryName = "AuthoringGraphs";
        private const float NodeSpacingX = 260f;
        private const float NodeSpacingY = 140f;

        [MenuItem(CompileMenuPath)]
        private static void CompileSelectedScripts()
        {
            List<string> scriptPaths = GetSelectedScriptPaths().ToList();

            if (scriptPaths.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "Compile Dialog Scripts",
                    "Select one or more .ds.md assets in the Project window.",
                    "OK");
                return;
            }

            List<string> compiledPaths = new();

            foreach (string scriptPath in scriptPaths)
            {
                try
                {
                    DialogScriptCompilationResult result = CompileToAssets(scriptPath);
                    compiledPaths.Add(result.RuntimeGraphPath);

                    foreach (DialogScriptPauseCompilationResult continuation in result.PauseContinuations)
                        compiledPaths.Add(continuation.RuntimeGraphPath);
                }
                catch (Exception exception)
                {
                    Debug.LogError($"Failed to compile dialog script '{scriptPath}': {exception.Message}");
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog(
                "Compile Dialog Scripts",
                compiledPaths.Count == 0
                    ? "No dialog scripts were compiled. Check the Console for errors."
                    : $"Compiled {compiledPaths.Count} dialog script(s).",
                "OK");
        }

        [MenuItem(CompileMenuPath, true)]
        private static bool CanCompileSelectedScripts() => GetSelectedScriptPaths().Any();

        public static DialogScriptCompilationResult CompileToAssets(string scriptPath)
        {
            if (!IsProjectDialogScript(scriptPath))
                throw new ArgumentException($"Dialog script path must be an Assets/ path ending with .{SourceExtension}.", nameof(scriptPath));

            if (!File.Exists(scriptPath))
                throw new FileNotFoundException($"Dialog script was not found at '{scriptPath}'.", scriptPath);

            string source = File.ReadAllText(scriptPath);
            DialogScriptParseResult parseResult = DialogScriptParser.Parse(source, scriptPath);
            List<DialogScriptDiagnostic> diagnostics = new(parseResult.Diagnostics);
            diagnostics.AddRange(DialogScriptValidator.Validate(parseResult.Document, scriptPath));
            LogDiagnostics(diagnostics);

            int errorCount = diagnostics.Count(diagnostic => diagnostic.IsError);
            if (errorCount > 0)
                throw new InvalidOperationException($"Dialog script has {errorCount} validation error(s).");

            DialogScriptDocument document = parseResult.Document;
            string graphPath = GetAuthoringGraphPath(scriptPath);

            DialogNodeGraph runtimeGraph = CompileGraph(scriptPath, graphPath, document, document.MainStatements, "main");
            string runtimePath = AssetDatabase.GetAssetPath(runtimeGraph);
            List<DialogScriptPauseCompilationResult> pauseContinuations = CompilePauseContinuations(scriptPath, document);

            if (pauseContinuations.Count > 0)
            {
                DialogScriptPauseCompilationResult firstContinuation = pauseContinuations[0];
                runtimeGraph.ConfigurePauseContinuation(
                    firstContinuation.SectionId,
                    AssetDatabase.AssetPathToGUID(firstContinuation.RuntimeGraphPath));
                EditorUtility.SetDirty(runtimeGraph);
                AssetDatabase.SaveAssets();
            }

            Debug.Log($"Compiled dialog script '{scriptPath}' to '{graphPath}' and '{runtimePath}'.");
            return new DialogScriptCompilationResult(scriptPath, graphPath, runtimePath, pauseContinuations);
        }

        internal static IReadOnlyList<DialogScriptDiagnostic> ValidateSource(string source, string scriptPath = "")
        {
            DialogScriptParseResult parseResult = DialogScriptParser.Parse(source, scriptPath);
            var diagnostics = new List<DialogScriptDiagnostic>(parseResult.Diagnostics);
            diagnostics.AddRange(DialogScriptValidator.Validate(parseResult.Document, scriptPath));
            return diagnostics;
        }

        public static bool IsProjectDialogScript(string assetPath) =>
            !string.IsNullOrEmpty(assetPath) &&
            assetPath.StartsWith("Assets/", StringComparison.Ordinal) &&
            assetPath.EndsWith($".{SourceExtension}", StringComparison.OrdinalIgnoreCase);

        private static IEnumerable<string> GetSelectedScriptPaths()
        {
            foreach (string guid in Selection.assetGUIDs)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);

                if (IsProjectDialogScript(path))
                    yield return path;
            }
        }

        private static string GetAuthoringGraphPath(string scriptPath)
        {
            return GetAuthoringGraphPath(scriptPath, string.Empty);
        }

        private static string GetAuthoringGraphPath(string scriptPath, string sectionId)
        {
            string directory = Path.GetDirectoryName(scriptPath)?.Replace("\\", "/");
            string fileName = Path.GetFileName(scriptPath);
            string graphName = fileName.EndsWith($".{SourceExtension}", StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - SourceExtension.Length - 1)
                : Path.GetFileNameWithoutExtension(scriptPath);

            if (!string.IsNullOrWhiteSpace(sectionId))
                graphName = $"{graphName}__{SanitizeAssetNameFragment(sectionId)}";

            string graphDirectory = GetAuthoringGraphDirectory(directory);
            EnsureAssetDirectory(graphDirectory);
            return $"{graphDirectory}/{graphName}.{DialogAuthoringGraph.AssetExtension}";
        }

        private static DialogNodeGraph CompileGraph(
            string scriptPath,
            string graphPath,
            DialogScriptDocument document,
            IReadOnlyList<DialogScriptStatement> entryStatements,
            string entryLabel)
        {
            if (AssetDatabase.LoadMainAssetAtPath(graphPath) != null)
                AssetDatabase.DeleteAsset(graphPath);

            DialogAuthoringGraph authoringGraph = GraphDatabase.CreateGraph<DialogAuthoringGraph>(graphPath);
            PopulateAuthoringGraph(authoringGraph, document, entryStatements, entryLabel);
            GraphDatabase.SaveGraphIfDirty(authoringGraph);
            AssetDatabase.ImportAsset(graphPath);

            DialogNodeGraph runtimeGraph = DialogGraphCompiler.CompileToRuntimeAsset(graphPath);
            Debug.Log($"Compiled dialog script '{scriptPath}' section '{entryLabel}' to '{graphPath}'.");
            return runtimeGraph;
        }

        private static List<DialogScriptPauseCompilationResult> CompilePauseContinuations(
            string scriptPath,
            DialogScriptDocument document)
        {
            var continuations = new List<DialogScriptPauseCompilationResult>();
            var compiledSectionIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (DialogScriptPauseStatement pause in document.Pauses)
            {
                if (!compiledSectionIds.Add(pause.TargetSectionId))
                    continue;

                if (!document.TryGetSection(pause.TargetSectionId, out IReadOnlyList<DialogScriptStatement> statements))
                    continue;

                string continuationGraphPath = GetAuthoringGraphPath(scriptPath, pause.TargetSectionId);
                DialogNodeGraph continuationRuntimeGraph = CompileGraph(
                    scriptPath,
                    continuationGraphPath,
                    document,
                    statements,
                    pause.TargetSectionId);

                continuations.Add(new DialogScriptPauseCompilationResult(
                    pause.TargetSectionId,
                    continuationGraphPath,
                    AssetDatabase.GetAssetPath(continuationRuntimeGraph)));
            }

            return continuations;
        }

        private static void LogDiagnostics(IEnumerable<DialogScriptDiagnostic> diagnostics)
        {
            foreach (DialogScriptDiagnostic diagnostic in diagnostics)
            {
                if (diagnostic.IsError)
                    Debug.LogError(diagnostic.FormatMessage());
                else
                    Debug.LogWarning(diagnostic.FormatMessage());
            }
        }

        private static string SanitizeAssetNameFragment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "section";

            char[] chars = value.Trim().ToCharArray();

            for (int i = 0; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_' && chars[i] != '-')
                    chars[i] = '_';
            }

            return new string(chars);
        }

        private static string GetAuthoringGraphDirectory(string sourceDirectory)
        {
            if (IsDialogueDaySourceDirectory(sourceDirectory))
                return $"{sourceDirectory}/{AuthoringGraphsDirectoryName}";

            return sourceDirectory;
        }

        private static bool IsDialogueDaySourceDirectory(string sourceDirectory)
        {
            if (string.IsNullOrEmpty(sourceDirectory) || !sourceDirectory.StartsWith(DialoguesRoot, StringComparison.Ordinal))
                return false;

            string relativePath = sourceDirectory.Substring(DialoguesRoot.Length).Trim('/');
            return !string.IsNullOrEmpty(relativePath) && !relativePath.Contains("/");
        }

        private static void EnsureAssetDirectory(string assetDirectory)
        {
            if (string.IsNullOrEmpty(assetDirectory) || AssetDatabase.IsValidFolder(assetDirectory))
                return;

            string[] parts = assetDirectory.Split('/');
            string current = parts[0];

            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";

                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);

                current = next;
            }
        }

        private static void PopulateAuthoringGraph(
            DialogAuthoringGraph authoringGraph,
            DialogScriptDocument document,
            IReadOnlyList<DialogScriptStatement> entryStatements,
            string entryLabel)
        {
            object graphModel = GetGraphModel(authoringGraph);

            if (graphModel == null)
                throw new InvalidOperationException("Could not access Graph Toolkit graph model.");

            AuthoringNodeBinding startNode = CreateAuthoringNode(graphModel, new DialogStartNode(), new Vector2(-NodeSpacingX, 0f));
            DialogScriptGraphBuilder builder = new(graphModel, document);
            BuildSequenceResult main = builder.Build(entryStatements, Vector2.zero, entryLabel);

            if (main.First != null)
                CreateWire(graphModel, startNode.Node, DialogGraphPorts.Next, main.First, DialogGraphPorts.Input);
        }

        private static object GetGraphModel(DialogAuthoringGraph graph)
        {
            FieldInfo implementationField = typeof(Graph).GetField("m_Implementation", BindingFlags.Instance | BindingFlags.NonPublic);
            return implementationField?.GetValue(graph);
        }

        private static AuthoringNodeBinding CreateAuthoringNode(object graphModel, GtkNode node, Vector2 position)
        {
            MethodInfo createNodeModel = graphModel.GetType().GetMethod("CreateNodeModel", BindingFlags.Instance | BindingFlags.Public);
            object nodeModel = createNodeModel?.Invoke(graphModel, new object[] { node, position });

            if (nodeModel == null)
                throw new InvalidOperationException($"Could not create authoring node model for {node.GetType().Name}.");

            return new AuthoringNodeBinding(node, nodeModel);
        }

        private static void SetOption(GtkNode node, string optionName, object value)
        {
            INodeOption option = node.GetNodeOptionByName(optionName);

            if (option == null)
                return;

            PropertyInfo portModelProperty = option.GetType().GetProperty("PortModel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object portModel = portModelProperty?.GetValue(option);
            PropertyInfo embeddedValueProperty = portModel?.GetType().GetProperty("EmbeddedValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object embeddedValue = embeddedValueProperty?.GetValue(portModel);
            PropertyInfo objectValueProperty = embeddedValue?.GetType().GetProperty("ObjectValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            objectValueProperty?.SetValue(embeddedValue, value);
        }

        private static void RedefineNode(object nodeModel)
        {
            MethodInfo defineNode = nodeModel.GetType().GetMethod("DefineNode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            defineNode?.Invoke(nodeModel, null);
        }

        private static void CreateWire(
            object graphModel,
            GtkNode sourceNode,
            string sourcePortName,
            GtkNode targetNode,
            string targetPortName)
        {
            IPort outputPort = sourceNode.GetOutputPortByName(sourcePortName);
            IPort inputPort = targetNode.GetInputPortByName(targetPortName);

            if (outputPort == null || inputPort == null)
                return;

            MethodInfo createWire = graphModel.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(method => method.Name == "CreateWire" && method.GetParameters().Length == 3);

            if (createWire == null)
                throw new InvalidOperationException("Graph Toolkit CreateWire method was not found.");

            createWire.Invoke(graphModel, new object[] { inputPort, outputPort, new Hash128() });
        }

        private sealed class DialogScriptGraphBuilder
        {
            private readonly object graphModel;
            private readonly DialogScriptDocument document;
            private readonly Dictionary<string, BuildSequenceResult> builtSections = new(StringComparer.Ordinal);
            private readonly HashSet<string> activeSections = new(StringComparer.Ordinal);

            public DialogScriptGraphBuilder(object graphModel, DialogScriptDocument document)
            {
                this.graphModel = graphModel;
                this.document = document;
            }

            public BuildSequenceResult Build(IReadOnlyList<DialogScriptStatement> statements, Vector2 origin, string label)
            {
                GtkNode first = null;
                GtkNode previous = null;

                for (int index = 0; index < statements.Count; index++)
                {
                    DialogScriptStatement statement = statements[index];

                    if (statement is DialogScriptPauseStatement)
                        break;

                    Vector2 position = origin + new Vector2(index * NodeSpacingX, 0f);
                    GtkNode current = CreateNode(statement, position, label, index);

                    if (current == null)
                        continue;

                    first ??= current;

                    if (previous != null)
                        CreateWire(graphModel, previous, DialogGraphPorts.Next, current, DialogGraphPorts.Input);

                    if (statement is DialogScriptChoiceStatement && HasStatementBeforePause(statements, index + 1))
                        throw new InvalidOperationException($"Choice in '{label}' must be the last statement in its sequence.");

                    previous = statement is DialogScriptChoiceStatement ? null : current;
                }

                return new BuildSequenceResult(first, previous);
            }

            private static bool HasStatementBeforePause(IReadOnlyList<DialogScriptStatement> statements, int startIndex)
            {
                for (int index = startIndex; index < statements.Count; index++)
                {
                    if (statements[index] is DialogScriptPauseStatement)
                        return false;

                    return true;
                }

                return false;
            }

            private GtkNode CreateNode(DialogScriptStatement statement, Vector2 position, string label, int index)
            {
                switch (statement)
                {
                    case DialogScriptSentenceStatement sentence:
                        return CreateSentenceNode(sentence, position);
                    case DialogScriptExternalFunctionStatement externalFunction:
                        return CreateExternalFunctionNode(externalFunction, position);
                    case DialogScriptChoiceStatement choice:
                        return CreateChoiceNode(choice, position, label, index);
                    default:
                        return null;
                }
            }

            private GtkNode CreateSentenceNode(DialogScriptSentenceStatement sentence, Vector2 position)
            {
                AuthoringNodeBinding binding = CreateAuthoringNode(graphModel, new DialogSentenceNode(), position);
                SetOption(binding.Node, DialogGraphOptions.CharacterName, sentence.Speaker);
                SetOption(binding.Node, DialogGraphOptions.SentenceText, sentence.Text);
                RedefineNode(binding.Model);
                return binding.Node;
            }

            private GtkNode CreateExternalFunctionNode(DialogScriptExternalFunctionStatement externalFunction, Vector2 position)
            {
                AuthoringNodeBinding binding = CreateAuthoringNode(graphModel, new DialogExternalFunctionNode(), position);
                SetOption(binding.Node, DialogGraphOptions.FunctionName, externalFunction.FunctionName);
                SetOption(binding.Node, DialogGraphOptions.FunctionDescription, externalFunction.Description);
                RedefineNode(binding.Model);
                return binding.Node;
            }

            private GtkNode CreateChoiceNode(DialogScriptChoiceStatement choice, Vector2 position, string label, int index)
            {
                if (choice.Choices.Count == 0)
                    throw new InvalidOperationException($"Choice in '{label}' has no answer options.");

                AuthoringNodeBinding binding = CreateAuthoringNode(graphModel, new DialogAnswerNode(), position);
                int answerCount = Mathf.Clamp(choice.Choices.Count, 1, DialogGraphPorts.MaxAnswerPorts);
                SetOption(binding.Node, DialogGraphOptions.AnswerCount, answerCount);
                RedefineNode(binding.Model);

                for (int choiceIndex = 0; choiceIndex < answerCount; choiceIndex++)
                {
                    DialogScriptChoiceOption option = choice.Choices[choiceIndex];
                    SetOption(binding.Node, DialogGraphOptions.AnswerTextPrefix + choiceIndex, option.Text);
                    SetOption(binding.Node, DialogGraphOptions.AnswerKeyPrefix + choiceIndex, option.TargetSection);

                    BuildSequenceResult branch = BuildSection(
                        option.TargetSection,
                        position + new Vector2(NodeSpacingX, (choiceIndex - (answerCount - 1) * 0.5f) * NodeSpacingY),
                        $"{label}.choice_{index + 1}.{choiceIndex + 1}");

                    if (branch.First != null)
                        CreateWire(graphModel, binding.Node, DialogGraphPorts.Answer(choiceIndex), branch.First, DialogGraphPorts.Input);
                }

                return binding.Node;
            }

            private BuildSequenceResult BuildSection(string sectionId, Vector2 origin, string label)
            {
                if (builtSections.TryGetValue(sectionId, out BuildSequenceResult built))
                    return built;

                if (!document.TryGetSection(sectionId, out IReadOnlyList<DialogScriptStatement> statements))
                    throw new InvalidOperationException($"Choice references missing section '{sectionId}'.");

                if (!activeSections.Add(sectionId))
                    throw new InvalidOperationException($"Dialog script section cycle detected at '{sectionId}'.");

                BuildSequenceResult result = Build(statements, origin, label);
                activeSections.Remove(sectionId);
                builtSections[sectionId] = result;
                return result;
            }
        }
    }

    internal sealed class DialogScriptAutoCompiler : AssetPostprocessor
    {
        private static readonly HashSet<string> PendingScriptPaths = new(StringComparer.Ordinal);
        private static bool isCompileScheduled;
        private static bool isCompiling;

        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (isCompiling)
                return;

            QueueScriptPaths(importedAssets);
            QueueScriptPaths(movedAssets);
            ScheduleCompile();
        }

        private static void QueueScriptPaths(IEnumerable<string> assetPaths)
        {
            foreach (string assetPath in assetPaths)
            {
                if (DialogScriptCompiler.IsProjectDialogScript(assetPath))
                    PendingScriptPaths.Add(assetPath);
            }
        }

        private static void ScheduleCompile()
        {
            if (PendingScriptPaths.Count == 0 || isCompileScheduled)
                return;

            isCompileScheduled = true;
            EditorApplication.delayCall += CompilePendingScripts;
        }

        private static void CompilePendingScripts()
        {
            isCompileScheduled = false;

            if (PendingScriptPaths.Count == 0)
                return;

            string[] scriptPaths = new string[PendingScriptPaths.Count];
            PendingScriptPaths.CopyTo(scriptPaths);
            PendingScriptPaths.Clear();
            isCompiling = true;

            try
            {
                foreach (string scriptPath in scriptPaths)
                    CompileScriptIfPresent(scriptPath);
            }
            finally
            {
                isCompiling = false;
            }
        }

        private static void CompileScriptIfPresent(string scriptPath)
        {
            if (AssetDatabase.LoadMainAssetAtPath(scriptPath) == null)
                return;

            try
            {
                DialogScriptCompiler.CompileToAssets(scriptPath);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Dialog script auto-compile skipped '{scriptPath}': {exception.Message}");
            }
        }
    }

    internal readonly struct DialogScriptCompilationResult
    {
        readonly DialogScriptPauseCompilationResult[] pauseContinuations;

        public DialogScriptCompilationResult(
            string scriptPath,
            string authoringGraphPath,
            string runtimeGraphPath,
            IEnumerable<DialogScriptPauseCompilationResult> pauseContinuations = null)
        {
            ScriptPath = scriptPath;
            AuthoringGraphPath = authoringGraphPath;
            RuntimeGraphPath = runtimeGraphPath;
            this.pauseContinuations = pauseContinuations?.ToArray() ?? Array.Empty<DialogScriptPauseCompilationResult>();
        }

        public string ScriptPath { get; }
        public string AuthoringGraphPath { get; }
        public string RuntimeGraphPath { get; }
        public IReadOnlyList<DialogScriptPauseCompilationResult> PauseContinuations =>
            pauseContinuations ?? Array.Empty<DialogScriptPauseCompilationResult>();
    }

    internal readonly struct DialogScriptPauseCompilationResult
    {
        public DialogScriptPauseCompilationResult(string sectionId, string authoringGraphPath, string runtimeGraphPath)
        {
            SectionId = sectionId ?? string.Empty;
            AuthoringGraphPath = authoringGraphPath ?? string.Empty;
            RuntimeGraphPath = runtimeGraphPath ?? string.Empty;
        }

        public string SectionId { get; }
        public string AuthoringGraphPath { get; }
        public string RuntimeGraphPath { get; }
    }

    internal sealed class DialogScriptParseResult
    {
        public DialogScriptParseResult(DialogScriptDocument document)
        {
            Document = document ?? new DialogScriptDocument();
        }

        public DialogScriptDocument Document { get; }
        public IReadOnlyList<DialogScriptDiagnostic> Diagnostics => Document.Diagnostics;
    }

    internal static class DialogScriptValidator
    {
        public static IReadOnlyList<DialogScriptDiagnostic> Validate(DialogScriptDocument document, string scriptPath)
        {
            var diagnostics = new List<DialogScriptDiagnostic>();

            if (document == null)
                return diagnostics;

            foreach (DialogScriptPauseStatement pause in document.Pauses)
            {
                if (!document.TryGetSection(pause.TargetSectionId, out _))
                {
                    diagnostics.Add(DialogScriptDiagnostic.Error(
                        "DIALOG_SCRIPT_MISSING_PAUSE_TARGET",
                        $"Pause target section '{pause.TargetSectionId}' does not exist.",
                        scriptPath,
                        pause.LineNumber));
                }
            }

            foreach (DialogScriptStatement statement in document.GetAllStatements())
            {
                if (statement is DialogScriptExternalFunctionStatement externalFunction)
                {
                    diagnostics.AddRange(DialogScriptExternalFunctionValidatorRegistry.Validate(
                        new DialogScriptExternalFunctionValidationContext(
                            scriptPath,
                            externalFunction.LineNumber,
                            externalFunction.FunctionName)));
                }

                if (statement is DialogScriptChoiceStatement choice)
                {
                    foreach (DialogScriptChoiceOption option in choice.Choices)
                    {
                        if (!document.TryGetSection(option.TargetSection, out _))
                        {
                            diagnostics.Add(DialogScriptDiagnostic.Error(
                                "DIALOG_SCRIPT_MISSING_CHOICE_TARGET",
                                $"Choice target section '{option.TargetSection}' does not exist.",
                                scriptPath,
                                option.LineNumber));
                        }
                    }
                }
            }

            return diagnostics;
        }
    }

    internal sealed class DialogScriptParser
    {
        public static DialogScriptParseResult Parse(string source, string scriptPath = "")
        {
            DialogScriptDocument document = new();
            List<DialogScriptStatement> currentStatements = document.MainStatements;
            string currentSpeaker = string.Empty;
            string[] lines = (source ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string line = lines[lineIndex].Trim();
                int lineNumber = lineIndex + 1;

                if (ShouldIgnoreLine(line))
                    continue;

                if (TryReadDirective(line, "@section", out string sectionId))
                {
                    currentStatements = document.GetOrCreateSection(sectionId);
                    currentSpeaker = string.Empty;
                    continue;
                }

                if (IsDirectiveKeyword(line, "@section"))
                {
                    AddMissingPayloadDiagnostic(document, scriptPath, lineNumber, "@section", "section id");
                    continue;
                }

                if (TryReadDirective(line, "@effect", out string effectCommands))
                {
                    currentStatements.Add(new DialogScriptExternalFunctionStatement(
                        $"effect:{effectCommands.Trim()}",
                        "Dialog script effect",
                        lineNumber));
                    continue;
                }

                if (IsDirectiveKeyword(line, "@effect"))
                {
                    AddMissingPayloadDiagnostic(document, scriptPath, lineNumber, "@effect", "effect command payload");
                    continue;
                }

                if (TryReadDirective(line, "@function", out string functionName))
                {
                    currentStatements.Add(new DialogScriptExternalFunctionStatement(
                        functionName.Trim(),
                        "Dialog script function",
                        lineNumber));
                    continue;
                }

                if (IsDirectiveKeyword(line, "@function"))
                {
                    AddMissingPayloadDiagnostic(document, scriptPath, lineNumber, "@function", "function name");
                    continue;
                }

                if (TryReadDirective(line, "@pause", out string pauseTarget))
                {
                    var pause = new DialogScriptPauseStatement(pauseTarget.Trim(), lineNumber);
                    currentStatements.Add(pause);
                    document.AddPause(pause);
                    continue;
                }

                if (IsDirectiveKeyword(line, "@pause"))
                {
                    AddMissingPayloadDiagnostic(document, scriptPath, lineNumber, "@pause", "target section id");
                    continue;
                }

                if (IsDirectiveKeyword(line, "@choice"))
                {
                    DialogScriptChoiceStatement choice = new(lineNumber);
                    lineIndex = ReadChoiceOptions(lines, lineIndex + 1, choice, document, scriptPath) - 1;
                    currentStatements.Add(choice);
                    continue;
                }

                if (line.StartsWith("@", StringComparison.Ordinal))
                {
                    document.AddDiagnostic(DialogScriptDiagnostic.Error(
                        "DIALOG_SCRIPT_UNKNOWN_DIRECTIVE",
                        $"Unknown dialog script directive '{line}'.",
                        scriptPath,
                        lineNumber));
                    continue;
                }

                if (line.EndsWith(":", StringComparison.Ordinal) && !line.StartsWith(">", StringComparison.Ordinal))
                {
                    currentSpeaker = line.Substring(0, line.Length - 1).Trim();
                    continue;
                }

                if (line.StartsWith(">", StringComparison.Ordinal))
                {
                    string text = line.Substring(1).Trim();

                    if (!string.IsNullOrWhiteSpace(text))
                        currentStatements.Add(new DialogScriptSentenceStatement(currentSpeaker, text, lineNumber));
                }
            }

            return new DialogScriptParseResult(document);
        }

        private static int ReadChoiceOptions(
            string[] lines,
            int lineIndex,
            DialogScriptChoiceStatement choice,
            DialogScriptDocument document,
            string scriptPath)
        {
            while (lineIndex < lines.Length)
            {
                string line = lines[lineIndex].Trim();
                int lineNumber = lineIndex + 1;

                if (ShouldIgnoreLine(line))
                {
                    lineIndex++;
                    continue;
                }

                if (!line.StartsWith("-", StringComparison.Ordinal))
                    break;

                string option = line.Substring(1).Trim();
                int targetMarker = option.LastIndexOf("->", StringComparison.Ordinal);

                if (targetMarker < 0)
                {
                    document.AddDiagnostic(DialogScriptDiagnostic.Error(
                        "DIALOG_SCRIPT_MALFORMED_CHOICE",
                        $"Choice option '{option}' is missing a '-> section_id' target.",
                        scriptPath,
                        lineNumber));
                    lineIndex++;
                    continue;
                }

                string text = option.Substring(0, targetMarker).Trim();
                string target = option.Substring(targetMarker + 2).Trim();

                if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(target))
                {
                    document.AddDiagnostic(DialogScriptDiagnostic.Error(
                        "DIALOG_SCRIPT_MALFORMED_CHOICE",
                        $"Choice option '{option}' must include both text and target section.",
                        scriptPath,
                        lineNumber));
                    lineIndex++;
                    continue;
                }

                choice.Choices.Add(new DialogScriptChoiceOption(text, target, lineNumber));
                lineIndex++;
            }

            return lineIndex;
        }

        private static bool TryReadDirective(string line, string directive, out string value)
        {
            value = string.Empty;

            if (!IsDirectiveKeyword(line, directive))
                return false;

            value = line.Substring(directive.Length).Trim();
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool IsDirectiveKeyword(string line, string directive)
        {
            return string.Equals(line, directive, StringComparison.Ordinal) ||
                line.StartsWith(directive + " ", StringComparison.Ordinal) ||
                line.StartsWith(directive + "\t", StringComparison.Ordinal);
        }

        private static void AddMissingPayloadDiagnostic(
            DialogScriptDocument document,
            string scriptPath,
            int lineNumber,
            string directive,
            string payloadDescription)
        {
            document.AddDiagnostic(DialogScriptDiagnostic.Error(
                "DIALOG_SCRIPT_MISSING_DIRECTIVE_PAYLOAD",
                $"{directive} requires a {payloadDescription}.",
                scriptPath,
                lineNumber));
        }

        private static bool ShouldIgnoreLine(string line) =>
            string.IsNullOrWhiteSpace(line) ||
            line.StartsWith("#", StringComparison.Ordinal) ||
            line == "---" ||
            line.StartsWith("*", StringComparison.Ordinal) ||
            line.StartsWith("(", StringComparison.Ordinal);
    }

    internal sealed class DialogScriptDocument
    {
        private readonly Dictionary<string, List<DialogScriptStatement>> sections = new(StringComparer.Ordinal);
        private readonly List<DialogScriptDiagnostic> diagnostics = new();
        private readonly List<DialogScriptPauseStatement> pauses = new();

        public List<DialogScriptStatement> MainStatements { get; } = new();
        public IReadOnlyList<DialogScriptDiagnostic> Diagnostics => diagnostics;
        public IReadOnlyList<DialogScriptPauseStatement> Pauses => pauses;

        public List<DialogScriptStatement> GetOrCreateSection(string sectionId)
        {
            if (string.IsNullOrWhiteSpace(sectionId))
                throw new ArgumentException("Section id cannot be empty.", nameof(sectionId));

            sectionId = sectionId.Trim();

            if (!sections.TryGetValue(sectionId, out List<DialogScriptStatement> statements))
            {
                statements = new List<DialogScriptStatement>();
                sections.Add(sectionId, statements);
            }

            return statements;
        }

        public bool TryGetSection(string sectionId, out IReadOnlyList<DialogScriptStatement> statements)
        {
            if (sections.TryGetValue(sectionId, out List<DialogScriptStatement> sectionStatements))
            {
                statements = sectionStatements;
                return true;
            }

            statements = null;
            return false;
        }

        public IEnumerable<DialogScriptStatement> GetAllStatements()
        {
            foreach (DialogScriptStatement statement in MainStatements)
                yield return statement;

            foreach (List<DialogScriptStatement> sectionStatements in sections.Values)
            {
                foreach (DialogScriptStatement statement in sectionStatements)
                    yield return statement;
            }
        }

        public void AddDiagnostic(DialogScriptDiagnostic diagnostic)
        {
            if (diagnostic != null)
                diagnostics.Add(diagnostic);
        }

        public void AddPause(DialogScriptPauseStatement pause)
        {
            if (pause != null)
                pauses.Add(pause);
        }
    }

    internal abstract class DialogScriptStatement
    {
        protected DialogScriptStatement(int lineNumber)
        {
            LineNumber = lineNumber;
        }

        public int LineNumber { get; }
    }

    internal sealed class DialogScriptSentenceStatement : DialogScriptStatement
    {
        public DialogScriptSentenceStatement(string speaker, string text, int lineNumber)
            : base(lineNumber)
        {
            Speaker = speaker ?? string.Empty;
            Text = text ?? string.Empty;
        }

        public string Speaker { get; }
        public string Text { get; }
    }

    internal sealed class DialogScriptExternalFunctionStatement : DialogScriptStatement
    {
        public DialogScriptExternalFunctionStatement(string functionName, string description, int lineNumber)
            : base(lineNumber)
        {
            FunctionName = functionName ?? string.Empty;
            Description = description ?? string.Empty;
        }

        public string FunctionName { get; }
        public string Description { get; }
    }

    internal sealed class DialogScriptPauseStatement : DialogScriptStatement
    {
        public DialogScriptPauseStatement(string targetSectionId, int lineNumber)
            : base(lineNumber)
        {
            TargetSectionId = targetSectionId ?? string.Empty;
        }

        public string TargetSectionId { get; }
    }

    internal sealed class DialogScriptChoiceStatement : DialogScriptStatement
    {
        public DialogScriptChoiceStatement(int lineNumber)
            : base(lineNumber)
        {
        }

        public List<DialogScriptChoiceOption> Choices { get; } = new();
    }

    internal sealed class DialogScriptChoiceOption
    {
        public DialogScriptChoiceOption(string text, string targetSection, int lineNumber)
        {
            Text = text ?? string.Empty;
            TargetSection = targetSection ?? string.Empty;
            LineNumber = lineNumber;
        }

        public string Text { get; }
        public string TargetSection { get; }
        public int LineNumber { get; }
    }

    internal readonly struct BuildSequenceResult
    {
        public BuildSequenceResult(GtkNode first, GtkNode last)
        {
            First = first;
            Last = last;
        }

        public GtkNode First { get; }
        public GtkNode Last { get; }
    }

    internal readonly struct AuthoringNodeBinding
    {
        public AuthoringNodeBinding(GtkNode node, object model)
        {
            Node = node;
            Model = model;
        }

        public GtkNode Node { get; }
        public object Model { get; }
    }
}
