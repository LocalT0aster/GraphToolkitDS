using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEngine;
using RuntimeNode = cherrydev.Node;

namespace cherrydev.Editor.GraphToolkit
{
    internal static class DialogGraphCompiler
    {
        private const string CompileMenuPath = "Tools/Dialog System/Compile Selected Graph Toolkit Dialog Graphs";

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

        public static DialogNodeGraph CompileToRuntimeAsset(string graphPath)
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
            RemoveRuntimeNodeSubAssets(runtimePath);

            Dictionary<INode, RuntimeNode> nodeMap = new();
            List<RuntimeNode> runtimeNodes = new();
            DialogCompilationSettings settings = DialogCompilationSettings.FromGraph(authoringGraph);

            foreach (INode authoringNode in authoringGraph.GetNodes())
            {
                if (authoringNode is DialogStartNode || authoringNode is DialogGraphSettingsNode)
                    continue;

                RuntimeNode runtimeNode = CreateRuntimeNode(authoringNode, runtimeGraph);

                if (runtimeNode == null)
                    continue;

                nodeMap.Add(authoringNode, runtimeNode);
                runtimeNodes.Add(runtimeNode);
                AssetDatabase.AddObjectToAsset(runtimeNode, runtimeGraph);
            }

            INode startTarget = GetStartTarget(authoringGraph);

            if (startTarget != null && nodeMap.TryGetValue(startTarget, out RuntimeNode entryRuntimeNode))
            {
                runtimeNodes.Remove(entryRuntimeNode);
                runtimeNodes.Insert(0, entryRuntimeNode);
            }

            WireRuntimeGraph(authoringGraph, nodeMap);

            runtimeGraph.ConfigureRuntimeGraph(
                runtimeNodes,
                settings.VariablesConfig,
                settings.LocalizationTableName,
                settings.CharacterNamesLocalizationName,
                AssetDatabase.AssetPathToGUID(graphPath));

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

        private static void RemoveRuntimeNodeSubAssets(string runtimePath)
        {
            foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(runtimePath))
            {
                if (asset is RuntimeNode)
                    UnityEngine.Object.DestroyImmediate(asset, true);
            }
        }

        private static RuntimeNode CreateRuntimeNode(INode authoringNode, DialogNodeGraph runtimeGraph)
        {
            RuntimeNode runtimeNode = authoringNode switch
            {
                DialogSentenceNode sentenceNode => CreateSentenceNode(sentenceNode),
                DialogAnswerNode answerNode => CreateAnswerNode(answerNode),
                DialogExternalFunctionNode externalFunctionNode => CreateExternalFunctionNode(externalFunctionNode),
                DialogModifyVariableNode modifyVariableNode => CreateModifyVariableNode(modifyVariableNode),
                DialogVariableConditionNode conditionNode => CreateVariableConditionNode(conditionNode),
                DialogGraphSettingsNode => null,
                _ => null
            };

            if (runtimeNode == null)
                return null;

            runtimeNode.name = authoringNode.GetType().Name.Replace("Dialog", string.Empty).Replace("Node", " Node");
            runtimeNode.AssignNodeGraph(runtimeGraph);
            return runtimeNode;
        }

        private static SentenceNode CreateSentenceNode(DialogSentenceNode authoringNode)
        {
            SentenceNode runtimeNode = ScriptableObject.CreateInstance<SentenceNode>();
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

        private static AnswerNode CreateAnswerNode(DialogAnswerNode authoringNode)
        {
            AnswerNode runtimeNode = ScriptableObject.CreateInstance<AnswerNode>();
            int answerCount = DialogGraphValidator.GetAnswerCount(authoringNode);
            List<string> answers = new();
            List<string> answerKeys = new();

            for (int i = 0; i < answerCount; i++)
            {
                answers.Add(DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.AnswerTextPrefix + i, string.Empty));
                answerKeys.Add(DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.AnswerKeyPrefix + i, string.Empty));
            }

            runtimeNode.Configure(answers, answerKeys);
            return runtimeNode;
        }

        private static ExternalFunctionNode CreateExternalFunctionNode(DialogExternalFunctionNode authoringNode)
        {
            ExternalFunctionNode runtimeNode = ScriptableObject.CreateInstance<ExternalFunctionNode>();
            runtimeNode.Configure(
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.FunctionName, string.Empty),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.FunctionDescription, string.Empty));
            return runtimeNode;
        }

        private static ModifyVariableNode CreateModifyVariableNode(DialogModifyVariableNode authoringNode)
        {
            ModifyVariableNode runtimeNode = ScriptableObject.CreateInstance<ModifyVariableNode>();
            runtimeNode.Configure(
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.VariableName, string.Empty),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.ModificationType, ModificationType.Set),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.BoolValue, false),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.IntValue, 0),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.FloatValue, 0f),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.StringValue, string.Empty));
            return runtimeNode;
        }

        private static VariableConditionNode CreateVariableConditionNode(DialogVariableConditionNode authoringNode)
        {
            VariableConditionNode runtimeNode = ScriptableObject.CreateInstance<VariableConditionNode>();
            runtimeNode.Configure(
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.VariableName, string.Empty),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.ConditionType, ConditionType.Equal),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.BoolValue, false),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.IntValue, 0),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.FloatValue, 0f),
                DialogGraphOptionReader.Read(authoringNode, DialogGraphOptions.StringValue, string.Empty));
            return runtimeNode;
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

        private static string GetRuntimeAssetPath(string graphPath)
        {
            string directory = Path.GetDirectoryName(graphPath);
            string graphName = Path.GetFileNameWithoutExtension(graphPath);
            return $"{directory}/{graphName}_Runtime.asset";
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
