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

    // Ramps are read from the scene instead of hard-coded: the object's
    // local +z points downhill, +y is the surface normal, scale is
    // (width, thickness, length). That is how they were constructed.
    struct RampCase
    {
        public string Name;
        public string Ramp;
        public bool AtFoot;   // start on flat ground below and walk uphill
        public bool Walk;
        public int Ticks;
    }

    static readonly RampCase[] RampCases =
    {
        R("Ramp45 stehen",      "Ramp45", false, false, 80),
        R("Ramp55 stehen",      "Ramp55", false, false, 80),
        R("Ramp45 hochgehen",   "Ramp45", true,  true,  240),
        R("Ramp55 hochgehen",   "Ramp55", true,  true,  240),
    };

    static RampCase R(string name, string ramp, bool atFoot, bool walk, int ticks)
        => new RampCase { Name = name, Ramp = ramp, AtFoot = atFoot, Walk = walk, Ticks = ticks };

    const int TicksPerCase = 240;

    PlayerMotor motor;
    CharacterController controller;

    int caseIndex = -1;
    int rampIndex = -1;
    int tick;
    float startY;
    float maxY;
    float minY;
    int steepTicks;
    int loggedJumps;
    int airTicks;        // controller.isGrounded read false
    int slideTicks;
    float maxAngle;
    Vector2 rampMove;
    float rampYaw;

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

        // Ramps first: they are what is under investigation, and a watchdog
        // that fires must not eat the cases that matter.
        NextRampCase();
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

        Place(Cases[caseIndex].Start);
    }

    void NextRampCase()
    {
        rampIndex++;
        if (rampIndex >= RampCases.Length)
        {
            NextCase();
            return;
        }

        RampCase r = RampCases[rampIndex];
        Transform t = GameObject.Find(r.Ramp).transform;

        // Surface instead of centre: the slab is thickness thick and its
        // pivot sits in the middle of it.
        Vector3 surface = t.position + t.up * (t.localScale.y * 0.5f);
        Vector3 uphill = -new Vector3(t.forward.x, 0f, t.forward.z).normalized;
        rampYaw = Mathf.Atan2(uphill.x, uphill.z) * Mathf.Rad2Deg;
        rampMove = r.Walk ? new Vector2(0f, 1f) : Vector2.zero;

        Vector3 start;
        if (r.AtFoot)
        {
            Vector3 foot = surface + t.forward * (t.localScale.z * 0.5f);
            start = foot - uphill * 1.5f;
            start.y = 0.9f;
        }
        else
        {
            start = surface + Vector3.up * 0.95f;
        }

        Debug.Log($"[probe] ~ {r.Name}: Start {start}, bergauf-Yaw {rampYaw:F0}");
        Place(start);
    }

    void Place(Vector3 start)
    {
        controller.enabled = false;
        motor.transform.position = start;
        controller.enabled = true;

        tick = 0;
        startY = start.y;
        maxY = startY;
        minY = startY;
        steepTicks = 0;
        loggedJumps = 0;
        airTicks = 0;
        slideTicks = 0;
        maxAngle = 0f;
    }

    void FixedUpdate()
    {
        if (rampIndex < RampCases.Length)
        {
            RampTick();
            return;
        }

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

    void RampTick()
    {
        RampCase r = RampCases[rampIndex];

        var input = new MoveInput
        {
            Move = rampMove,
            Yaw = rampYaw,
            Pitch = 0f,
            JumpPresses = 0,
            Sprint = false,
            Crouch = false,
        };

        motor.Tick(input, Time.fixedDeltaTime);

        float y = motor.transform.position.y;
        if (y > maxY) maxY = y;
        if (y < minY) minY = y;
        if (!controller.isGrounded) airTicks++;
        if (motor.IsSliding) slideTicks++;
        if (motor.GroundAngle > maxAngle) maxAngle = motor.GroundAngle;
        if (motor.GroundAngle > controller.slopeLimit) steepTicks++;

        // The first ticks tell the whole story: if isGrounded flickers, the
        // steep reading never holds long enough to confirm a slide.
        if (tick < 16)
            Debug.Log($"[probe] ~ {r.Name} t{tick:D3} y {y:F3}" +
                      $" | touching {controller.isGrounded}" +
                      $" | angle {motor.GroundAngle:F1} | grounded {motor.IsGrounded}" +
                      $" | sliding {motor.IsSliding}");

        tick++;
        if (tick >= r.Ticks)
        {
            Debug.Log($"[probe] == {r.Name}: end {y - startY:+0.000;-0.000} m," +
                      $" max {maxY - startY:+0.000;-0.000}, min {minY - startY:+0.000;-0.000}," +
                      $" maxAngle {maxAngle:F1}, steile Ticks {steepTicks}," +
                      $" Rutsch-Ticks {slideTicks}, Luft-Ticks {airTicks} von {r.Ticks}");
            NextRampCase();
        }
    }
}
#endif
