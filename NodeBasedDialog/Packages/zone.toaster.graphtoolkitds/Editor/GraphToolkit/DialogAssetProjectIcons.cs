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
        private const float ListIconSize = 16f;
        private const float ListIconOffset = 2f;
        private const float GridLabelReservedHeight = 14f;

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
            if (IsListItemRect(selectionRect))
            {
                return new Rect(
                    selectionRect.x - ListIconSize - ListIconOffset,
                    selectionRect.y + (selectionRect.height - ListIconSize) * 0.5f,
                    ListIconSize,
                    ListIconSize);
            }

            float iconAreaHeight = Mathf.Max(ListIconSize, selectionRect.height - GridLabelReservedHeight);
            float iconSize = Mathf.Max(ListIconSize, Mathf.Min(selectionRect.width, iconAreaHeight));

            return new Rect(
                selectionRect.x + (selectionRect.width - iconSize) * 0.5f,
                selectionRect.y,
                iconSize,
                iconSize);
        }

        internal static Color GetIconBackgroundColor(bool isSelected)
        {
            if (isSelected)
            {
                Color selectionColor = GUI.skin.settings.selectionColor;
                selectionColor.a = 1f;
                return selectionColor;
            }

            return EditorGUIUtility.isProSkin
                ? new Color32(56, 56, 56, 255)
                : new Color32(194, 194, 194, 255);
        }

        private static Texture2D LoadIcon(string iconPath)
        {
            return AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
        }

        private static void DrawProjectIcon(string guid, Rect selectionRect)
        {
            if (Event.current.type != EventType.Repaint)
                return;

            Texture2D icon = GetIconForAssetPath(AssetDatabase.GUIDToAssetPath(guid));

            if (icon == null)
                return;

            Rect iconRect = GetIconRect(selectionRect);
            EditorGUI.DrawRect(iconRect, GetIconBackgroundColor(IsSelected(guid)));
            GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit, true);
        }

        private static bool IsListItemRect(Rect selectionRect)
        {
            return selectionRect.height <= 20f;
        }

        private static bool IsSelected(string guid)
        {
            return Array.IndexOf(Selection.assetGUIDs, guid) >= 0;
        }
    }
}
