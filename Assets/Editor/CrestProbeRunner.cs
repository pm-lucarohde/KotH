using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Runs StepProbe headless: -batchmode -nographics without -quit, since the
/// probe leaves play mode itself. Kept rather than thrown away; it is how the
/// motor gets measured instead of guessed at.
/// </summary>
public static class CrestProbeRunner
{
    const double WatchdogSeconds = 600.0;

    static double deadline;

    public static void Run()
    {
        // Armed before anything can throw. Batchmode has no window to close,
        // so a failure here would otherwise hang the process forever -- which
        // is exactly what an AddComponent exception once did.
        deadline = EditorApplication.timeSinceStartup + WatchdogSeconds;
        EditorApplication.update += Watchdog;

        EditorSceneManager.OpenScene("Assets/Scenes/Prototype.unity", OpenSceneMode.Single);

        GameObject host = new GameObject("CrestProbe");
        host.AddComponent<CrestProbe>();

        Debug.Log("[crest] entering play mode");
        EditorApplication.EnterPlaymode();
    }

    static void Watchdog()
    {
        if (EditorApplication.timeSinceStartup < deadline)
            return;

        Debug.LogError("[crest] watchdog fired, play mode never finished");
        EditorApplication.Exit(2);
    }
}
