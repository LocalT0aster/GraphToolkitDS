using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace cherrydev.Editor.GraphToolkit
{
    internal sealed class DialogGraphAutoCompiler : AssetPostprocessor
    {
        private static readonly HashSet<string> PendingGraphPaths = new(StringComparer.Ordinal);
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

            QueueGraphPaths(importedAssets);
            QueueGraphPaths(movedAssets);
            ScheduleCompile();
        }

        private static void QueueGraphPaths(IEnumerable<string> assetPaths)
        {
            foreach (string assetPath in assetPaths)
            {
                if (!IsProjectDialogGraph(assetPath))
                    continue;

                PendingGraphPaths.Add(assetPath);
            }
        }

        private static bool IsProjectDialogGraph(string assetPath) =>
            !string.IsNullOrEmpty(assetPath) &&
            assetPath.StartsWith("Assets/", StringComparison.Ordinal) &&
            assetPath.EndsWith($".{DialogAuthoringGraph.AssetExtension}", StringComparison.OrdinalIgnoreCase);

        private static void ScheduleCompile()
        {
            if (PendingGraphPaths.Count == 0 || isCompileScheduled)
                return;

            isCompileScheduled = true;
            EditorApplication.delayCall += CompilePendingGraphs;
        }

        private static void CompilePendingGraphs()
        {
            isCompileScheduled = false;

            if (PendingGraphPaths.Count == 0)
                return;

            string[] graphPaths = new string[PendingGraphPaths.Count];
            PendingGraphPaths.CopyTo(graphPaths);
            PendingGraphPaths.Clear();

            isCompiling = true;

            try
            {
                foreach (string graphPath in graphPaths)
                    CompileGraphIfPresent(graphPath);
            }
            finally
            {
                isCompiling = false;
            }
        }

        internal static bool CompileGraphIfPresent(string graphPath)
        {
            if (AssetDatabase.LoadMainAssetAtPath(graphPath) == null)
                return false;

            if (IsGeneratedFromDialogScript(graphPath))
            {
                Debug.Log($"Dialog graph auto-compile skipped generated script graph '{graphPath}'.");
                return false;
            }

            try
            {
                DialogGraphCompiler.CompileToRuntimeAsset(graphPath);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Dialog graph auto-compile skipped '{graphPath}': {exception.Message}");
                return false;
            }
        }

        private static bool IsGeneratedFromDialogScript(string graphPath)
        {
            string runtimePath = DialogGraphCompiler.GetRuntimeAssetPath(graphPath);
            DialogNodeGraph runtimeGraph = AssetDatabase.LoadAssetAtPath<DialogNodeGraph>(runtimePath);

            return runtimeGraph != null &&
                string.Equals(
                    runtimeGraph.CompilerInputKind,
                    DialogCompilerMetadata.DialogScriptInputKind,
                    StringComparison.Ordinal);
        }
    }
}
