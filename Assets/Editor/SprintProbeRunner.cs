using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Runs SprintProbe headless: -batchmode -nographics without -quit, since the
/// probe leaves play mode itself. Measures the sprint state, which only
/// exists inside PlayerMotor and cannot be read off a single tick.
/// </summary>
public static class SprintProbeRunner
{
    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/Prototype.unity", OpenSceneMode.Single);

        GameObject host = new GameObject("SprintProbe");
        host.AddComponent<SprintProbe>();

        Debug.Log("[sprint] entering play mode");
        EditorApplication.EnterPlaymode();
    }
}
