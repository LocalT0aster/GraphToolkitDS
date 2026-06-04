using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using cherrydev.Editor.GraphToolkit;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEngine;
using RuntimeNode = cherrydev.Node;

namespace cherrydev.Editor.Migration
{
    public static class LegacyGraphMigrationTool
    {
        private const string MenuPath = "Tools/Dialog System/Migrate Legacy Dialog Graphs";

        [MenuItem(MenuPath)]
        private static void MigrateSelectedGraphs()
        {
            List<DialogNodeGraph> graphs = Selection.objects.OfType<DialogNodeGraph>().ToList();

            if (graphs.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "Migrate Legacy Dialog Graphs",
                    "Select one or more legacy DialogNodeGraph assets.",
                    "OK");
                return;
            }

            List<MigrationArtifacts> artifacts = graphs.Select(MigrateGraph).ToList();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog(
                "Migrate Legacy Dialog Graphs",
                $"Created migration artifacts for {artifacts.Count} graph(s).",
                "OK");
        }

        [MenuItem(MenuPath, true)]
        private static bool CanMigrateSelectedGraphs() =>
            Selection.objects.Any(selection => selection is DialogNodeGraph);

        public static MigrationArtifacts MigrateGraphAtPath(string legacyGraphPath)
        {
            DialogNodeGraph graph = AssetDatabase.LoadAssetAtPath<DialogNodeGraph>(legacyGraphPath);

            if (graph == null)
                throw new InvalidOperationException($"No DialogNodeGraph found at '{legacyGraphPath}'.");

            return MigrateGraph(graph);
        }

        public static MigrationArtifacts MigrateGraph(DialogNodeGraph graph)
        {
            if (graph == null)
                throw new ArgumentNullException(nameof(graph));

            string legacyPath = AssetDatabase.GetAssetPath(graph);
            string directory = Path.GetDirectoryName(legacyPath)?.Replace("\\", "/");
            string graphName = Path.GetFileNameWithoutExtension(legacyPath);
            string authoringGraphPath = AssetDatabase.GenerateUniqueAssetPath($"{directory}/{graphName}.{DialogAuthoringGraph.AssetExtension}");
            string manifestPath = AssetDatabase.GenerateUniqueAssetPath($"{directory}/{graphName}.dialogmigration.json");

            GraphDatabase.CreateGraph<DialogAuthoringGraph>(authoringGraphPath);
            ConfigureAuthoringGraph(authoringGraphPath, graph);

            LegacyMigrationManifest manifest = BuildManifest(graph, legacyPath, authoringGraphPath);
            DialogNodeGraph runtimeGraph = CreateRuntimeAssetFromLegacy(graph, manifest.targetRuntimeGraphPath, authoringGraphPath);

            File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, true));
            AssetDatabase.ImportAsset(manifestPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string runtimePath = AssetDatabase.GetAssetPath(runtimeGraph);
            Debug.Log(
                $"Migrated legacy dialog graph '{legacyPath}' to authoring graph '{authoringGraphPath}', " +
                $"runtime graph '{runtimePath}', and manifest '{manifestPath}'.");

            return new MigrationArtifacts(legacyPath, authoringGraphPath, runtimePath, manifestPath);
        }

        private static void ConfigureAuthoringGraph(string authoringGraphPath, DialogNodeGraph legacyGraph)
        {
            DialogAuthoringGraph authoringGraph = GraphDatabase.LoadGraph<DialogAuthoringGraph>(authoringGraphPath)
                ?? GraphDatabase.LoadGraphForImporter<DialogAuthoringGraph>(authoringGraphPath);

            if (authoringGraph == null)
                return;

            authoringGraph.ConfigureMigratedSettings(
                legacyGraph.VariablesConfig,
                legacyGraph.LocalizationTableName,
                legacyGraph.CharacterNamesLocalizationName);

            GraphDatabase.SaveGraphIfDirty(authoringGraph);
        }

        private static DialogNodeGraph CreateRuntimeAssetFromLegacy(
            DialogNodeGraph legacyGraph,
            string runtimePath,
            string authoringGraphPath)
        {
            EnsureAssetDirectory(Path.GetDirectoryName(runtimePath)?.Replace("\\", "/"));

            DialogNodeGraph runtimeGraph = AssetDatabase.LoadAssetAtPath<DialogNodeGraph>(runtimePath);
            if (runtimeGraph == null)
            {
                runtimeGraph = ScriptableObject.CreateInstance<DialogNodeGraph>();
                runtimeGraph.name = Path.GetFileNameWithoutExtension(runtimePath);
                AssetDatabase.CreateAsset(runtimeGraph, runtimePath);
            }

            RemoveRuntimeNodeSubAssets(runtimePath);

            Dictionary<RuntimeNode, RuntimeNode> nodeMap = new();
            List<RuntimeNode> runtimeNodes = new();

            foreach (RuntimeNode legacyNode in legacyGraph.NodesList.Where(node => node != null))
            {
                RuntimeNode runtimeNode = CloneNode(legacyNode, runtimeGraph);

                if (runtimeNode == null)
                    continue;

                nodeMap.Add(legacyNode, runtimeNode);
                runtimeNodes.Add(runtimeNode);
                AssetDatabase.AddObjectToAsset(runtimeNode, runtimeGraph);
            }

            foreach (RuntimeNode legacyNode in legacyGraph.NodesList.Where(node => node != null))
                WireClonedNode(legacyNode, nodeMap);

            runtimeGraph.ConfigureRuntimeGraph(
                runtimeNodes,
                legacyGraph.VariablesConfig,
                legacyGraph.LocalizationTableName,
                legacyGraph.CharacterNamesLocalizationName,
                AssetDatabase.AssetPathToGUID(authoringGraphPath));

            EditorUtility.SetDirty(runtimeGraph);

            foreach (RuntimeNode runtimeNode in runtimeNodes)
                EditorUtility.SetDirty(runtimeNode);

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(runtimePath);
            return runtimeGraph;
        }

        private static RuntimeNode CloneNode(RuntimeNode source, DialogNodeGraph runtimeGraph)
        {
            RuntimeNode clone = source switch
            {
                SentenceNode sentenceNode => CloneSentenceNode(sentenceNode),
                AnswerNode answerNode => CloneAnswerNode(answerNode),
                ExternalFunctionNode externalFunctionNode => CloneExternalFunctionNode(externalFunctionNode),
                ModifyVariableNode modifyVariableNode => CloneModifyVariableNode(modifyVariableNode),
                VariableConditionNode conditionNode => CloneVariableConditionNode(conditionNode),
                _ => null
            };

            if (clone == null)
                return null;

            clone.name = string.IsNullOrEmpty(source.name) ? source.GetType().Name : source.name;
            clone.Rect = source.Rect;
            clone.AssignNodeGraph(runtimeGraph);
            return clone;
        }

        private static SentenceNode CloneSentenceNode(SentenceNode source)
        {
            SentenceNode clone = ScriptableObject.CreateInstance<SentenceNode>();
            Sentence sentence = new(source.Sentence.CharacterName, source.Sentence.Text)
            {
                CharacterSprite = source.Sentence.CharacterSprite
            };

            clone.Configure(
                sentence,
                source.IsExternalFunc(),
                source.GetExternalFunctionName(),
                source.CharacterNameKey,
                source.SentenceTextKey);

            return clone;
        }

        private static AnswerNode CloneAnswerNode(AnswerNode source)
        {
            AnswerNode clone = ScriptableObject.CreateInstance<AnswerNode>();
            clone.Configure(source.Answers, source.AnswerKeys);
            clone.EnsureChildSlots(Mathf.Max(source.Answers.Count, source.ChildNodes.Count));
            return clone;
        }

        private static ExternalFunctionNode CloneExternalFunctionNode(ExternalFunctionNode source)
        {
            ExternalFunctionNode clone = ScriptableObject.CreateInstance<ExternalFunctionNode>();
            clone.Configure(source.FunctionName, source.Description);
            return clone;
        }

        private static ModifyVariableNode CloneModifyVariableNode(ModifyVariableNode source)
        {
            ModifyVariableNode clone = ScriptableObject.CreateInstance<ModifyVariableNode>();
            clone.Configure(
                source.VariableName,
                source.Modification,
                source.BoolValue,
                source.IntValue,
                source.FloatValue,
                source.StringValue);
            return clone;
        }

        private static VariableConditionNode CloneVariableConditionNode(VariableConditionNode source)
        {
            VariableConditionNode clone = ScriptableObject.CreateInstance<VariableConditionNode>();
            clone.Configure(
                source.VariableName,
                source.Condition,
                source.BoolTargetValue,
                source.IntTargetValue,
                source.FloatTargetValue,
                source.StringTargetValue);
            return clone;
        }

        private static void WireClonedNode(RuntimeNode legacyNode, Dictionary<RuntimeNode, RuntimeNode> nodeMap)
        {
            if (!nodeMap.TryGetValue(legacyNode, out RuntimeNode runtimeNode))
                return;

            switch (legacyNode)
            {
                case SentenceNode legacySentence when runtimeNode is SentenceNode runtimeSentence:
                    runtimeSentence.ChildNode = GetClonedNode(legacySentence.ChildNode, nodeMap);
                    AddParent(runtimeSentence.ChildNode, runtimeSentence);
                    break;
                case ExternalFunctionNode legacyFunction when runtimeNode is ExternalFunctionNode runtimeFunction:
                    runtimeFunction.ChildNode = GetClonedNode(legacyFunction.ChildNode, nodeMap);
                    AddParent(runtimeFunction.ChildNode, runtimeFunction);
                    break;
                case ModifyVariableNode legacyModify when runtimeNode is ModifyVariableNode runtimeModify:
                    runtimeModify.ChildNode = GetClonedNode(legacyModify.ChildNode, nodeMap);
                    AddParent(runtimeModify.ChildNode, runtimeModify);
                    break;
                case VariableConditionNode legacyCondition when runtimeNode is VariableConditionNode runtimeCondition:
                    runtimeCondition.TrueChildNode = GetClonedNode(legacyCondition.TrueChildNode, nodeMap);
                    runtimeCondition.FalseChildNode = GetClonedNode(legacyCondition.FalseChildNode, nodeMap);
                    AddParent(runtimeCondition.TrueChildNode, runtimeCondition);
                    AddParent(runtimeCondition.FalseChildNode, runtimeCondition);
                    break;
                case AnswerNode legacyAnswer when runtimeNode is AnswerNode runtimeAnswer:
                    WireAnswerNode(legacyAnswer, runtimeAnswer, nodeMap);
                    break;
            }
        }

        private static void WireAnswerNode(
            AnswerNode legacyAnswer,
            AnswerNode runtimeAnswer,
            Dictionary<RuntimeNode, RuntimeNode> nodeMap)
        {
            runtimeAnswer.EnsureChildSlots(Mathf.Max(legacyAnswer.Answers.Count, legacyAnswer.ChildNodes.Count));

            for (int i = 0; i < legacyAnswer.ChildNodes.Count && i < runtimeAnswer.ChildNodes.Count; i++)
            {
                RuntimeNode child = GetClonedNode(legacyAnswer.ChildNodes[i], nodeMap);
                runtimeAnswer.ChildNodes[i] = child;
                AddParent(child, runtimeAnswer);
            }
        }

        private static RuntimeNode GetClonedNode(RuntimeNode legacyNode, Dictionary<RuntimeNode, RuntimeNode> nodeMap) =>
            legacyNode != null && nodeMap.TryGetValue(legacyNode, out RuntimeNode runtimeNode) ? runtimeNode : null;

        private static void AddParent(RuntimeNode child, RuntimeNode parent)
        {
            if (child == null || parent == null)
                return;

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

        private static void RemoveRuntimeNodeSubAssets(string runtimePath)
        {
            foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(runtimePath))
            {
                if (asset is RuntimeNode)
                    UnityEngine.Object.DestroyImmediate(asset, true);
            }
        }

        private static LegacyMigrationManifest BuildManifest(DialogNodeGraph graph, string legacyPath, string authoringGraphPath)
        {
            LegacyMigrationManifest manifest = new()
            {
                legacyGraphPath = legacyPath,
                legacyGraphGuid = AssetDatabase.AssetPathToGUID(legacyPath),
                targetAuthoringGraphPath = authoringGraphPath,
                targetRuntimeGraphPath = $"{Path.GetDirectoryName(authoringGraphPath)}/{Path.GetFileNameWithoutExtension(authoringGraphPath)}_Runtime.asset",
                variablesConfigPath = graph.VariablesConfig == null ? string.Empty : AssetDatabase.GetAssetPath(graph.VariablesConfig),
                localizationTableName = graph.LocalizationTableName,
                characterNamesLocalizationName = graph.CharacterNamesLocalizationName,
                nodes = new List<LegacyNodeRecord>(),
                edges = new List<LegacyEdgeRecord>(),
                warnings = new List<string>()
            };

            if (graph.NodesList == null || graph.NodesList.Count == 0)
            {
                manifest.warnings.Add("Legacy graph has no nodes.");
                return manifest;
            }

            foreach (RuntimeNode node in graph.NodesList.Where(node => node != null))
                manifest.nodes.Add(CreateNodeRecord(node));

            foreach (RuntimeNode node in graph.NodesList.Where(node => node != null))
                AddEdges(node, manifest.edges);

            if (manifest.nodes.Count > 0 && manifest.edges.Count == 0)
                manifest.warnings.Add("Legacy graph contains nodes but no edges.");

            return manifest;
        }

        private static LegacyNodeRecord CreateNodeRecord(RuntimeNode node)
        {
            LegacyNodeRecord record = new()
            {
                id = GetStableId(node),
                nodeType = GetAuthoringNodeType(node),
                legacyType = node.GetType().Name,
                x = node.Rect.x,
                y = node.Rect.y
            };

            switch (node)
            {
                case SentenceNode sentenceNode:
                    record.characterName = sentenceNode.Sentence.CharacterName;
                    record.text = sentenceNode.Sentence.Text;
                    record.spritePath = sentenceNode.Sentence.CharacterSprite == null
                        ? string.Empty
                        : AssetDatabase.GetAssetPath(sentenceNode.Sentence.CharacterSprite);
                    record.characterNameKey = sentenceNode.CharacterNameKey;
                    record.textKey = sentenceNode.SentenceTextKey;
                    record.inlineExternalFunction = sentenceNode.IsExternalFunc();
                    record.functionName = sentenceNode.GetExternalFunctionName();
                    break;
                case AnswerNode answerNode:
                    record.answers = new List<string>(answerNode.Answers);
                    record.answerKeys = new List<string>(answerNode.AnswerKeys);
                    break;
                case ExternalFunctionNode externalFunctionNode:
                    record.functionName = externalFunctionNode.FunctionName;
                    record.description = externalFunctionNode.Description;
                    break;
                case ModifyVariableNode modifyVariableNode:
                    record.variableName = modifyVariableNode.VariableName;
                    record.modificationType = modifyVariableNode.Modification.ToString();
                    record.boolValue = modifyVariableNode.BoolValue;
                    record.intValue = modifyVariableNode.IntValue;
                    record.floatValue = modifyVariableNode.FloatValue;
                    record.stringValue = modifyVariableNode.StringValue;
                    break;
                case VariableConditionNode conditionNode:
                    record.variableName = conditionNode.VariableName;
                    record.conditionType = conditionNode.Condition.ToString();
                    record.boolValue = conditionNode.BoolTargetValue;
                    record.intValue = conditionNode.IntTargetValue;
                    record.floatValue = conditionNode.FloatTargetValue;
                    record.stringValue = conditionNode.StringTargetValue;
                    break;
            }

            return record;
        }

        private static string GetAuthoringNodeType(RuntimeNode node) => node switch
        {
            SentenceNode => nameof(DialogSentenceNode),
            AnswerNode => nameof(DialogAnswerNode),
            ExternalFunctionNode => nameof(DialogExternalFunctionNode),
            ModifyVariableNode => nameof(DialogModifyVariableNode),
            VariableConditionNode => nameof(DialogVariableConditionNode),
            _ => "Unsupported"
        };

        private static void AddEdges(RuntimeNode node, List<LegacyEdgeRecord> edges)
        {
            switch (node)
            {
                case SentenceNode sentenceNode:
                    AddEdge(sentenceNode, DialogGraphPorts.Next, sentenceNode.ChildNode, edges);
                    break;
                case ExternalFunctionNode externalFunctionNode:
                    AddEdge(externalFunctionNode, DialogGraphPorts.Next, externalFunctionNode.ChildNode, edges);
                    break;
                case ModifyVariableNode modifyVariableNode:
                    AddEdge(modifyVariableNode, DialogGraphPorts.Next, modifyVariableNode.ChildNode, edges);
                    break;
                case VariableConditionNode conditionNode:
                    AddEdge(conditionNode, DialogGraphPorts.True, conditionNode.TrueChildNode, edges);
                    AddEdge(conditionNode, DialogGraphPorts.False, conditionNode.FalseChildNode, edges);
                    break;
                case AnswerNode answerNode:
                    for (int i = 0; i < answerNode.ChildNodes.Count; i++)
                        AddEdge(answerNode, DialogGraphPorts.Answer(i), answerNode.ChildNodes[i], edges);
                    break;
            }
        }

        private static void AddEdge(RuntimeNode source, string sourcePort, RuntimeNode target, List<LegacyEdgeRecord> edges)
        {
            if (target == null)
                return;

            edges.Add(new LegacyEdgeRecord
            {
                fromNodeId = GetStableId(source),
                fromPort = sourcePort,
                toNodeId = GetStableId(target),
                toPort = DialogGraphPorts.Input
            });
        }

        private static string GetStableId(UnityEngine.Object asset)
        {
            return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long localId)
                ? $"{guid}:{localId}"
                : asset.GetInstanceID().ToString();
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

        public sealed class MigrationArtifacts
        {
            public MigrationArtifacts(
                string legacyGraphPath,
                string authoringGraphPath,
                string runtimeGraphPath,
                string manifestPath)
            {
                LegacyGraphPath = legacyGraphPath;
                AuthoringGraphPath = authoringGraphPath;
                RuntimeGraphPath = runtimeGraphPath;
                ManifestPath = manifestPath;
            }

            public string LegacyGraphPath { get; }
            public string AuthoringGraphPath { get; }
            public string RuntimeGraphPath { get; }
            public string ManifestPath { get; }
        }

        [Serializable]
        private sealed class LegacyMigrationManifest
        {
            public string legacyGraphPath;
            public string legacyGraphGuid;
            public string targetAuthoringGraphPath;
            public string targetRuntimeGraphPath;
            public string variablesConfigPath;
            public string localizationTableName;
            public string characterNamesLocalizationName;
            public List<LegacyNodeRecord> nodes;
            public List<LegacyEdgeRecord> edges;
            public List<string> warnings;
        }

        [Serializable]
        private sealed class LegacyNodeRecord
        {
            public string id;
            public string legacyType;
            public string nodeType;
            public float x;
            public float y;
            public string characterName;
            public string text;
            public string spritePath;
            public string characterNameKey;
            public string textKey;
            public bool inlineExternalFunction;
            public string functionName;
            public string description;
            public List<string> answers;
            public List<string> answerKeys;
            public string variableName;
            public string modificationType;
            public string conditionType;
            public bool boolValue;
            public int intValue;
            public float floatValue;
            public string stringValue;
        }

        [Serializable]
        private sealed class LegacyEdgeRecord
        {
            public string fromNodeId;
            public string fromPort;
            public string toNodeId;
            public string toPort;
        }
    }
}
