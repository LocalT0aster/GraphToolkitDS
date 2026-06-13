using NUnit.Framework;
using UnityEditor;

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
    }
}
