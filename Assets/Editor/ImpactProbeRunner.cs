using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Runs ImpactProbe headless: -batchmode -nographics without -quit, since the
/// probe leaves play mode itself. Measures the body push, which only exists
/// inside SimulationSystem.
/// </summary>
public static class ImpactProbeRunner
{
    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/Prototype.unity", OpenSceneMode.Single);

        GameObject host = new GameObject("ImpactProbe");
        host.AddComponent<ImpactProbe>();

        Debug.Log("[impact] entering play mode");
        EditorApplication.EnterPlaymode();
    }
}
