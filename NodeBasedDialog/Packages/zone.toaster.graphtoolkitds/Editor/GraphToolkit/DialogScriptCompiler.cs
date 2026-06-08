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

            DialogScriptDocument document = DialogScriptParser.Parse(File.ReadAllText(scriptPath));
            string graphPath = GetAuthoringGraphPath(scriptPath);

            if (AssetDatabase.LoadMainAssetAtPath(graphPath) != null)
                AssetDatabase.DeleteAsset(graphPath);

            DialogAuthoringGraph authoringGraph = GraphDatabase.CreateGraph<DialogAuthoringGraph>(graphPath);
            PopulateAuthoringGraph(authoringGraph, document);
            GraphDatabase.SaveGraphIfDirty(authoringGraph);
            AssetDatabase.ImportAsset(graphPath);

            DialogNodeGraph runtimeGraph = DialogGraphCompiler.CompileToRuntimeAsset(graphPath);
            string runtimePath = AssetDatabase.GetAssetPath(runtimeGraph);

            Debug.Log($"Compiled dialog script '{scriptPath}' to '{graphPath}' and '{runtimePath}'.");
            return new DialogScriptCompilationResult(scriptPath, graphPath, runtimePath);
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
            string directory = Path.GetDirectoryName(scriptPath)?.Replace("\\", "/");
            string fileName = Path.GetFileName(scriptPath);
            string graphName = fileName.EndsWith($".{SourceExtension}", StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - SourceExtension.Length - 1)
                : Path.GetFileNameWithoutExtension(scriptPath);

            string graphDirectory = GetAuthoringGraphDirectory(directory);
            EnsureAssetDirectory(graphDirectory);
            return $"{graphDirectory}/{graphName}.{DialogAuthoringGraph.AssetExtension}";
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

        private static void PopulateAuthoringGraph(DialogAuthoringGraph authoringGraph, DialogScriptDocument document)
        {
            object graphModel = GetGraphModel(authoringGraph);

            if (graphModel == null)
                throw new InvalidOperationException("Could not access Graph Toolkit graph model.");

            AuthoringNodeBinding startNode = CreateAuthoringNode(graphModel, new DialogStartNode(), new Vector2(-NodeSpacingX, 0f));
            DialogScriptGraphBuilder builder = new(graphModel, document);
            BuildSequenceResult main = builder.Build(document.MainStatements, Vector2.zero, "main");

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
                    Vector2 position = origin + new Vector2(index * NodeSpacingX, 0f);
                    GtkNode current = CreateNode(statement, position, label, index);

                    if (current == null)
                        continue;

                    first ??= current;

                    if (previous != null)
                        CreateWire(graphModel, previous, DialogGraphPorts.Next, current, DialogGraphPorts.Input);

                    if (statement is DialogScriptChoiceStatement && index < statements.Count - 1)
                        throw new InvalidOperationException($"Choice in '{label}' must be the last statement in its sequence.");

                    previous = statement is DialogScriptChoiceStatement ? null : current;
                }

                return new BuildSequenceResult(first, previous);
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
        public DialogScriptCompilationResult(string scriptPath, string authoringGraphPath, string runtimeGraphPath)
        {
            ScriptPath = scriptPath;
            AuthoringGraphPath = authoringGraphPath;
            RuntimeGraphPath = runtimeGraphPath;
        }

        public string ScriptPath { get; }
        public string AuthoringGraphPath { get; }
        public string RuntimeGraphPath { get; }
    }

    internal sealed class DialogScriptParser
    {
        public static DialogScriptDocument Parse(string source)
        {
            DialogScriptDocument document = new();
            List<DialogScriptStatement> currentStatements = document.MainStatements;
            string currentSpeaker = string.Empty;
            string[] lines = (source ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string line = lines[lineIndex].Trim();

                if (ShouldIgnoreLine(line))
                    continue;

                if (TryReadDirective(line, "@section", out string sectionId))
                {
                    currentStatements = document.GetOrCreateSection(sectionId);
                    currentSpeaker = string.Empty;
                    continue;
                }

                if (TryReadDirective(line, "@effect", out string effectCommands))
                {
                    currentStatements.Add(new DialogScriptExternalFunctionStatement(
                        $"effect:{effectCommands.Trim()}",
                        "Dialog script effect"));
                    continue;
                }

                if (TryReadDirective(line, "@function", out string functionName))
                {
                    currentStatements.Add(new DialogScriptExternalFunctionStatement(functionName.Trim(), "Dialog script function"));
                    continue;
                }

                if (line.StartsWith("@choice", StringComparison.Ordinal))
                {
                    DialogScriptChoiceStatement choice = new();
                    lineIndex = ReadChoiceOptions(lines, lineIndex + 1, choice) - 1;
                    currentStatements.Add(choice);
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
                        currentStatements.Add(new DialogScriptSentenceStatement(currentSpeaker, text));
                }
            }

            return document;
        }

        private static int ReadChoiceOptions(string[] lines, int lineIndex, DialogScriptChoiceStatement choice)
        {
            while (lineIndex < lines.Length)
            {
                string line = lines[lineIndex].Trim();

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
                    throw new InvalidOperationException($"Choice option '{option}' is missing a '-> section_id' target.");

                string text = option.Substring(0, targetMarker).Trim();
                string target = option.Substring(targetMarker + 2).Trim();

                if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(target))
                    throw new InvalidOperationException($"Choice option '{option}' must include both text and target section.");

                choice.Choices.Add(new DialogScriptChoiceOption(text, target));
                lineIndex++;
            }

            return lineIndex;
        }

        private static bool TryReadDirective(string line, string directive, out string value)
        {
            value = string.Empty;

            if (!line.StartsWith(directive, StringComparison.Ordinal))
                return false;

            value = line.Substring(directive.Length).Trim();
            return !string.IsNullOrWhiteSpace(value);
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

        public List<DialogScriptStatement> MainStatements { get; } = new();

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
    }

    internal abstract class DialogScriptStatement
    {
    }

    internal sealed class DialogScriptSentenceStatement : DialogScriptStatement
    {
        public DialogScriptSentenceStatement(string speaker, string text)
        {
            Speaker = speaker ?? string.Empty;
            Text = text ?? string.Empty;
        }

        public string Speaker { get; }
        public string Text { get; }
    }

    internal sealed class DialogScriptExternalFunctionStatement : DialogScriptStatement
    {
        public DialogScriptExternalFunctionStatement(string functionName, string description)
        {
            FunctionName = functionName ?? string.Empty;
            Description = description ?? string.Empty;
        }

        public string FunctionName { get; }
        public string Description { get; }
    }

    internal sealed class DialogScriptChoiceStatement : DialogScriptStatement
    {
        public List<DialogScriptChoiceOption> Choices { get; } = new();
    }

    internal sealed class DialogScriptChoiceOption
    {
        public DialogScriptChoiceOption(string text, string targetSection)
        {
            Text = text ?? string.Empty;
            TargetSection = targetSection ?? string.Empty;
        }

        public string Text { get; }
        public string TargetSection { get; }
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
