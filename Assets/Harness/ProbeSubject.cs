#if UNITY_EDITOR
using KotH.Client.Player;
using KotH.Shared.Player;
using UnityEngine;

/// <summary>
/// Binds a probe to the player. Since the training dummy exists, "the first
/// PlayerMotor in the scene" is undefined: a probe that grabs the wrong body
/// disables the wrong input reader and then measures nothing at all, without
/// ever failing. That is why this stops the run instead of guessing.
/// </summary>
public static class ProbeSubject
{
    public static bool TryBind(out PlayerMotor motor,
                               out CharacterController controller)
    {
        motor = null;
        controller = null;

        // The input reader is what makes a body the player, and its
        // RequireComponent puts the motor on that same object.
        PlayerInputReader reader =
            Object.FindFirstObjectByType<PlayerInputReader>();

        if (reader == null)
        {
            Debug.LogError("[probe] kein PlayerInputReader in der Szene");
            UnityEditor.EditorApplication.Exit(3);
            return false;
        }

        reader.enabled = false;
        motor = reader.GetComponent<PlayerMotor>();
        controller = motor.GetComponent<CharacterController>();
        return true;
    }
}
#endif
