using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
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
            DialogCompilerInputMetadata inputMetadata = DialogCompilerMetadata.ForDialogScriptSource(source);

            if (TryGetCurrentScriptCompilation(scriptPath, document, inputMetadata, out DialogScriptCompilationResult currentResult))
            {
                Debug.Log($"Dialog script '{scriptPath}' generated assets are already current.");
                return currentResult;
            }

            VariablesConfig variablesConfig = BuildVariablesConfig(scriptPath, document);
            string graphPath = GetAuthoringGraphPath(scriptPath);

            DialogNodeGraph runtimeGraph = CompileGraph(
                scriptPath,
                graphPath,
                document,
                document.MainStatements,
                "main",
                variablesConfig,
                inputMetadata);
            string runtimePath = AssetDatabase.GetAssetPath(runtimeGraph);
            List<DialogScriptPauseCompilationResult> pauseContinuations = CompilePauseContinuations(
                scriptPath,
                document,
                variablesConfig,
                inputMetadata);

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

        private static bool TryGetCurrentScriptCompilation(
            string scriptPath,
            DialogScriptDocument document,
            DialogCompilerInputMetadata inputMetadata,
            out DialogScriptCompilationResult result)
        {
            result = default;
            string graphPath = GetAuthoringGraphPath(scriptPath);
            string runtimePath = DialogGraphCompiler.GetRuntimeAssetPath(graphPath);

            if (document.Variables.Count > 0 &&
                AssetDatabase.LoadAssetAtPath<VariablesConfig>(GetVariablesConfigPath(scriptPath)) == null)
            {
                return false;
            }

            if (!IsRuntimeCurrentForScript(graphPath, runtimePath, inputMetadata, out DialogNodeGraph mainRuntimeGraph))
                return false;

            var continuations = new List<DialogScriptPauseCompilationResult>();
            var compiledSectionIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (DialogScriptPauseStatement pause in document.Pauses)
            {
                if (!compiledSectionIds.Add(pause.TargetSectionId))
                    continue;

                if (!document.TryGetSection(pause.TargetSectionId, out _))
                    continue;

                string continuationGraphPath = GetAuthoringGraphPath(scriptPath, pause.TargetSectionId);
                string continuationRuntimePath = DialogGraphCompiler.GetRuntimeAssetPath(continuationGraphPath);

                if (!IsRuntimeCurrentForScript(continuationGraphPath, continuationRuntimePath, inputMetadata, out _))
                    return false;

                continuations.Add(new DialogScriptPauseCompilationResult(
                    pause.TargetSectionId,
                    continuationGraphPath,
                    continuationRuntimePath));
            }

            if (continuations.Count > 0)
            {
                DialogScriptPauseCompilationResult firstContinuation = continuations[0];
                string continuationGuid = AssetDatabase.AssetPathToGUID(firstContinuation.RuntimeGraphPath);

                if (!string.Equals(mainRuntimeGraph.PauseTargetSectionId, firstContinuation.SectionId, StringComparison.Ordinal) ||
                    !string.Equals(mainRuntimeGraph.PauseContinuationGraphGuid, continuationGuid, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            result = new DialogScriptCompilationResult(scriptPath, graphPath, runtimePath, continuations);
            return true;
        }

        private static bool IsRuntimeCurrentForScript(
            string graphPath,
            string runtimePath,
            DialogCompilerInputMetadata inputMetadata,
            out DialogNodeGraph runtimeGraph)
        {
            runtimeGraph = AssetDatabase.LoadAssetAtPath<DialogNodeGraph>(runtimePath);

            return AssetDatabase.LoadMainAssetAtPath(graphPath) != null &&
                runtimeGraph != null &&
                runtimeGraph.CompilerSchemaVersion == DialogCompilerMetadata.SchemaVersion &&
                string.Equals(runtimeGraph.CompilerInputKind, inputMetadata.InputKind, StringComparison.Ordinal) &&
                string.Equals(runtimeGraph.CompilerInputHash, inputMetadata.InputHash, StringComparison.Ordinal) &&
                runtimeGraph.NodesList != null &&
                runtimeGraph.NodesList.All(node => node != null);
        }

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
            string entryLabel,
            VariablesConfig variablesConfig,
            DialogCompilerInputMetadata inputMetadata)
        {
            if (AssetDatabase.LoadMainAssetAtPath(graphPath) != null)
                AssetDatabase.DeleteAsset(graphPath);

            DialogAuthoringGraph authoringGraph = GraphDatabase.CreateGraph<DialogAuthoringGraph>(graphPath);
            authoringGraph.ConfigureMigratedSettings(variablesConfig, string.Empty, string.Empty);
            PopulateAuthoringGraph(authoringGraph, graphPath, document, entryStatements, entryLabel, variablesConfig);
            GraphDatabase.SaveGraphIfDirty(authoringGraph);
            AssetDatabase.ImportAsset(graphPath);

            DialogNodeGraph runtimeGraph = DialogGraphCompiler.CompileToRuntimeAsset(graphPath, inputMetadata);
            Debug.Log($"Compiled dialog script '{scriptPath}' section '{entryLabel}' to '{graphPath}'.");
            return runtimeGraph;
        }

        private static List<DialogScriptPauseCompilationResult> CompilePauseContinuations(
            string scriptPath,
            DialogScriptDocument document,
            VariablesConfig variablesConfig,
            DialogCompilerInputMetadata inputMetadata)
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
                    pause.TargetSectionId,
                    variablesConfig,
                    inputMetadata);

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

        private static VariablesConfig BuildVariablesConfig(string scriptPath, DialogScriptDocument document)
        {
            if (document == null || document.Variables.Count == 0)
                return null;

            string variablesPath = GetVariablesConfigPath(scriptPath);
            EnsureAssetDirectory(Path.GetDirectoryName(variablesPath)?.Replace("\\", "/"));

            VariablesConfig variablesConfig = AssetDatabase.LoadAssetAtPath<VariablesConfig>(variablesPath);

            if (variablesConfig == null)
            {
                variablesConfig = ScriptableObject.CreateInstance<VariablesConfig>();
                variablesConfig.name = Path.GetFileNameWithoutExtension(variablesPath);
                AssetDatabase.CreateAsset(variablesConfig, variablesPath);
            }

            variablesConfig.Variables.Clear();

            foreach (DialogScriptVariableDeclaration declaration in document.Variables)
            {
                var variable = new Variable(declaration.Name, declaration.Type, false);

                switch (declaration.Type)
                {
                    case VariableType.Bool:
                        variable.SetValue(declaration.BoolValue);
                        break;
                    case VariableType.Int:
                        variable.SetValue(declaration.IntValue);
                        break;
                    case VariableType.Float:
                        variable.SetValue(declaration.FloatValue);
                        break;
                    case VariableType.String:
                        variable.SetValue(declaration.StringValue);
                        break;
                }

                variablesConfig.AddVariable(variable);
            }

            EditorUtility.SetDirty(variablesConfig);
            return variablesConfig;
        }

        private static string GetVariablesConfigPath(string scriptPath)
        {
            string directory = Path.GetDirectoryName(scriptPath)?.Replace("\\", "/");
            string fileName = Path.GetFileName(scriptPath);
            string graphName = fileName.EndsWith($".{SourceExtension}", StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - SourceExtension.Length - 1)
                : Path.GetFileNameWithoutExtension(scriptPath);
            string graphDirectory = GetAuthoringGraphDirectory(directory);
            EnsureAssetDirectory(graphDirectory);
            return $"{graphDirectory}/{graphName}_Variables.asset";
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
            string graphPath,
            DialogScriptDocument document,
            IReadOnlyList<DialogScriptStatement> entryStatements,
            string entryLabel,
            VariablesConfig variablesConfig)
        {
            object graphModel = GetGraphModel(authoringGraph);

            if (graphModel == null)
                throw new InvalidOperationException("Could not access Graph Toolkit graph model.");

            SetGraphModelGuid(graphModel, $"dialog-script:{graphPath}:{entryLabel}:graph");

            AuthoringNodeBinding startNode = CreateAuthoringNode(
                graphModel,
                new DialogStartNode(),
                new Vector2(-NodeSpacingX, 0f),
                $"{entryLabel}/start");

            if (variablesConfig != null)
            {
                AuthoringNodeBinding settingsNode = CreateAuthoringNode(
                    graphModel,
                    new DialogGraphSettingsNode(),
                    new Vector2(-NodeSpacingX, -NodeSpacingY),
                    $"{entryLabel}/settings");
                SetOption(settingsNode.Node, DialogGraphOptions.VariablesConfig, variablesConfig);
                RedefineNode(settingsNode.Model);
            }

            DialogScriptGraphBuilder builder = new(graphModel, document);
            BuildSequenceResult main = builder.Build(entryStatements, Vector2.zero, entryLabel);

            if (main.First != null)
            {
                CreateWire(
                    graphModel,
                    startNode.Node,
                    DialogGraphPorts.Next,
                    main.First,
                    DialogGraphPorts.Input,
                    $"{entryLabel}/start:{DialogGraphPorts.Next}->{GetNodeSourceKey(main.First)}:{DialogGraphPorts.Input}");
            }
        }

        private static object GetGraphModel(DialogAuthoringGraph graph)
        {
            FieldInfo implementationField = typeof(Graph).GetField("m_Implementation", BindingFlags.Instance | BindingFlags.NonPublic);
            return implementationField?.GetValue(graph);
        }

        private static AuthoringNodeBinding CreateAuthoringNode(
            object graphModel,
            GtkNode node,
            Vector2 position,
            string sourceKey = "")
        {
            if (!string.IsNullOrWhiteSpace(sourceKey) &&
                TryCreateAuthoringNodeWithDeterministicGuid(graphModel, node, position, sourceKey, out AuthoringNodeBinding binding))
            {
                return binding;
            }

            MethodInfo createNodeModel = graphModel.GetType().GetMethod("CreateNodeModel", BindingFlags.Instance | BindingFlags.Public);
            object nodeModel = createNodeModel?.Invoke(graphModel, new object[] { node, position });

            if (nodeModel == null)
                throw new InvalidOperationException($"Could not create authoring node model for {node.GetType().Name}.");

            return new AuthoringNodeBinding(node, nodeModel);
        }

        private static bool TryCreateAuthoringNodeWithDeterministicGuid(
            object graphModel,
            GtkNode node,
            Vector2 position,
            string sourceKey,
            out AuthoringNodeBinding binding)
        {
            binding = default;
            MethodInfo createNode = graphModel.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "CreateNode")
                        return false;

                    ParameterInfo[] parameters = method.GetParameters();
                    return parameters.Length == 6 &&
                        parameters[0].ParameterType == typeof(Type) &&
                        parameters[1].ParameterType == typeof(string) &&
                        parameters[2].ParameterType == typeof(Vector2) &&
                        parameters[3].ParameterType == typeof(Hash128);
                });

            if (createNode == null)
                return false;

            Type userNodeModelType = graphModel.GetType().Assembly
                .GetType("Unity.GraphToolkit.Editor.Implementation.UserNodeModelImp");

            if (userNodeModelType == null)
                return false;

            Type callbackType = createNode.GetParameters()[4].ParameterType;
            Type spawnFlagsType = createNode.GetParameters()[5].ParameterType;
            Delegate initializationCallback = CreateInitializeNodeCallback(callbackType, node);
            object spawnFlags = Enum.ToObject(spawnFlagsType, 0);
            object nodeModel = createNode.Invoke(
                graphModel,
                new object[]
                {
                    userNodeModelType,
                    string.Empty,
                    position,
                    Hash128.Compute(sourceKey),
                    initializationCallback,
                    spawnFlags
                });

            if (nodeModel == null)
                return false;

            binding = new AuthoringNodeBinding(node, nodeModel);
            return true;
        }

        private static Delegate CreateInitializeNodeCallback(Type callbackType, GtkNode node)
        {
            Type modelType = callbackType.GetGenericArguments()[0];
            ParameterExpression modelParameter = Expression.Parameter(modelType, "model");
            MethodInfo initializeMethod = typeof(DialogScriptCompiler)
                .GetMethod(nameof(InitializeAuthoringNodeModel), BindingFlags.Static | BindingFlags.NonPublic);
            MethodCallExpression body = Expression.Call(
                initializeMethod,
                Expression.Convert(modelParameter, typeof(object)),
                Expression.Constant(node, typeof(GtkNode)));

            return Expression.Lambda(callbackType, body, modelParameter).Compile();
        }

        private static void InitializeAuthoringNodeModel(object nodeModel, GtkNode node)
        {
            MethodInfo initCustomNode = nodeModel.GetType()
                .GetMethod("InitCustomNode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            initCustomNode?.Invoke(nodeModel, new object[] { node });
        }

        private static void SetGraphModelGuid(object graphModel, string sourceKey)
        {
            MethodInfo setGuid = graphModel.GetType()
                .GetMethod("SetGuid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            setGuid?.Invoke(graphModel, new object[] { Hash128.Compute(sourceKey) });
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
            string targetPortName,
            string sourceKey = "")
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

            Hash128 wireGuid = string.IsNullOrWhiteSpace(sourceKey)
                ? new Hash128()
                : Hash128.Compute(sourceKey);
            createWire.Invoke(graphModel, new object[] { inputPort, outputPort, wireGuid });
        }

        private static string GetNodeSourceKey(GtkNode node)
        {
            string sourceKey = DialogGraphOptionReader.Read(node, DialogGraphOptions.CompilerSourceKey, string.Empty);
            return string.IsNullOrWhiteSpace(sourceKey)
                ? node.GetType().FullName
                : sourceKey;
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
                List<FlowExit> pendingExits = new();

                for (int index = 0; index < statements.Count; index++)
                {
                    DialogScriptStatement statement = statements[index];

                    if (statement is DialogScriptPauseStatement)
                        break;

                    Vector2 position = origin + new Vector2(index * NodeSpacingX, 0f);
                    BuildSequenceResult current = CreateStatement(statement, position, label, index);

                    if (current.First == null)
                        continue;

                    first ??= current.First;

                    foreach (FlowExit pendingExit in pendingExits)
                    {
                        CreateWire(
                            graphModel,
                            pendingExit.Node,
                            pendingExit.PortName,
                            current.First,
                            DialogGraphPorts.Input,
                            $"{GetNodeSourceKey(pendingExit.Node)}:{pendingExit.PortName}->{GetNodeSourceKey(current.First)}:{DialogGraphPorts.Input}");
                    }

                    if (statement is DialogScriptChoiceStatement && HasStatementBeforePause(statements, index + 1))
                        throw new InvalidOperationException($"Choice in '{label}' must be the last statement in its sequence.");

                    if (statement is DialogScriptConditionalStatement conditional && conditional.UsesSectionTargets && HasStatementBeforePause(statements, index + 1))
                        throw new InvalidOperationException($"Section-jump condition in '{label}' must be the last statement in its sequence.");

                    pendingExits = new List<FlowExit>(current.Exits);
                }

                return new BuildSequenceResult(first, pendingExits);
            }

            private static bool HasStatementBeforePause(IReadOnlyList<DialogScriptStatement> statements, int startIndex)
            {
                if (startIndex >= statements.Count)
                    return false;

                return statements[startIndex] is not DialogScriptPauseStatement;
            }

            private BuildSequenceResult CreateStatement(DialogScriptStatement statement, Vector2 position, string label, int index)
            {
                switch (statement)
                {
                    case DialogScriptSentenceStatement sentence:
                    {
                        GtkNode node = CreateSentenceNode(
                            sentence,
                            position,
                            GetStatementSourceKey(label, sentence, index, "sentence"));
                        return BuildSequenceResult.Single(node, DialogGraphPorts.Next);
                    }
                    case DialogScriptExternalFunctionStatement externalFunction:
                    {
                        GtkNode node = CreateExternalFunctionNode(
                            externalFunction,
                            position,
                            GetStatementSourceKey(label, externalFunction, index, "external_function"));
                        return BuildSequenceResult.Single(node, DialogGraphPorts.Next);
                    }
                    case DialogScriptChoiceStatement choice:
                        return CreateChoiceNode(choice, position, label, index);
                    case DialogScriptConditionalStatement conditional:
                        return CreateConditionalNode(conditional, position, label, index);
                    default:
                        return BuildSequenceResult.Empty;
                }
            }

            private static string GetStatementSourceKey(
                string label,
                DialogScriptStatement statement,
                int index,
                string kind) =>
                $"{label}/line_{statement.LineNumber:D4}/index_{index:D4}/{kind}";

            private GtkNode CreateSentenceNode(DialogScriptSentenceStatement sentence, Vector2 position, string sourceKey)
            {
                AuthoringNodeBinding binding = CreateAuthoringNode(graphModel, new DialogSentenceNode(), position, sourceKey);
                SetOption(binding.Node, DialogGraphOptions.CompilerSourceKey, sourceKey);
                SetOption(binding.Node, DialogGraphOptions.CharacterName, sentence.Speaker);
                SetOption(binding.Node, DialogGraphOptions.SentenceText, sentence.Text);
                RedefineNode(binding.Model);
                return binding.Node;
            }

            private GtkNode CreateExternalFunctionNode(
                DialogScriptExternalFunctionStatement externalFunction,
                Vector2 position,
                string sourceKey)
            {
                AuthoringNodeBinding binding = CreateAuthoringNode(
                    graphModel,
                    new DialogExternalFunctionNode(),
                    position,
                    sourceKey);
                SetOption(binding.Node, DialogGraphOptions.CompilerSourceKey, sourceKey);
                SetOption(binding.Node, DialogGraphOptions.FunctionName, externalFunction.FunctionName);
                SetOption(binding.Node, DialogGraphOptions.FunctionDescription, externalFunction.Description);
                RedefineNode(binding.Model);
                return binding.Node;
            }

            private BuildSequenceResult CreateChoiceNode(DialogScriptChoiceStatement choice, Vector2 position, string label, int index)
            {
                if (choice.Choices.Count == 0)
                    throw new InvalidOperationException($"Choice in '{label}' has no answer options.");

                string sourceKey = GetStatementSourceKey(label, choice, index, "choice");
                AuthoringNodeBinding binding = CreateAuthoringNode(graphModel, new DialogAnswerNode(), position, sourceKey);
                int answerCount = Mathf.Clamp(choice.Choices.Count, 1, DialogGraphPorts.MaxAnswerPorts);
                SetOption(binding.Node, DialogGraphOptions.CompilerSourceKey, sourceKey);
                SetOption(binding.Node, DialogGraphOptions.AnswerCount, answerCount);
                RedefineNode(binding.Model);

                for (int choiceIndex = 0; choiceIndex < answerCount; choiceIndex++)
                {
                    DialogScriptChoiceOption option = choice.Choices[choiceIndex];
                    SetOption(binding.Node, DialogGraphOptions.AnswerTextPrefix + choiceIndex, option.Text);
                    SetOption(binding.Node, DialogGraphOptions.AnswerKeyPrefix + choiceIndex, option.TargetSection);
                    SetOption(binding.Node, DialogGraphOptions.AnswerConditionPrefix + choiceIndex, option.ConditionExpression);

                    BuildSequenceResult branch = BuildSection(
                        option.TargetSection,
                        position + new Vector2(NodeSpacingX, (choiceIndex - (answerCount - 1) * 0.5f) * NodeSpacingY),
                        $"{label}.choice_{index + 1}.{choiceIndex + 1}");

                    if (branch.First != null)
                    {
                        CreateWire(
                            graphModel,
                            binding.Node,
                            DialogGraphPorts.Answer(choiceIndex),
                            branch.First,
                            DialogGraphPorts.Input,
                            $"{sourceKey}:{DialogGraphPorts.Answer(choiceIndex)}->{GetNodeSourceKey(branch.First)}:{DialogGraphPorts.Input}");
                    }
                }

                return new BuildSequenceResult(binding.Node, Array.Empty<FlowExit>());
            }

            private BuildSequenceResult CreateConditionalNode(
                DialogScriptConditionalStatement conditional,
                Vector2 position,
                string label,
                int index)
            {
                string sourceKey = GetStatementSourceKey(label, conditional, index, "condition");
                AuthoringNodeBinding binding = CreateAuthoringNode(
                    graphModel,
                    new DialogVariableConditionNode(),
                    position,
                    sourceKey);
                SetOption(binding.Node, DialogGraphOptions.CompilerSourceKey, sourceKey);
                SetOption(binding.Node, DialogGraphOptions.ConditionExpression, conditional.ConditionExpression);
                RedefineNode(binding.Model);

                if (conditional.UsesSectionTargets)
                {
                    WireConditionalSectionTarget(
                        binding.Node,
                        DialogGraphPorts.True,
                        conditional.TrueTargetSection,
                        position + new Vector2(NodeSpacingX, -NodeSpacingY * 0.5f),
                        $"{label}.if_{index + 1}.true");
                    WireConditionalSectionTarget(
                        binding.Node,
                        DialogGraphPorts.False,
                        conditional.FalseTargetSection,
                        position + new Vector2(NodeSpacingX, NodeSpacingY * 0.5f),
                        $"{label}.if_{index + 1}.false");

                    return new BuildSequenceResult(binding.Node, Array.Empty<FlowExit>());
                }

                var exits = new List<FlowExit>();
                BuildSequenceResult trueBranch = Build(
                    conditional.TrueStatements,
                    position + new Vector2(NodeSpacingX, -NodeSpacingY * 0.5f),
                    $"{label}.if_{index + 1}.true");

                if (trueBranch.First != null)
                {
                    CreateWire(
                        graphModel,
                        binding.Node,
                        DialogGraphPorts.True,
                        trueBranch.First,
                        DialogGraphPorts.Input,
                        $"{sourceKey}:{DialogGraphPorts.True}->{GetNodeSourceKey(trueBranch.First)}:{DialogGraphPorts.Input}");
                    exits.AddRange(trueBranch.Exits);
                }
                else
                    exits.Add(new FlowExit(binding.Node, DialogGraphPorts.True));

                BuildSequenceResult falseBranch = Build(
                    conditional.FalseStatements,
                    position + new Vector2(NodeSpacingX, NodeSpacingY * 0.5f),
                    $"{label}.if_{index + 1}.false");

                if (falseBranch.First != null)
                {
                    CreateWire(
                        graphModel,
                        binding.Node,
                        DialogGraphPorts.False,
                        falseBranch.First,
                        DialogGraphPorts.Input,
                        $"{sourceKey}:{DialogGraphPorts.False}->{GetNodeSourceKey(falseBranch.First)}:{DialogGraphPorts.Input}");
                    exits.AddRange(falseBranch.Exits);
                }
                else
                    exits.Add(new FlowExit(binding.Node, DialogGraphPorts.False));

                return new BuildSequenceResult(binding.Node, exits);
            }

            private void WireConditionalSectionTarget(
                GtkNode conditionNode,
                string portName,
                string sectionId,
                Vector2 origin,
                string label)
            {
                BuildSequenceResult branch = BuildSection(sectionId, origin, label);

                if (branch.First != null)
                {
                    CreateWire(
                        graphModel,
                        conditionNode,
                        portName,
                        branch.First,
                        DialogGraphPorts.Input,
                        $"{GetNodeSourceKey(conditionNode)}:{portName}->{GetNodeSourceKey(branch.First)}:{DialogGraphPorts.Input}");
                }
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

            VariablesConfig variablesConfig = document.CreateValidationVariablesConfig();

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

                        AddConditionDiagnostics(option.ConditionExpression, option.LineNumber, document, variablesConfig, scriptPath, diagnostics);
                    }
                }

                if (statement is DialogScriptConditionalStatement conditional)
                {
                    AddConditionDiagnostics(conditional.ConditionExpression, conditional.LineNumber, document, variablesConfig, scriptPath, diagnostics);

                    if (conditional.UsesSectionTargets)
                    {
                        if (!document.TryGetSection(conditional.TrueTargetSection, out _))
                        {
                            diagnostics.Add(DialogScriptDiagnostic.Error(
                                "DIALOG_SCRIPT_MISSING_CONDITION_TARGET",
                                $"Condition true target section '{conditional.TrueTargetSection}' does not exist.",
                                scriptPath,
                                conditional.LineNumber));
                        }

                        if (!document.TryGetSection(conditional.FalseTargetSection, out _))
                        {
                            diagnostics.Add(DialogScriptDiagnostic.Error(
                                "DIALOG_SCRIPT_MISSING_CONDITION_TARGET",
                                $"Condition false target section '{conditional.FalseTargetSection}' does not exist.",
                                scriptPath,
                                conditional.LineNumber));
                        }
                    }
                }
            }

            return diagnostics;
        }

        private static void AddConditionDiagnostics(
            string conditionExpression,
            int lineNumber,
            DialogScriptDocument document,
            VariablesConfig variablesConfig,
            string scriptPath,
            List<DialogScriptDiagnostic> diagnostics)
        {
            if (string.IsNullOrWhiteSpace(conditionExpression))
                return;

            if (!DialogConditionExpression.TryParse(conditionExpression, out DialogConditionExpression parsedExpression, out string parseError))
            {
                diagnostics.Add(DialogScriptDiagnostic.Error(
                    "DIALOG_SCRIPT_INVALID_CONDITION",
                    parseError,
                    scriptPath,
                    lineNumber));
                return;
            }

            if (document.Variables.Count == 0)
            {
                diagnostics.Add(DialogScriptDiagnostic.Error(
                    "DIALOG_SCRIPT_MISSING_VARIABLES",
                    $"Condition '{conditionExpression}' requires @var declarations.",
                    scriptPath,
                    lineNumber));
                return;
            }

            if (!parsedExpression.Validate(variablesConfig, out string validationError))
            {
                diagnostics.Add(DialogScriptDiagnostic.Error(
                    "DIALOG_SCRIPT_INVALID_CONDITION",
                    validationError,
                    scriptPath,
                    lineNumber));
            }
        }
    }

    internal sealed class DialogScriptParser
    {
        public static DialogScriptParseResult Parse(string source, string scriptPath = "")
        {
            DialogScriptDocument document = new();
            List<DialogScriptStatement> currentStatements = document.MainStatements;
            string currentSpeaker = string.Empty;
            var ifStack = new Stack<DialogScriptIfParseContext>();
            string[] lines = (source ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string line = lines[lineIndex].Trim();
                int lineNumber = lineIndex + 1;

                if (ShouldIgnoreLine(line))
                    continue;

                if (TryReadDirective(line, "@var", out string variablePayload))
                {
                    ParseVariableDeclaration(variablePayload, document, scriptPath, lineNumber);
                    continue;
                }

                if (IsDirectiveKeyword(line, "@var"))
                {
                    AddMissingPayloadDiagnostic(document, scriptPath, lineNumber, "@var", "variable declaration");
                    continue;
                }

                if (TryReadDirective(line, "@section", out string sectionId))
                {
                    if (ifStack.Count > 0)
                    {
                        document.AddDiagnostic(DialogScriptDiagnostic.Error(
                            "DIALOG_SCRIPT_SECTION_IN_CONDITIONAL",
                            "@section cannot appear inside an @if block. Move the section outside the block or use section-jump @if syntax.",
                            scriptPath,
                            lineNumber));
                        continue;
                    }

                    currentStatements = document.GetOrCreateSection(sectionId);
                    currentSpeaker = string.Empty;
                    continue;
                }

                if (IsDirectiveKeyword(line, "@section"))
                {
                    AddMissingPayloadDiagnostic(document, scriptPath, lineNumber, "@section", "section id");
                    continue;
                }

                if (TryReadDirective(line, "@if", out string conditionPayload))
                {
                    if (TryParseConditionalSectionJump(
                            conditionPayload,
                            out string conditionExpression,
                            out string trueTargetSection,
                            out string falseTargetSection))
                    {
                        currentStatements.Add(new DialogScriptConditionalStatement(
                            conditionExpression,
                            trueTargetSection,
                            falseTargetSection,
                            lineNumber));
                        continue;
                    }

                    var conditional = new DialogScriptConditionalStatement(conditionPayload.Trim(), lineNumber);
                    currentStatements.Add(conditional);
                    ifStack.Push(new DialogScriptIfParseContext(conditional, currentStatements, lineNumber));
                    currentStatements = conditional.TrueStatements;
                    continue;
                }

                if (IsDirectiveKeyword(line, "@if"))
                {
                    AddMissingPayloadDiagnostic(document, scriptPath, lineNumber, "@if", "condition expression");
                    continue;
                }

                if (IsDirectiveKeyword(line, "@else"))
                {
                    if (ifStack.Count == 0)
                    {
                        document.AddDiagnostic(DialogScriptDiagnostic.Error(
                            "DIALOG_SCRIPT_UNEXPECTED_ELSE",
                            "@else has no matching @if.",
                            scriptPath,
                            lineNumber));
                        continue;
                    }

                    DialogScriptIfParseContext context = ifStack.Pop();

                    if (context.IsElseActive)
                    {
                        document.AddDiagnostic(DialogScriptDiagnostic.Error(
                            "DIALOG_SCRIPT_DUPLICATE_ELSE",
                            "@if block already has an @else branch.",
                            scriptPath,
                            lineNumber));
                        ifStack.Push(context);
                        continue;
                    }

                    context = context.ActivateElse();
                    ifStack.Push(context);
                    currentStatements = context.Conditional.FalseStatements;
                    continue;
                }

                if (IsDirectiveKeyword(line, "@endif"))
                {
                    if (ifStack.Count == 0)
                    {
                        document.AddDiagnostic(DialogScriptDiagnostic.Error(
                            "DIALOG_SCRIPT_UNEXPECTED_ENDIF",
                            "@endif has no matching @if.",
                            scriptPath,
                            lineNumber));
                        continue;
                    }

                    DialogScriptIfParseContext context = ifStack.Pop();
                    currentStatements = context.ParentStatements;
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

            while (ifStack.Count > 0)
            {
                DialogScriptIfParseContext context = ifStack.Pop();
                document.AddDiagnostic(DialogScriptDiagnostic.Error(
                    "DIALOG_SCRIPT_MISSING_ENDIF",
                    "@if block is missing a matching @endif.",
                    scriptPath,
                    context.LineNumber));
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
                string conditionExpression = string.Empty;

                if (TryReadChoiceCondition(option, out string guardedCondition, out string guardedOption))
                {
                    conditionExpression = guardedCondition;
                    option = guardedOption;
                }
                else if (option.StartsWith("[if ", StringComparison.Ordinal))
                {
                    document.AddDiagnostic(DialogScriptDiagnostic.Error(
                        "DIALOG_SCRIPT_MALFORMED_CHOICE",
                        $"Choice option '{option}' has an unterminated condition guard.",
                        scriptPath,
                        lineNumber));
                    lineIndex++;
                    continue;
                }

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

                choice.Choices.Add(new DialogScriptChoiceOption(text, target, lineNumber, conditionExpression));
                lineIndex++;
            }

            return lineIndex;
        }

        private static bool TryReadChoiceCondition(string option, out string conditionExpression, out string remainingOption)
        {
            conditionExpression = string.Empty;
            remainingOption = option;

            if (!option.StartsWith("[if ", StringComparison.Ordinal))
                return false;

            int closeIndex = option.IndexOf(']');

            if (closeIndex < 0)
                return false;

            conditionExpression = option.Substring("[if ".Length, closeIndex - "[if ".Length).Trim();
            remainingOption = option.Substring(closeIndex + 1).Trim();
            return true;
        }

        private static bool TryParseConditionalSectionJump(
            string payload,
            out string conditionExpression,
            out string trueTargetSection,
            out string falseTargetSection)
        {
            conditionExpression = string.Empty;
            trueTargetSection = string.Empty;
            falseTargetSection = string.Empty;

            int arrowIndex = payload.IndexOf("->", StringComparison.Ordinal);

            if (arrowIndex < 0)
                return false;

            string condition = payload.Substring(0, arrowIndex).Trim();
            string targetPayload = payload.Substring(arrowIndex + 2).Trim();
            int elseIndex = targetPayload.IndexOf(" else ", StringComparison.Ordinal);

            if (elseIndex < 0)
                return false;

            conditionExpression = condition;
            trueTargetSection = targetPayload.Substring(0, elseIndex).Trim();
            falseTargetSection = targetPayload.Substring(elseIndex + " else ".Length).Trim();
            return !string.IsNullOrWhiteSpace(conditionExpression) &&
                !string.IsNullOrWhiteSpace(trueTargetSection) &&
                !string.IsNullOrWhiteSpace(falseTargetSection);
        }

        private static void ParseVariableDeclaration(
            string payload,
            DialogScriptDocument document,
            string scriptPath,
            int lineNumber)
        {
            int equalsIndex = payload.IndexOf('=');

            if (equalsIndex < 0)
            {
                document.AddDiagnostic(DialogScriptDiagnostic.Error(
                    "DIALOG_SCRIPT_MALFORMED_VARIABLE",
                    "@var requires 'name:type = default'.",
                    scriptPath,
                    lineNumber));
                return;
            }

            string nameAndType = payload.Substring(0, equalsIndex).Trim();
            string valueText = payload.Substring(equalsIndex + 1).Trim();
            int typeMarker = nameAndType.IndexOf(':');

            if (typeMarker < 0)
            {
                document.AddDiagnostic(DialogScriptDiagnostic.Error(
                    "DIALOG_SCRIPT_MALFORMED_VARIABLE",
                    "@var requires an explicit type, for example '@var psyche:int = 100'.",
                    scriptPath,
                    lineNumber));
                return;
            }

            string variableName = nameAndType.Substring(0, typeMarker).Trim();
            string typeText = nameAndType.Substring(typeMarker + 1).Trim();

            if (string.IsNullOrWhiteSpace(variableName) ||
                !TryParseVariableType(typeText, out VariableType variableType) ||
                !TryParseVariableDefault(valueText, variableType, out bool boolValue, out int intValue, out float floatValue, out string stringValue))
            {
                document.AddDiagnostic(DialogScriptDiagnostic.Error(
                    "DIALOG_SCRIPT_MALFORMED_VARIABLE",
                    $"Could not parse variable declaration '{payload}'.",
                    scriptPath,
                    lineNumber));
                return;
            }

            var declaration = new DialogScriptVariableDeclaration(
                variableName,
                variableType,
                boolValue,
                intValue,
                floatValue,
                stringValue,
                lineNumber);

            if (!document.AddVariable(declaration))
            {
                document.AddDiagnostic(DialogScriptDiagnostic.Error(
                    "DIALOG_SCRIPT_DUPLICATE_VARIABLE",
                    $"Variable '{variableName}' is declared more than once.",
                    scriptPath,
                    lineNumber));
            }
        }

        private static bool TryParseVariableType(string typeText, out VariableType variableType)
        {
            switch (typeText.Trim().ToLowerInvariant())
            {
                case "bool":
                    variableType = VariableType.Bool;
                    return true;
                case "int":
                    variableType = VariableType.Int;
                    return true;
                case "float":
                    variableType = VariableType.Float;
                    return true;
                case "string":
                    variableType = VariableType.String;
                    return true;
                default:
                    variableType = VariableType.String;
                    return false;
            }
        }

        private static bool TryParseVariableDefault(
            string valueText,
            VariableType variableType,
            out bool boolValue,
            out int intValue,
            out float floatValue,
            out string stringValue)
        {
            boolValue = false;
            intValue = 0;
            floatValue = 0f;
            stringValue = string.Empty;

            switch (variableType)
            {
                case VariableType.Bool:
                    return bool.TryParse(valueText, out boolValue);
                case VariableType.Int:
                    return int.TryParse(valueText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out intValue);
                case VariableType.Float:
                    return float.TryParse(valueText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out floatValue);
                case VariableType.String:
                    stringValue = UnquoteString(valueText);
                    return true;
                default:
                    return false;
            }
        }

        private static string UnquoteString(string valueText)
        {
            if (valueText.Length >= 2 &&
                ((valueText[0] == '"' && valueText[valueText.Length - 1] == '"') ||
                 (valueText[0] == '\'' && valueText[valueText.Length - 1] == '\'')))
            {
                return valueText.Substring(1, valueText.Length - 2);
            }

            return valueText;
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

        private readonly struct DialogScriptIfParseContext
        {
            public DialogScriptIfParseContext(
                DialogScriptConditionalStatement conditional,
                List<DialogScriptStatement> parentStatements,
                int lineNumber,
                bool isElseActive = false)
            {
                Conditional = conditional;
                ParentStatements = parentStatements;
                LineNumber = lineNumber;
                IsElseActive = isElseActive;
            }

            public DialogScriptConditionalStatement Conditional { get; }
            public List<DialogScriptStatement> ParentStatements { get; }
            public int LineNumber { get; }
            public bool IsElseActive { get; }

            public DialogScriptIfParseContext ActivateElse() =>
                new(Conditional, ParentStatements, LineNumber, true);
        }
    }

    internal sealed class DialogScriptDocument
    {
        private readonly Dictionary<string, List<DialogScriptStatement>> sections = new(StringComparer.Ordinal);
        private readonly List<DialogScriptDiagnostic> diagnostics = new();
        private readonly List<DialogScriptPauseStatement> pauses = new();
        private readonly List<DialogScriptVariableDeclaration> variables = new();
        private readonly HashSet<string> variableNames = new(StringComparer.Ordinal);

        public List<DialogScriptStatement> MainStatements { get; } = new();
        public IReadOnlyList<DialogScriptDiagnostic> Diagnostics => diagnostics;
        public IReadOnlyList<DialogScriptPauseStatement> Pauses => pauses;
        public IReadOnlyList<DialogScriptVariableDeclaration> Variables => variables;

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
            {
                foreach (DialogScriptStatement nested in FlattenStatement(statement))
                    yield return nested;
            }

            foreach (List<DialogScriptStatement> sectionStatements in sections.Values)
            {
                foreach (DialogScriptStatement statement in sectionStatements)
                {
                    foreach (DialogScriptStatement nested in FlattenStatement(statement))
                        yield return nested;
                }
            }
        }

        static IEnumerable<DialogScriptStatement> FlattenStatement(DialogScriptStatement statement)
        {
            yield return statement;

            if (statement is not DialogScriptConditionalStatement conditional || conditional.UsesSectionTargets)
                yield break;

            foreach (DialogScriptStatement nested in conditional.TrueStatements)
            {
                foreach (DialogScriptStatement flattened in FlattenStatement(nested))
                    yield return flattened;
            }

            foreach (DialogScriptStatement nested in conditional.FalseStatements)
            {
                foreach (DialogScriptStatement flattened in FlattenStatement(nested))
                    yield return flattened;
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

        public bool AddVariable(DialogScriptVariableDeclaration variable)
        {
            if (variable == null || string.IsNullOrWhiteSpace(variable.Name))
                return false;

            if (!variableNames.Add(variable.Name))
                return false;

            variables.Add(variable);
            return true;
        }

        public VariablesConfig CreateValidationVariablesConfig()
        {
            VariablesConfig variablesConfig = ScriptableObject.CreateInstance<VariablesConfig>();

            foreach (DialogScriptVariableDeclaration declaration in variables)
            {
                var variable = new Variable(declaration.Name, declaration.Type, false);

                switch (declaration.Type)
                {
                    case VariableType.Bool:
                        variable.SetValue(declaration.BoolValue);
                        break;
                    case VariableType.Int:
                        variable.SetValue(declaration.IntValue);
                        break;
                    case VariableType.Float:
                        variable.SetValue(declaration.FloatValue);
                        break;
                    case VariableType.String:
                        variable.SetValue(declaration.StringValue);
                        break;
                }

                variablesConfig.AddVariable(variable);
            }

            return variablesConfig;
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

    internal sealed class DialogScriptConditionalStatement : DialogScriptStatement
    {
        public DialogScriptConditionalStatement(string conditionExpression, int lineNumber)
            : base(lineNumber)
        {
            ConditionExpression = conditionExpression ?? string.Empty;
            TrueStatements = new List<DialogScriptStatement>();
            FalseStatements = new List<DialogScriptStatement>();
        }

        public DialogScriptConditionalStatement(
            string conditionExpression,
            string trueTargetSection,
            string falseTargetSection,
            int lineNumber)
            : this(conditionExpression, lineNumber)
        {
            TrueTargetSection = trueTargetSection ?? string.Empty;
            FalseTargetSection = falseTargetSection ?? string.Empty;
            UsesSectionTargets = true;
        }

        public string ConditionExpression { get; }
        public List<DialogScriptStatement> TrueStatements { get; }
        public List<DialogScriptStatement> FalseStatements { get; }
        public bool UsesSectionTargets { get; }
        public string TrueTargetSection { get; }
        public string FalseTargetSection { get; }
    }

    internal sealed class DialogScriptChoiceOption
    {
        public DialogScriptChoiceOption(string text, string targetSection, int lineNumber, string conditionExpression = "")
        {
            Text = text ?? string.Empty;
            TargetSection = targetSection ?? string.Empty;
            ConditionExpression = conditionExpression ?? string.Empty;
            LineNumber = lineNumber;
        }

        public string Text { get; }
        public string TargetSection { get; }
        public string ConditionExpression { get; }
        public int LineNumber { get; }
    }

    internal sealed class DialogScriptVariableDeclaration
    {
        public DialogScriptVariableDeclaration(
            string name,
            VariableType type,
            bool boolValue,
            int intValue,
            float floatValue,
            string stringValue,
            int lineNumber)
        {
            Name = name ?? string.Empty;
            Type = type;
            BoolValue = boolValue;
            IntValue = intValue;
            FloatValue = floatValue;
            StringValue = stringValue ?? string.Empty;
            LineNumber = lineNumber;
        }

        public string Name { get; }
        public VariableType Type { get; }
        public bool BoolValue { get; }
        public int IntValue { get; }
        public float FloatValue { get; }
        public string StringValue { get; }
        public int LineNumber { get; }
    }

    internal readonly struct BuildSequenceResult
    {
        readonly FlowExit[] exits;

        public BuildSequenceResult(GtkNode first, IEnumerable<FlowExit> exits)
        {
            First = first;
            this.exits = exits?.ToArray() ?? Array.Empty<FlowExit>();
        }

        public GtkNode First { get; }
        public IReadOnlyList<FlowExit> Exits => exits ?? Array.Empty<FlowExit>();

        public static BuildSequenceResult Empty => new(null, Array.Empty<FlowExit>());

        public static BuildSequenceResult Single(GtkNode node, string portName) =>
            node == null
                ? Empty
                : new BuildSequenceResult(node, new[] { new FlowExit(node, portName) });
    }

    internal readonly struct FlowExit
    {
        public FlowExit(GtkNode node, string portName)
        {
            Node = node;
            PortName = portName ?? string.Empty;
        }

        public GtkNode Node { get; }
        public string PortName { get; }
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
