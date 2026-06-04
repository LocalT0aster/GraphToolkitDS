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
    internal static class LegacyGraphMigrationTool
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

            foreach (DialogNodeGraph graph in graphs)
                CreateMigrationArtifacts(graph);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog(
                "Migrate Legacy Dialog Graphs",
                $"Created migration artifacts for {graphs.Count} graph(s).",
                "OK");
        }

        [MenuItem(MenuPath, true)]
        private static bool CanMigrateSelectedGraphs() =>
            Selection.objects.Any(selection => selection is DialogNodeGraph);

        private static void CreateMigrationArtifacts(DialogNodeGraph graph)
        {
            string legacyPath = AssetDatabase.GetAssetPath(graph);
            string directory = Path.GetDirectoryName(legacyPath);
            string graphName = Path.GetFileNameWithoutExtension(legacyPath);
            string authoringGraphPath = AssetDatabase.GenerateUniqueAssetPath($"{directory}/{graphName}.{DialogAuthoringGraph.AssetExtension}");
            string manifestPath = AssetDatabase.GenerateUniqueAssetPath($"{directory}/{graphName}.dialogmigration.json");

            GraphDatabase.CreateGraph<DialogAuthoringGraph>(authoringGraphPath);

            LegacyMigrationManifest manifest = BuildManifest(graph, legacyPath, authoringGraphPath);
            File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, true));
            AssetDatabase.ImportAsset(manifestPath);

            Debug.Log(
                $"Created Graph Toolkit migration placeholder '{authoringGraphPath}' and manifest '{manifestPath}'. " +
                "Unity 6000.3 does not expose public APIs for fully automated GTK node/wire creation, so rebuild the graph manually using the manifest mapping.");
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
