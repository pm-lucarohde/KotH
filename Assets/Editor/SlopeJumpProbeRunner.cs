using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Runs SlopeJumpProbe headless: -batchmode -nographics without -quit, since
/// the probe leaves play mode itself. Guards the ground snap -- without it
/// running downhill loses the ground every tick and refuses the jump.
/// </summary>
public static class SlopeJumpProbeRunner
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

        GameObject host = new GameObject("SlopeJumpProbe");
        host.AddComponent<SlopeJumpProbe>();

        Debug.Log("[slope] entering play mode");
        EditorApplication.EnterPlaymode();
    }

    static void Watchdog()
    {
        if (EditorApplication.timeSinceStartup < deadline)
            return;

        Debug.LogError("[slope] watchdog fired, play mode never finished");
        EditorApplication.Exit(2);
    }
}
