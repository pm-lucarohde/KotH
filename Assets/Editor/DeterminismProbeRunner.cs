using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Runs DeterminismProbe headless: -batchmode -nographics without -quit,
/// since the probe leaves play mode itself. Exit 0 means every replay was
/// bit for bit identical to the live run.
/// </summary>
public static class DeterminismProbeRunner
{
    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/Prototype.unity", OpenSceneMode.Single);

        GameObject host = new GameObject("DeterminismProbe");
        host.AddComponent<DeterminismProbe>();

        Debug.Log("[det] entering play mode");
        EditorApplication.EnterPlaymode();
    }
}
