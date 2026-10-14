using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Runs MeleeProbe headless: -batchmode -nographics without -quit, since the
/// probe leaves play mode itself. Measures reach, rate, charge and the cap.
/// </summary>
public static class MeleeProbeRunner
{
    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/Prototype.unity", OpenSceneMode.Single);

        GameObject host = new GameObject("MeleeProbe");
        host.AddComponent<MeleeProbe>();

        Debug.Log("[melee] entering play mode");
        EditorApplication.EnterPlaymode();
    }
}
