using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public class UnusedAssetCleaner
{
    [MenuItem("Tools/Clean Unused Assets to Trash")]
    public static void DeleteUnusedAssetsToTrash()
    {
        // 1. Gather all scenes as dependency roots
        string[] allGuids = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" });
        List<string> rootPaths = new List<string>();
        foreach (var guid in allGuids)
            rootPaths.Add(AssetDatabase.GUIDToAssetPath(guid));

        // 2. Protect materials and pipeline settings
        string[] extraSettings = AssetDatabase.FindAssets("t:RenderPipelineAsset t:Material", new[] { "Assets" });
        foreach (var guid in extraSettings)
            rootPaths.Add(AssetDatabase.GUIDToAssetPath(guid));

        // 3. Find all dependencies referenced by scenes/materials
        string[] dependencies = AssetDatabase.GetDependencies(rootPaths.ToArray(), true);
        HashSet<string> usedAssets = new HashSet<string>(dependencies);

        string[] allAssets = AssetDatabase.GetAllAssetPaths();
        int trashedCount = 0;

        AssetDatabase.StartAssetEditing();

        try
        {
            foreach (string path in allAssets)
            {
                // Skip non-Assets files, active scene assets, folders, and C# scripts
                if (!path.StartsWith("Assets/") ||
                    usedAssets.Contains(path) ||
                    AssetDatabase.IsValidFolder(path) ||
                    path.EndsWith(".cs"))
                    continue;

                // PROTECT SYSTEM & RENDERING FILES
                string ext = Path.GetExtension(path).ToLower();
                if (ext == ".shader" || ext == ".shadergraph" || ext == ".hlsl" ||
                    ext == ".cginc" || ext == ".mat" || ext == ".asset" ||
                    path.Contains("/Resources/") || path.Contains("TextMesh"))
                    continue;

                // Move directly to OS Trash/Recycle Bin
                if (AssetDatabase.MoveAssetToTrash(path))
                {
                    trashedCount++;
                }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();
        }

        Debug.Log($"Successfully moved {trashedCount} unused assets to your system Trash / Recycle Bin.");
    }
}