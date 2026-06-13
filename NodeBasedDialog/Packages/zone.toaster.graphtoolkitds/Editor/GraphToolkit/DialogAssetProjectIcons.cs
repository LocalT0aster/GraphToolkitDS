using System;
using UnityEditor;
using UnityEngine;

namespace cherrydev.Editor.GraphToolkit
{
    [InitializeOnLoad]
    internal static class DialogAssetProjectIcons
    {
        private const string DialogScriptIconPath =
            "Packages/zone.toaster.graphtoolkitds/Editor/Icons/icon.ds.md.png";
        private const string DialogGraphIconPath =
            "Packages/zone.toaster.graphtoolkitds/Editor/Icons/icon.dialoggtk.png";

        private static Texture2D dialogScriptIcon;
        private static Texture2D dialogGraphIcon;

        static DialogAssetProjectIcons()
        {
            EditorApplication.projectWindowItemOnGUI += DrawProjectIcon;
            EditorApplication.delayCall += EditorApplication.RepaintProjectWindow;
        }

        internal static Texture2D GetIconForAssetPath(string assetPath)
        {
            string iconPath = GetIconPathForAssetPath(assetPath);

            if (string.IsNullOrEmpty(iconPath))
                return null;

            if (iconPath == DialogScriptIconPath)
                return dialogScriptIcon ? dialogScriptIcon : dialogScriptIcon = LoadIcon(DialogScriptIconPath);

            if (iconPath == DialogGraphIconPath)
                return dialogGraphIcon ? dialogGraphIcon : dialogGraphIcon = LoadIcon(DialogGraphIconPath);

            return null;
        }

        internal static string GetIconPathForAssetPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return null;

            if (assetPath.EndsWith($".{DialogScriptCompiler.SourceExtension}", StringComparison.OrdinalIgnoreCase))
                return DialogScriptIconPath;

            if (assetPath.EndsWith($".{DialogAuthoringGraph.AssetExtension}", StringComparison.OrdinalIgnoreCase))
                return DialogGraphIconPath;

            return null;
        }

        internal static Rect GetIconRect(Rect selectionRect)
        {
            const float listIconSize = 16f;

            if (selectionRect.height <= 20f)
            {
                return new Rect(
                    selectionRect.x - listIconSize - 2f,
                    selectionRect.y + (selectionRect.height - listIconSize) * 0.5f,
                    listIconSize,
                    listIconSize);
            }

            float iconSize = Mathf.Clamp(
                Mathf.Min(selectionRect.width, selectionRect.height - 14f),
                listIconSize,
                64f);

            return new Rect(
                selectionRect.x + (selectionRect.width - iconSize) * 0.5f,
                selectionRect.y,
                iconSize,
                iconSize);
        }

        private static Texture2D LoadIcon(string iconPath)
        {
            return AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
        }

        private static void DrawProjectIcon(string guid, Rect selectionRect)
        {
            Texture2D icon = GetIconForAssetPath(AssetDatabase.GUIDToAssetPath(guid));

            if (icon == null)
                return;

            GUI.DrawTexture(GetIconRect(selectionRect), icon, ScaleMode.ScaleToFit, true);
        }
    }
}
