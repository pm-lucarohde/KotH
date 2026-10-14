#if UNITY_EDITOR
using KotH.Shared.Player;
using UnityEngine;

/// <summary>
/// Measures how often the ground is lost while running DOWN a ramp. A jump
/// needs IsGrounded, so every tick without it is a refused jump. Throwaway
/// probe for that one question.
/// </summary>
public sealed class SlopeJumpProbe : MonoBehaviour
{
    const int TicksPerCase = 120;

    static readonly string[] Ramps = { "Ramp20", "Ramp35", "Ramp45" };

    PlayerMotor motor;
    CharacterController controller;

    int caseIndex = -1;
    int tick;
    float yaw;
    bool sprint;

    int notGrounded;
    int streak;
    int worstStreak;

    void Start()
    {
        if (!ProbeSubject.TryBind(out motor, out controller))
            return;

        Next();
    }

    void Next()
    {
        caseIndex++;
        if (caseIndex >= Ramps.Length * 2)
        {
            UnityEditor.EditorApplication.Exit(0);
            return;
        }

        Transform t = GameObject.Find(Ramps[caseIndex / 2]).transform;
        sprint = caseIndex % 2 == 1;

        // Local +z points downhill, the pivot sits inside the slab.
        Vector3 surface = t.position + t.up * (t.localScale.y * 0.5f);
        Vector3 downhill = new Vector3(t.forward.x, 0f, t.forward.z).normalized;
        yaw = Mathf.Atan2(downhill.x, downhill.z) * Mathf.Rad2Deg;

        Vector3 start = surface - downhill * (t.localScale.z * 0.35f)
                      + Vector3.up * 0.95f;

        controller.enabled = false;
        motor.transform.position = start;
        controller.enabled = true;

        tick = 0;
        notGrounded = 0;
        streak = 0;
        worstStreak = 0;
    }

    void FixedUpdate()
    {
        if (caseIndex >= Ramps.Length * 2)
            return;

        var input = new MoveInput
        {
            Move = new Vector2(0f, 1f),
            Yaw = yaw,
            Pitch = 0f,
            JumpPresses = 0,
            Sprint = sprint,
            Crouch = false,
        };

        motor.Tick(input, Time.fixedDeltaTime);

        // Settling ticks are not part of the question.
        if (tick >= 20)
        {
            if (!motor.IsGrounded)
            {
                notGrounded++;
                streak++;
                if (streak > worstStreak)
                    worstStreak = streak;
            }
            else
            {
                streak = 0;
            }
        }

        tick++;
        if (tick < TicksPerCase)
            return;

        int counted = TicksPerCase - 20;
        Debug.Log($"[slope] == {Ramps[caseIndex / 2]}" +
                  $" {(sprint ? "sprint" : "gehen")} runter:" +
                  $" ohne Boden {notGrounded} von {counted}" +
                  $" ({100f * notGrounded / counted:F0} %)," +
                  $" längste Strecke {worstStreak} Ticks" +
                  $" ({worstStreak * 0.025f:F3} s)");
        Next();
    }
}
#endif
