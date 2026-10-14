using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Runs JumpHeightProbe headless: -batchmode -nographics without -quit,
/// since the probe leaves play mode itself. Measures how the jump height
/// follows the hold time, which no earlier probe covered at all.
/// </summary>
public static class JumpHeightProbeRunner
{
    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/Prototype.unity", OpenSceneMode.Single);

        GameObject host = new GameObject("JumpHeightProbe");
        host.AddComponent<JumpHeightProbe>();

        Debug.Log("[jump] entering play mode");
        EditorApplication.EnterPlaymode();
    }
}
