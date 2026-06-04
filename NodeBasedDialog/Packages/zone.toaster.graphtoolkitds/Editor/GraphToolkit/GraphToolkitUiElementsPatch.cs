using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace cherrydev.Editor.GraphToolkit
{
    [InitializeOnLoad]
    internal static class GraphToolkitUiElementsPatch
    {
        private const string PackageName = "com.unity.graphtoolkit";
        private const string ConstantFieldPath =
            "GraphToolkitEditor/UI/ModelView/Inspector/UIElements/ConstantField.cs";
        private const string SessionWarningKey = "GraphToolkitDS.GraphToolkitUiElementsPatch.WarningLogged";

        private const string BrokenRegisterCallbackLookup =
@"            var registerCallbackMethod = typeof(CallbackEventHandler)
                .GetMethods(BindingFlags.Public | BindingFlags.DeclaredOnly | BindingFlags.Instance)
                .SingleOrDefault(m => m.Name == nameof(RegisterCallback) && m.GetGenericArguments().Length == 2);";

        private const string FixedRegisterCallbackLookup =
@"            var registerCallbackMethod = typeof(CallbackEventHandler)
                .GetMethods(BindingFlags.Public | BindingFlags.DeclaredOnly | BindingFlags.Instance)
                .SingleOrDefault(m =>
                {
                    if (m.Name != nameof(RegisterCallback) || m.GetGenericArguments().Length != 2)
                        return false;

                    ParameterInfo[] parameters = m.GetParameters();
                    return parameters.Length == 3 && parameters[2].ParameterType == typeof(TrickleDown);
                });";

        private static readonly string PatchMarker =
            "parameters.Length == 3 && parameters[2].ParameterType == typeof(TrickleDown)";

        static GraphToolkitUiElementsPatch() => EditorApplication.delayCall += ApplyPatchOnLoad;

        [MenuItem("Tools/Dialog System/Apply Graph Toolkit UI Toolkit Fix")]
        private static void ApplyPatchFromMenu()
        {
            if (ApplyPatch())
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        }

        private static void ApplyPatchOnLoad()
        {
            if (ApplyPatch())
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        }

        private static bool ApplyPatch()
        {
            PackageInfo packageInfo = PackageInfo.FindForPackageName(PackageName);

            if (packageInfo == null || string.IsNullOrWhiteSpace(packageInfo.resolvedPath))
                return false;

            string constantFieldPath = Path.Combine(packageInfo.resolvedPath, ConstantFieldPath);

            if (!File.Exists(constantFieldPath))
                return false;

            string source = File.ReadAllText(constantFieldPath);

            if (source.Contains(PatchMarker, StringComparison.Ordinal))
                return false;

            if (!source.Contains(BrokenRegisterCallbackLookup, StringComparison.Ordinal))
            {
                LogPatchWarningOnce(constantFieldPath);
                return false;
            }

            File.WriteAllText(
                constantFieldPath,
                source.Replace(BrokenRegisterCallbackLookup, FixedRegisterCallbackLookup));

            Debug.Log(
                $"GraphToolkitDS patched {PackageName} ConstantField callback lookup for Unity UI Toolkit overload compatibility.");

            return true;
        }

        private static void LogPatchWarningOnce(string constantFieldPath)
        {
            if (SessionState.GetBool(SessionWarningKey, false))
                return;

            SessionState.SetBool(SessionWarningKey, true);
            Debug.LogWarning(
                $"GraphToolkitDS could not apply the UI Toolkit compatibility patch because '{constantFieldPath}' did not match the expected Graph Toolkit 0.4.0-exp.2 source.");
        }
    }
}
