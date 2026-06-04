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

        private static void CompileGraphIfPresent(string graphPath)
        {
            if (AssetDatabase.LoadMainAssetAtPath(graphPath) == null)
                return;

            try
            {
                DialogGraphCompiler.CompileToRuntimeAsset(graphPath);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Dialog graph auto-compile skipped '{graphPath}': {exception.Message}");
            }
        }
    }
}
