using System;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEngine;

namespace cherrydev.Editor.GraphToolkit
{
    [Graph(AssetExtension)]
    [Serializable]
    public class DialogAuthoringGraph : Graph
    {
        public const string AssetExtension = "dialoggtk";

        [SerializeField] private VariablesConfig _variablesConfig;
        [SerializeField] private string _localizationTableName;
        [SerializeField] private string _characterNamesLocalizationName;

        public VariablesConfig VariablesConfig => _variablesConfig;
        public string LocalizationTableName => _localizationTableName;
        public string CharacterNamesLocalizationName => _characterNamesLocalizationName;

        [MenuItem("Assets/Create/Dialog Node Based System/Dialog Graph", false, 120)]
        private static void CreateAssetFile() =>
            GraphDatabase.PromptInProjectBrowserToCreateNewAsset<DialogAuthoringGraph>("New Dialog Graph");

        public override void OnGraphChanged(GraphLogger graphLogger)
        {
            foreach (DialogGraphIssue issue in DialogGraphValidator.Validate(this))
            {
                if (issue.IsError)
                    graphLogger.LogError(issue.Message, issue.Context);
                else
                    graphLogger.LogWarning(issue.Message, issue.Context);
            }
        }
    }
}
