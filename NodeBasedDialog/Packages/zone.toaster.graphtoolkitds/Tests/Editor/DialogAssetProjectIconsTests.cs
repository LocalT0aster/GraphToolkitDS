using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace cherrydev.Editor.GraphToolkit.Tests
{
    public sealed class DialogAssetProjectIconsTests
    {
        [Test]
        public void GetIconPathForAssetPathRecognizesDialogueAssetExtensions()
        {
            string scriptIconPath = DialogAssetProjectIcons.GetIconPathForAssetPath("Assets/Dialogues/Day1/Client_01.ds.md");
            string graphIconPath = DialogAssetProjectIcons.GetIconPathForAssetPath("Assets/Dialogues/Day1/Client_01.dialoggtk");

            Assert.AreEqual(
                "Packages/zone.toaster.graphtoolkitds/Editor/Icons/icon.ds.md.png",
                scriptIconPath);
            Assert.AreEqual(
                "Packages/zone.toaster.graphtoolkitds/Editor/Icons/icon.dialoggtk.png",
                graphIconPath);
        }

        [Test]
        public void GetIconPathForAssetPathMatchesExtensionsCaseInsensitively()
        {
            string scriptIconPath = DialogAssetProjectIcons.GetIconPathForAssetPath("Assets/Dialogues/CLIENT.DS.MD");
            string graphIconPath = DialogAssetProjectIcons.GetIconPathForAssetPath("Assets/Dialogues/CLIENT.DIALOGGTK");

            Assert.IsNotNull(scriptIconPath);
            Assert.IsNotNull(graphIconPath);
        }

        [Test]
        public void PackagedIconAssetsExistAtResolvedPaths()
        {
            string scriptIconPath = DialogAssetProjectIcons.GetIconPathForAssetPath("Assets/Dialogues/Client.ds.md");
            string graphIconPath = DialogAssetProjectIcons.GetIconPathForAssetPath("Assets/Dialogues/Client.dialoggtk");

            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<UnityEngine.Texture2D>(scriptIconPath));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<UnityEngine.Texture2D>(graphIconPath));
        }

        [Test]
        public void GetIconPathForAssetPathIgnoresOtherAssets()
        {
            Assert.IsNull(DialogAssetProjectIcons.GetIconPathForAssetPath("Assets/Dialogues/Client.md"));
            Assert.IsNull(DialogAssetProjectIcons.GetIconPathForAssetPath("Assets/Dialogues/Client.asset"));
            Assert.IsNull(DialogAssetProjectIcons.GetIconPathForAssetPath(string.Empty));
        }

        [Test]
        public void GetIconRectKeepsListViewIconSize()
        {
            Rect iconRect = DialogAssetProjectIcons.GetIconRect(new Rect(30f, 10f, 220f, 18f));

            Assert.AreEqual(new Rect(12f, 11f, 16f, 16f), iconRect);
        }

        [Test]
        public void GetIconRectScalesWithLargeProjectGridItems()
        {
            Rect iconRect = DialogAssetProjectIcons.GetIconRect(new Rect(10f, 20f, 96f, 116f));

            Assert.AreEqual(new Rect(10f, 20f, 96f, 96f), iconRect);
        }

        [Test]
        public void GetIconBackgroundColorIsOpaque()
        {
            Assert.AreEqual(1f, DialogAssetProjectIcons.GetIconBackgroundColor(isSelected: false).a);
            Assert.AreEqual(1f, DialogAssetProjectIcons.GetIconBackgroundColor(isSelected: true).a);
        }
    }
}
