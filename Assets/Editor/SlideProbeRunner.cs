using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Runs SlideProbe headless: -batchmode -nographics without -quit, since the
/// probe leaves play mode itself. Brings its own ramps, because every ramp in
/// the scene leads up while a slide needs a descent.
/// </summary>
public static class SlideProbeRunner
{
    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/Prototype.unity", OpenSceneMode.Single);

        GameObject host = new GameObject("SlideProbe");
        host.AddComponent<SlideProbe>();

        Debug.Log("[slide] entering play mode");
        EditorApplication.EnterPlaymode();
    }
}
