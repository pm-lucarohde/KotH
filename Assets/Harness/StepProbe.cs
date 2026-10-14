#if UNITY_EDITOR
using KotH.Client.Player;
using KotH.Shared.Player;
using UnityEngine;

/// <summary>
/// Drives the motor against the stair flights with scripted input, so the
/// wiggle exploit can be measured instead of guessed at. Editor only: the
/// whole file is behind UNITY_EDITOR, so it compiles to nothing in a build.
/// An asmdef with includePlatforms Editor does not work here -- that makes
/// the class an editor script and AddComponent refuses it outright.
/// </summary>
public sealed class StepProbe : MonoBehaviour
{
    struct Case
    {
        public string Name;
        public Vector3 Start;
        public Vector2 Move;
        public float BaseYaw;
        public float WiggleHz;
        public float WiggleDegrees;
        public bool Crouch;
    }

    // Flights sit at z = -10, 2 m wide: Steps20 x -6, Steps30 -10,
    // Steps40 -14, Steps50 -18. Steps50 spans x -19 to -17.
    static readonly Case[] Cases =
    {
        // Must climb.
        C("+ Steps20 geduckt",       -6f,   -11.5f, 0, 1, 0,   0,  0,   true),
        C("+ Steps20 geduckt wackel",-6f,   -11.5f, 0, 1, 0,   9f, 80f, true),
        C("+ Steps40 ruhig",         -14f,  -11.5f, 0, 1, 0,   0,  0,   false),
        C("+ Steps40 wackeln",       -14f,  -11.5f, 0, 1, 0,   9f, 80f, false),
        C("+ Steps40 Ecke wackeln",  -13.1f,-11.5f, 0, 1, 0,   9f, 80f, false),
        // Must NOT climb.
        C("- Steps50 ruhig",         -18f,  -11.5f, 0, 1, 0,   0,  0,   false),
        C("- Steps50 wackeln",       -18f,  -11.5f, 0, 1, 0,   9f, 80f, false),
        C("- Steps50 Ecke wackeln",  -17.1f,-11.5f, 0, 1, 0,   9f, 80f, false),
        C("- Steps50 Ecke langsam",  -17.1f,-11.5f, 0, 1, 0,   2f, 45f, false),
        C("- Steps50 seitlich",      -17.1f,-9.0f,  1, 0, 0,   9f, 80f, false),
        C("- Steps30 geduckt",       -10f,  -11.5f, 0, 1, 0,   9f, 80f, true),
        C("- Steps30 geduckt Ecke",  -9.1f, -11.5f, 0, 1, 0,   9f, 80f, true),
    };

    static Case C(string name, float x, float z, float mx, float my,
                  float yaw, float hz, float deg, bool crouch)
        => new Case
        {
            Name = name,
            Start = new Vector3(x, 0.9f, z),
            Move = new Vector2(mx, my),
            BaseYaw = yaw,
            WiggleHz = hz,
            WiggleDegrees = deg,
            Crouch = crouch,
        };

    const int TicksPerCase = 240;

    PlayerMotor motor;
    CharacterController controller;

    int caseIndex = -1;
    int tick;
    float startY;
    float maxY;
    int steepTicks;
    int loggedJumps;

    void Start()
    {
        motor = FindFirstObjectByType<PlayerMotor>();
        controller = motor.GetComponent<CharacterController>();

        PlayerInputReader reader = motor.GetComponent<PlayerInputReader>();
        if (reader != null)
            reader.enabled = false;

        Debug.Log("[probe] stepOffset " + controller.stepOffset +
                  ", height " + controller.height +
                  ", slopeLimit " + controller.slopeLimit);
        NextCase();
    }

    void NextCase()
    {
        caseIndex++;
        if (caseIndex >= Cases.Length)
        {
            Debug.Log("[probe] done");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(0);
#endif
            return;
        }

        controller.enabled = false;
        motor.transform.position = Cases[caseIndex].Start;
        controller.enabled = true;

        tick = 0;
        startY = motor.transform.position.y;
        maxY = startY;
        steepTicks = 0;
        loggedJumps = 0;
    }

    void FixedUpdate()
    {
        if (caseIndex >= Cases.Length)
            return;

        Case c = Cases[caseIndex];

        float yaw = c.BaseYaw + (c.WiggleHz <= 0f
            ? 0f
            : Mathf.Sin(tick * Time.fixedDeltaTime * c.WiggleHz * Mathf.PI * 2f) * c.WiggleDegrees);

        var input = new MoveInput
        {
            Move = c.Move,
            Yaw = yaw,
            Pitch = 0f,
            JumpPresses = 0,
            Sprint = false,
            Crouch = c.Crouch,
        };

        float before = motor.transform.position.y;
        motor.Tick(input, Time.fixedDeltaTime);
        float after = motor.transform.position.y;

        if (motor.GroundAngle > controller.slopeLimit)
            steepTicks++;

        if (after > maxY)
            maxY = after;

        // Only the first few climbing ticks, otherwise the log drowns.
        if (after - before > 0.02f && loggedJumps < 6)
        {
            loggedJumps++;
            Debug.Log($"[probe] {c.Name} t{tick:D3} +{after - before:F3} m -> y {after:F3}" +
                      $" | angle {motor.GroundAngle:F1} | grounded {motor.IsGrounded}" +
                      $" | sliding {motor.IsSliding} | yaw {yaw:F0}");
        }

        tick++;
        if (tick >= TicksPerCase)
        {
            Debug.Log($"[probe] == {c.Name}: max {maxY - startY:F3} m," +
                      $" end {motor.transform.position.y - startY:F3} m," +
                      $" steile Ticks {steepTicks}");
            NextCase();
        }
    }
}
#endif
