using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEngine;
using RuntimeNode = cherrydev.Node;

namespace cherrydev.Editor.GraphToolkit
{
    internal static class DialogGraphCompiler
    {
        private const string CompileMenuPath = "Tools/Dialog System/Compile Selected Graph Toolkit Dialog Graphs";
        private const string AuthoringGraphsDirectoryName = "AuthoringGraphs";
        private const string RuntimeGraphsDirectoryName = "RuntimeGraphs";

        [MenuItem(CompileMenuPath)]
        private static void CompileSelectedGraphs()
        {
            List<string> graphPaths = GetSelectedGraphPaths().ToList();

            if (graphPaths.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "Compile Dialog Graph",
                    "Select one or more .dialoggtk assets in the Project window.",
                    "OK");
                return;
            }

            List<string> compiledPaths = new();

            foreach (string graphPath in graphPaths)
            {
                try
                {
                    DialogNodeGraph runtimeGraph = CompileToRuntimeAsset(graphPath);
                    compiledPaths.Add(AssetDatabase.GetAssetPath(runtimeGraph));
                }
                catch (Exception exception)
                {
                    Debug.LogError($"Failed to compile dialog graph '{graphPath}': {exception.Message}");
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog(
                "Compile Dialog Graph",
                compiledPaths.Count == 0
                    ? "No dialog graphs were compiled. Check the Console for validation errors."
                    : $"Compiled {compiledPaths.Count} dialog graph(s).",
                "OK");
        }

        [MenuItem(CompileMenuPath, true)]
        private static bool CanCompileSelectedGraphs() => GetSelectedGraphPaths().Any();

        public static DialogNodeGraph CompileToRuntimeAsset(string graphPath) =>
            CompileToRuntimeAsset(graphPath, default);

        internal static DialogNodeGraph CompileToRuntimeAsset(
            string graphPath,
            DialogCompilerInputMetadata inputMetadata)
        {
            DialogAuthoringGraph liveGraph = GraphDatabase.LoadGraph<DialogAuthoringGraph>(graphPath);

            if (liveGraph != null)
                GraphDatabase.SaveGraphIfDirty(liveGraph);

            DialogAuthoringGraph authoringGraph = GraphDatabase.LoadGraphForImporter<DialogAuthoringGraph>(graphPath)
                ?? liveGraph;

            if (authoringGraph == null)
                throw new InvalidOperationException($"Could not load DialogAuthoringGraph at '{graphPath}'.");

            IReadOnlyList<DialogGraphIssue> issues = DialogGraphValidator.Validate(authoringGraph);
            List<DialogGraphIssue> errors = issues.Where(issue => issue.IsError).ToList();

            if (errors.Count > 0)
            {
                foreach (DialogGraphIssue error in errors)
                    Debug.LogError(error.Message);

                throw new InvalidOperationException($"Graph has {errors.Count} validation error(s).");
            }

            string runtimePath = GetRuntimeAssetPath(graphPath);
            DialogNodeGraph runtimeGraph = GetOrCreateRuntimeGraph(runtimePath);
            string authoringGraphHash = DialogCompilerMetadata.ComputeFileHash(graphPath);

            if (IsRuntimeGraphCurrent(runtimeGraph, authoringGraphHash))
            {
                if (inputMetadata.HasValue &&
                    ApplyCompilerMetadataIfChanged(runtimeGraph, inputMetadata, authoringGraphHash))
                {
                    AssetDatabase.SaveAssets();
                }

                return runtimeGraph;
            }

            Dictionary<INode, RuntimeNode> nodeMap = new();
            List<RuntimeNode> runtimeNodes = new();
            Dictionary<string, RuntimeNode> reusableNodes = LoadReusableRuntimeNodes(runtimePath);
            HashSet<RuntimeNode> usedNodes = new();
            DialogCompilationSettings settings = DialogCompilationSettings.FromGraph(authoringGraph);
            int nodeIndex = 0;

            foreach (INode authoringNode in authoringGraph.GetNodes())
            {
                if (authoringNode is DialogStartNode || authoringNode is DialogGraphSettingsNode)
                    continue;

                string sourceKey = GetRuntimeSourceKey(authoringNode, nodeIndex);
                RuntimeNode runtimeNode = CreateRuntimeNode(authoringNode, runtimeGraph, sourceKey, reusableNodes);

                if (runtimeNode == null)
                    continue;

                nodeMap.Add(authoringNode, runtimeNode);
                runtimeNodes.Add(runtimeNode);
                usedNodes.Add(runtimeNode);
                nodeIndex++;
            }

            INode startTarget = GetStartTarget(authoringGraph);

            if (startTarget != null && nodeMap.TryGetValue(startTarget, out RuntimeNode entryRuntimeNode))
            {
                runtimeNodes.Remove(entryRuntimeNode);
                runtimeNodes.Insert(0, entryRuntimeNode);
            }

            WireRuntimeGraph(authoringGraph, nodeMap);
            RemoveUnusedRuntimeNodeSubAssets(runtimePath, usedNodes);

            DialogCompilerInputMetadata effectiveInputMetadata = inputMetadata.HasValue
                ? inputMetadata
                : DialogCompilerMetadata.ForAuthoringGraph(graphPath);

            runtimeGraph.ConfigureRuntimeGraph(
                runtimeNodes,
                settings.VariablesConfig,
                settings.LocalizationTableName,
                settings.CharacterNamesLocalizationName,
                AssetDatabase.AssetPathToGUID(graphPath));
            runtimeGraph.ConfigureCompilerMetadata(
                DialogCompilerMetadata.SchemaVersion,
                effectiveInputMetadata.InputKind,
                effectiveInputMetadata.InputHash,
                authoringGraphHash);

            EditorUtility.SetDirty(runtimeGraph);

            foreach (RuntimeNode runtimeNode in runtimeNodes)
                EditorUtility.SetDirty(runtimeNode);

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(runtimePath);
            return runtimeGraph;
        }

        private static IEnumerable<string> GetSelectedGraphPaths()
        {
            foreach (string guid in Selection.assetGUIDs)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);

                if (path.EndsWith($".{DialogAuthoringGraph.AssetExtension}", StringComparison.OrdinalIgnoreCase))
                    yield return path;
            }
        }

        private static DialogNodeGraph GetOrCreateRuntimeGraph(string runtimePath)
        {
            DialogNodeGraph runtimeGraph = AssetDatabase.LoadAssetAtPath<DialogNodeGraph>(runtimePath);

            if (runtimeGraph != null)
                return runtimeGraph;

            runtimeGraph = ScriptableObject.CreateInstance<DialogNodeGraph>();
            runtimeGraph.name = Path.GetFileNameWithoutExtension(runtimePath);
            AssetDatabase.CreateAsset(runtimeGraph, runtimePath);
            return runtimeGraph;
        }

        private static bool IsRuntimeGraphCurrent(DialogNodeGraph runtimeGraph, string authoringGraphHash) =>
            runtimeGraph != null &&
            runtimeGraph.CompilerSchemaVersion == DialogCompilerMetadata.SchemaVersion &&
            string.Equals(runtimeGraph.CompilerAuthoringGraphHash, authoringGraphHash, StringComparison.Ordinal) &&
            runtimeGraph.NodesList != null &&
            runtimeGraph.NodesList.All(node => node != null);

        private static bool ApplyCompilerMetadataIfChanged(
            DialogNodeGraph runtimeGraph,
            DialogCompilerInputMetadata inputMetadata,
            string authoringGraphHash)
        {
            if (runtimeGraph.CompilerSchemaVersion == DialogCompilerMetadata.SchemaVersion &&
                string.Equals(runtimeGraph.CompilerInputKind, inputMetadata.InputKind, StringComparison.Ordinal) &&
                string.Equals(runtimeGraph.CompilerInputHash, inputMetadata.InputHash, StringComparison.Ordinal) &&
                string.Equals(runtimeGraph.CompilerAuthoringGraphHash, authoringGraphHash, StringComparison.Ordinal))
            {
                return false;
            }

            runtimeGraph.ConfigureCompilerMetadata(
                DialogCompilerMetadata.SchemaVersion,
                inputMetadata.InputKind,
                inputMetadata.InputHash,
                authoringGraphHash);
            EditorUtility.SetDirty(runtimeGraph);
            return true;
        }

        private static Dictionary<string, RuntimeNode> LoadReusableRuntimeNodes(string runtimePath)
        {
            var reusableNodes = new Dictionary<string, RuntimeNode>(StringComparer.Ordinal);

            foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(runtimePath))
            {
                if (asset is not RuntimeNode runtimeNode || string.IsNullOrWhiteSpace(runtimeNode.CompilerSourceKey))
                    continue;

                if (!reusableNodes.ContainsKey(runtimeNode.CompilerSourceKey))
                    reusableNodes.Add(runtimeNode.CompilerSourceKey, runtimeNode);
            }

            return reusableNodes;
        }

        private static void RemoveUnusedRuntimeNodeSubAssets(string runtimePath, HashSet<RuntimeNode> usedNodes)
        {
            foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(runtimePath))
            {
                if (asset is RuntimeNode runtimeNode && !usedNodes.Contains(runtimeNode))
                    UnityEngine.Object.DestroyImmediate(asset, true);
            }
        }

        private static RuntimeNode CreateRuntimeNode(
            INode authoringNode,
            DialogNodeGraph runtimeGraph,
            string sourceKey,
            Dictionary<string, RuntimeNode> reusableNodes)
        {
            RuntimeNode runtimeNode = null;

            if (!string.IsNullOrWhiteSpace(sourceKey) &&
                reusableNodes.TryGetValue(sourceKey, out RuntimeNode reusableNode) &&
                IsCompatibleRuntimeNode(authoringNode, reusableNode))
            {
                runtimeNode = reusableNode;
                ResetRuntimeNodeConnections(runtimeNode);
            }

            runtimeNode = authoringNode switch
            {
                DialogSentenceNode sentenceNode => ConfigureSentenceNode(sentenceNode, runtimeNode as SentenceNode),
                DialogAnswerNode answerNode => ConfigureAnswerNode(answerNode, runtimeNode as AnswerNode),
                DialogExternalFunctionNode externalFunctionNode => ConfigureExternalFunctionNode(
                    externalFunctionNode,
                    runtimeNode as ExternalFunctionNode),
                DialogModifyVariableNode modifyVariableNode => ConfigureModifyVariableNode(
                    modifyVariableNode,
                    runtimeNode as ModifyVariableNode),
                DialogVariableConditionNode conditionNode => ConfigureVariableConditionNode(
                    conditionNode,
                    runtimeNode as VariableConditionNode),
                DialogGraphSettingsNode => null,
                _ => null
            };

            if (runtimeNode == null)
                return null;

            runtimeNode.name = authoringNode.GetType().Name.Replace("Dialog", string.Empty).Replace("Node", " Node");
            runtimeNode.AssignNodeGraph(runtimeGraph);
            runtimeNode.AssignCompilerSourceKey(sourceKey);

            if (!AssetDatabase.Contains(runtimeNode))
                AssetDatabase.AddObjectToAsset(runtimeNode, runtimeGraph);

            return runtimeNode;
        }

        private static bool IsCompatibleRuntimeNode(INode authoringNode, RuntimeNode runtimeNode) =>
            authoringNode switch
            {
                DialogSentenceNode => runtimeNode is SentenceNode,
                DialogAnswerNode => runtimeNode is AnswerNode,
                DialogExternalFunctionNode => runtimeNode is ExternalFunctionNode,
                DialogModifyVariableNode => runtimeNode is ModifyVariableNode,
                DialogVariableConditionNode => runtimeNode is VariableConditionNode,
                _ => false
            };

        private static void ResetRuntimeNodeConnections(RuntimeNode runtimeNode)
        {
            switch (runtimeNode)
            {
                case SentenceNode sentenceNode:
                    sentenceNode.ParentNodes ??= new List<RuntimeNode>();
                    sentenceNode.ParentNodes.Clear();
                    sentenceNode.ChildNode = null;
                    break;
                case AnswerNode answerNode:
                    answerNode.ParentNodes ??= new List<RuntimeNode>();
                    answerNode.ChildNodes ??= new List<RuntimeNode>();
                    answerNode.ParentNodes.Clear();
                    answerNode.ChildNodes.Clear();
                    break;
                case ExternalFunctionNode externalFunctionNode:
                    externalFunctionNode.ParentNodes ??= new List<RuntimeNode>();
                    externalFunctionNode.ParentNodes.Clear();
                    externalFunctionNode.ChildNode = null;
                    break;
                case ModifyVariableNode modifyVariableNode:
                    modifyVariableNode.ParentNodes ??= new List<RuntimeNode>();
                    modifyVariableNode.ParentNodes.Clear();
                    modifyVariableNode.ChildNode = null;
                    break;
                case VariableConditionNode conditionNode:
                    conditionNode.ParentNodes ??= new List<RuntimeNode>();
                    conditionNode.ParentNodes.Clear();
                    conditionNode.TrueChildNode = null;
                    conditionNode.FalseChildNode = null;
                    break;
            }
        }

        private static SentenceNode ConfigureSentenceNode(DialogSentenceNode authoringNode, SentenceNode runtimeNode)
        {
            runtimeNode ??= ScriptableObject.CreateInstance<SentenceNode>();
            Sentence sentence = new(
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.CharacterName, string.Empty),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.SentenceText, string.Empty))
            {
                CharacterSprite = DialogGraphOptionReader.Read<Sprite>(authoringNode, DialogGraphOptions.CharacterSprite, null)
            };

            runtimeNode.Configure(
                sentence,
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.UseInlineExternalFunction, false),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.InlineExternalFunctionName, string.Empty),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.CharacterNameKey, string.Empty),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.SentenceTextKey, string.Empty));

            return runtimeNode;
        }

        private static AnswerNode ConfigureAnswerNode(DialogAnswerNode authoringNode, AnswerNode runtimeNode)
        {
            runtimeNode ??= ScriptableObject.CreateInstance<AnswerNode>();
            int answerCount = DialogGraphValidator.GetAnswerCount(authoringNode);
            List<string> answers = new();
            List<string> answerKeys = new();
            List<string> answerConditions = new();

            for (int i = 0; i < answerCount; i++)
            {
                answers.Add(DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.AnswerTextPrefix + i, string.Empty));
                answerKeys.Add(DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.AnswerKeyPrefix + i, string.Empty));
                answerConditions.Add(DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.AnswerConditionPrefix + i, string.Empty));
            }

            runtimeNode.Configure(answers, answerKeys, answerConditions);
            return runtimeNode;
        }

        private static ExternalFunctionNode ConfigureExternalFunctionNode(
            DialogExternalFunctionNode authoringNode,
            ExternalFunctionNode runtimeNode)
        {
            runtimeNode ??= ScriptableObject.CreateInstance<ExternalFunctionNode>();
            runtimeNode.Configure(
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.FunctionName, string.Empty),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.FunctionDescription, string.Empty));
            return runtimeNode;
        }

        private static ModifyVariableNode ConfigureModifyVariableNode(
            DialogModifyVariableNode authoringNode,
            ModifyVariableNode runtimeNode)
        {
            runtimeNode ??= ScriptableObject.CreateInstance<ModifyVariableNode>();
            runtimeNode.Configure(
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.VariableName, string.Empty),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.ModificationType, ModificationType.Set),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.BoolValue, false),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.IntValue, 0),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.FloatValue, 0f),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.StringValue, string.Empty));
            return runtimeNode;
        }

        private static VariableConditionNode ConfigureVariableConditionNode(
            DialogVariableConditionNode authoringNode,
            VariableConditionNode runtimeNode)
        {
            runtimeNode ??= ScriptableObject.CreateInstance<VariableConditionNode>();
            string conditionExpression = DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.ConditionExpression, string.Empty);

            if (!string.IsNullOrWhiteSpace(conditionExpression))
            {
                runtimeNode.ConfigureExpression(conditionExpression);
                return runtimeNode;
            }

            runtimeNode.Configure(
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.VariableName, string.Empty),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.ConditionType, ConditionType.Equal),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.BoolValue, false),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.IntValue, 0),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.FloatValue, 0f),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.StringValue, string.Empty));
            return runtimeNode;
        }

        private static string GetRuntimeSourceKey(INode authoringNode, int nodeIndex)
        {
            if (authoringNode is Unity.GraphToolkit.Editor.Node graphToolkitNode)
            {
                string compilerSourceKey = DialogGraphOptionReader.Read(
                    graphToolkitNode,
                    DialogGraphOptions.CompilerSourceKey,
                    string.Empty);

                if (!string.IsNullOrWhiteSpace(compilerSourceKey))
                    return compilerSourceKey;
            }

            string graphToolkitGuid = TryGetGraphToolkitNodeGuid(authoringNode);
            return !string.IsNullOrWhiteSpace(graphToolkitGuid)
                ? $"graph:{graphToolkitGuid}"
                : $"graph:{nodeIndex:D4}:{authoringNode.GetType().FullName}";
        }

        private static string TryGetGraphToolkitNodeGuid(INode authoringNode)
        {
            if (authoringNode is not Unity.GraphToolkit.Editor.Node graphToolkitNode)
                return string.Empty;

            FieldInfo implementationField = typeof(Unity.GraphToolkit.Editor.Node)
                .GetField("m_Implementation", BindingFlags.Instance | BindingFlags.NonPublic);
            object implementation = implementationField?.GetValue(graphToolkitNode);
            PropertyInfo guidProperty = implementation?.GetType()
                .GetProperty("Guid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object guid = guidProperty?.GetValue(implementation);
            return guid?.ToString() ?? string.Empty;
        }

        private static void WireRuntimeGraph(DialogAuthoringGraph authoringGraph, Dictionary<INode, RuntimeNode> nodeMap)
        {
            foreach (INode authoringNode in authoringGraph.GetNodes())
            {
                if (!nodeMap.TryGetValue(authoringNode, out RuntimeNode sourceRuntimeNode))
                    continue;

                switch (authoringNode)
                {
                    case DialogAnswerNode answerNode:
                        WireAnswerNode(answerNode, (AnswerNode)sourceRuntimeNode, nodeMap);
                        break;
                    case DialogVariableConditionNode conditionNode:
                        WireConditionNode(conditionNode, (VariableConditionNode)sourceRuntimeNode, nodeMap);
                        break;
                    default:
                        WireSingleChild(authoringNode, sourceRuntimeNode, DialogGraphPorts.Next, nodeMap);
                        break;
                }
            }
        }

        private static void WireSingleChild(
            INode authoringNode,
            RuntimeNode sourceRuntimeNode,
            string portName,
            Dictionary<INode, RuntimeNode> nodeMap)
        {
            RuntimeNode child = GetSingleRuntimeTarget(authoringNode, portName, nodeMap);

            if (child == null)
                return;

            switch (sourceRuntimeNode)
            {
                case SentenceNode sentenceNode:
                    sentenceNode.ChildNode = child;
                    break;
                case ExternalFunctionNode externalFunctionNode:
                    externalFunctionNode.ChildNode = child;
                    break;
                case ModifyVariableNode modifyVariableNode:
                    modifyVariableNode.ChildNode = child;
                    break;
            }

            AddParent(child, sourceRuntimeNode);
        }

        private static void WireAnswerNode(
            DialogAnswerNode authoringNode,
            AnswerNode sourceRuntimeNode,
            Dictionary<INode, RuntimeNode> nodeMap)
        {
            int answerCount = DialogGraphValidator.GetAnswerCount(authoringNode);
            sourceRuntimeNode.EnsureChildSlots(answerCount);

            for (int i = 0; i < answerCount; i++)
            {
                RuntimeNode child = GetSingleRuntimeTarget(authoringNode, DialogGraphPorts.Answer(i), nodeMap);

                if (child == null)
                    continue;

                sourceRuntimeNode.ChildNodes[i] = child;
                AddParent(child, sourceRuntimeNode);
            }
        }

        private static void WireConditionNode(
            DialogVariableConditionNode authoringNode,
            VariableConditionNode sourceRuntimeNode,
            Dictionary<INode, RuntimeNode> nodeMap)
        {
            RuntimeNode trueChild = GetSingleRuntimeTarget(authoringNode, DialogGraphPorts.True, nodeMap);
            RuntimeNode falseChild = GetSingleRuntimeTarget(authoringNode, DialogGraphPorts.False, nodeMap);

            sourceRuntimeNode.TrueChildNode = trueChild;
            sourceRuntimeNode.FalseChildNode = falseChild;

            if (trueChild != null)
                AddParent(trueChild, sourceRuntimeNode);

            if (falseChild != null)
                AddParent(falseChild, sourceRuntimeNode);
        }

        private static RuntimeNode GetSingleRuntimeTarget(
            INode authoringNode,
            string portName,
            Dictionary<INode, RuntimeNode> nodeMap)
        {
            INode target = DialogGraphValidator.GetConnectedTargets(authoringNode, portName).FirstOrDefault();
            return target != null && nodeMap.TryGetValue(target, out RuntimeNode runtimeNode) ? runtimeNode : null;
        }

        private static INode GetStartTarget(DialogAuthoringGraph graph)
        {
            DialogStartNode startNode = graph.GetNodes().OfType<DialogStartNode>().FirstOrDefault();
            return startNode == null
                ? null
                : DialogGraphValidator.GetConnectedTargets(startNode, DialogGraphPorts.Next).FirstOrDefault();
        }

        private static void AddParent(RuntimeNode child, RuntimeNode parent)
        {
            switch (child)
            {
                case SentenceNode sentenceNode:
                    AddUnique(sentenceNode.ParentNodes, parent);
                    break;
                case AnswerNode answerNode:
                    AddUnique(answerNode.ParentNodes, parent);
                    break;
                case ExternalFunctionNode externalFunctionNode:
                    AddUnique(externalFunctionNode.ParentNodes, parent);
                    break;
                case ModifyVariableNode modifyVariableNode:
                    AddUnique(modifyVariableNode.ParentNodes, parent);
                    break;
                case VariableConditionNode conditionNode:
                    AddUnique(conditionNode.ParentNodes, parent);
                    break;
            }
        }

        private static void AddUnique(List<RuntimeNode> nodes, RuntimeNode node)
        {
            if (!nodes.Contains(node))
                nodes.Add(node);
        }

        internal static string GetRuntimeAssetPath(string graphPath)
        {
            string graphName = Path.GetFileNameWithoutExtension(graphPath);
            string directory = GetRuntimeAssetDirectory(graphPath);
            return $"{directory}/{graphName}_Runtime.asset";
        }

        private static string GetRuntimeAssetDirectory(string graphPath)
        {
            string assetDirectory = Path.GetDirectoryName(graphPath)?.Replace("\\", "/");

            if (!string.IsNullOrEmpty(assetDirectory) &&
                string.Equals(Path.GetFileName(assetDirectory), AuthoringGraphsDirectoryName, StringComparison.Ordinal))
            {
                string parentDirectory = Path.GetDirectoryName(assetDirectory)?.Replace("\\", "/");

                if (!string.IsNullOrEmpty(parentDirectory))
                {
                    string runtimeDirectory = $"{parentDirectory}/{RuntimeGraphsDirectoryName}";
                    EnsureAssetDirectory(runtimeDirectory);
                    return runtimeDirectory;
                }
            }

            return GetWritableAssetDirectory(graphPath, "Compiled");
        }

        private static string GetWritableAssetDirectory(string assetPath, string fallbackSubdirectory)
        {
            if (!string.IsNullOrEmpty(assetPath) && assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                string assetDirectory = Path.GetDirectoryName(assetPath)?.Replace("\\", "/");

                if (!string.IsNullOrEmpty(assetDirectory))
                {
                    EnsureAssetDirectory(assetDirectory);
                    return assetDirectory;
                }
            }

            string fallbackDirectory = $"Assets/DialogNodeBasedSystem/{fallbackSubdirectory}";
            EnsureAssetDirectory(fallbackDirectory);
            return fallbackDirectory;
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

        private readonly struct DialogCompilationSettings
        {
            private DialogCompilationSettings(
                VariablesConfig variablesConfig,
                string localizationTableName,
                string characterNamesLocalizationName)
            {
                VariablesConfig = variablesConfig;
                LocalizationTableName = localizationTableName;
                CharacterNamesLocalizationName = characterNamesLocalizationName;
            }

            public VariablesConfig VariablesConfig { get; }
            public string LocalizationTableName { get; }
            public string CharacterNamesLocalizationName { get; }

            public static DialogCompilationSettings FromGraph(DialogAuthoringGraph graph)
            {
                DialogGraphSettingsNode settingsNode = graph.GetNodes().OfType<DialogGraphSettingsNode>().FirstOrDefault();

                if (settingsNode == null)
                {
                    return new DialogCompilationSettings(
                        graph.VariablesConfig,
                        graph.LocalizationTableName,
                        graph.CharacterNamesLocalizationName);
                }

                return new DialogCompilationSettings(
                    DialogGraphOptionReader.Read<VariablesConfig>(settingsNode, DialogGraphOptions.VariablesConfig, graph.VariablesConfig),
                    DialogGraphOptionReader.Read(settingsNode, DialogGraphOptions.LocalizationTableName, graph.LocalizationTableName),
                    DialogGraphOptionReader.Read(settingsNode, DialogGraphOptions.CharacterNamesLocalizationName, graph.CharacterNamesLocalizationName));
            }
        }
    }
}
