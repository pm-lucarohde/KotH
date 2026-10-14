#if UNITY_EDITOR
using KotH.Shared.Player;
using UnityEngine;

/// <summary>
/// Walks up a ramp from several lateral offsets and approach angles and
/// reports whether the crest is cleared. "Sometimes stuck at the top" is a
/// question about the approach, so one fixed path cannot answer it.
/// </summary>
public sealed class CrestProbe : MonoBehaviour
{
    const int TicksPerCase = 400;
    const float PlateauY = 5f;

    struct Case
    {
        public float Offset;    // sideways along the ramp width
        public float YawBias;   // degrees off straight uphill
        public bool Crouch;
        public bool Sprint;
        public float WiggleHz;  // view shaken while climbing
        public float WiggleDeg;
    }

    // Angles stay small: at 20 degrees the player simply walks off the
    // 4 m wide ramp, which says nothing about the crest.
    static readonly Case[] Cases =
    {
        K(0f, 0f, false, false), K(0f, 0f, false, true),
        K(-1.5f, 0f, false, true), K(1.5f, 0f, false, true),
        K(0f, 5f, false, false), K(0f, -5f, false, false),
        K(0f, 5f, false, true), K(0f, -5f, false, true),
        K(0f, 10f, false, false), K(0f, -10f, false, true),
        K(-1f, 8f, false, true), K(1f, -8f, false, true),
        W(0f, 0f, false, false, 6f, 30f), W(0f, 0f, false, true, 6f, 30f),
        W(0f, 0f, false, true, 11f, 60f), W(-1f, 0f, false, true, 11f, 60f),
        K(0f, 0f, true, false), K(0f, 5f, true, false),
    };

    static Case K(float offset, float yawBias, bool crouch, bool sprint)
        => new Case { Offset = offset, YawBias = yawBias,
                      Crouch = crouch, Sprint = sprint };

    static Case W(float offset, float yawBias, bool crouch, bool sprint,
                  float hz, float deg)
        => new Case { Offset = offset, YawBias = yawBias, Crouch = crouch,
                      Sprint = sprint, WiggleHz = hz, WiggleDeg = deg };

    PlayerMotor motor;
    CharacterController controller;

    int caseIndex = -1;
    int tick;
    float yaw;
    float maxY;
    float lastY;
    int reachedAt = -1;
    int stallTicks;    // high on the ramp, grounded, yet not rising
    int worstStall;

    void Start()
    {
        if (!ProbeSubject.TryBind(out motor, out controller))
            return;

        Next();
    }

    void Next()
    {
        caseIndex++;
        if (caseIndex >= Cases.Length)
        {
            UnityEditor.EditorApplication.Exit(0);
            return;
        }

        Case c = Cases[caseIndex];
        Transform t = GameObject.Find("Ramp45").transform;

        Vector3 surface = t.position + t.up * (t.localScale.y * 0.5f);
        Vector3 uphill = -new Vector3(t.forward.x, 0f, t.forward.z).normalized;
        Vector3 side = Vector3.Cross(Vector3.up, uphill);

        Vector3 foot = surface + t.forward * (t.localScale.z * 0.5f);
        Vector3 start = foot + uphill * -1.5f + side * c.Offset;
        start.y = 0.9f;

        yaw = Mathf.Atan2(uphill.x, uphill.z) * Mathf.Rad2Deg + c.YawBias;

        controller.enabled = false;
        motor.transform.position = start;
        controller.enabled = true;

        tick = 0;
        maxY = start.y;
        lastY = start.y;
        reachedAt = -1;
        stallTicks = 0;
        worstStall = 0;
    }

    void FixedUpdate()
    {
        if (caseIndex >= Cases.Length)
            return;

        Case c = Cases[caseIndex];

        float shake = c.WiggleHz <= 0f ? 0f
            : Mathf.Sin(tick * Time.fixedDeltaTime * c.WiggleHz * Mathf.PI * 2f)
              * c.WiggleDeg;

        var input = new MoveInput
        {
            Move = new Vector2(0f, 1f),
            Yaw = yaw + shake,
            Pitch = 0f,
            JumpPresses = 0,
            Sprint = c.Sprint,
            Crouch = c.Crouch,
        };

        motor.Tick(input, Time.fixedDeltaTime);

        float y = motor.transform.position.y;
        if (y > maxY)
            maxY = y;

        bool onPlateau = y - 0.9f >= PlateauY - 0.1f;
        if (onPlateau && reachedAt < 0)
            reachedAt = tick;

        // A stall is standing high on the ramp, in contact, and not
        // rising. Plain climbing is not a stall, however slow it is.
        if (!onPlateau && y - 0.9f > 3.5f
            && motor.IsGrounded && y - lastY < 0.005f)
        {
            stallTicks++;
            if (stallTicks > worstStall)
                worstStall = stallTicks;
        }
        else if (!onPlateau)
        {
            stallTicks = 0;
        }

        lastY = y;

        tick++;
        if (tick < TicksPerCase)
            return;

        string how = c.Crouch ? "geduckt" : c.Sprint ? "sprint " : "gehen  ";
        string shakeTag = c.WiggleHz > 0f ? $" wackel {c.WiggleDeg:F0}°" : "";
        Debug.Log($"[crest] == Versatz {c.Offset,5:F2} Winkel {c.YawBias,4:F0}" +
                  $" {how}{shakeTag}:" +
                  $" {(reachedAt >= 0 ? $"oben bei Tick {reachedAt,3}" : "NIE OBEN")}," +
                  $" max {maxY - 0.9f:F3} m," +
                  $" längster Stillstand {worstStall} Ticks");
        Next();
    }
}
#endif
