#if UNITY_EDITOR
using System.Reflection;
using KotH.Shared.Player;
using UnityEngine;

/// <summary>
/// Measures the sprint state: what starts it, what keeps it, what ends it.
/// Reports the wish speed and the distance actually covered side by side,
/// because a blocked CharacterController keeps the first and loses the second.
/// </summary>
public sealed class SprintProbe : MonoBehaviour
{
    enum Speed { Any, Zero, Walk, Sprint, Crouch, Blocked, Uphill }
    enum Where { Flat, Ramp20, Ramp35, Ramp45 }

    struct Phase
    {
        public string name;
        public int ticks;
        public bool sprint;
        public bool crouch;
        public Vector2 move;
        public bool push;         // a body push lands at the start of this phase
        public int trace;         // log the first n ticks of the phase
        public bool wantSprint;
        public Speed wantSpeed;
    }

    struct Case
    {
        public string name;
        public Where where;
        public bool wall;         // a wall 10 m ahead, clear of the run-up
        public Phase[] phases;
    }

    // Its own patch of ground far outside the scene, so no ramp, step or
    // dummy can reach into a measurement about flat ground.
    static readonly Vector3 Spot = new Vector3(-110f, 0.9f, -120f);

    static readonly Vector2 Fwd = new Vector2(0f, 1f);
    static readonly Vector2 Back = new Vector2(0f, -1f);

    static Phase P(string name, int ticks, bool sprint, bool crouch,
                   Vector2 move, bool wantSprint, Speed wantSpeed)
        => new Phase { name = name, ticks = ticks, sprint = sprint,
                       crouch = crouch, move = move,
                       wantSprint = wantSprint, wantSpeed = wantSpeed };

    static Phase T(string name, int ticks, bool sprint, bool crouch,
                   Vector2 move, bool wantSprint, Speed wantSpeed, int trace)
    {
        Phase p = P(name, ticks, sprint, crouch, move, wantSprint, wantSpeed);
        p.trace = trace;
        return p;
    }

    static readonly Case[] Cases =
    {
        new Case { name = "Einmal tippen", phases = new[] {
            P("tippen, 1 Tick",       1, true,  false, Fwd, true, Speed.Any),
            P("Taste los, vorwärts", 40, false, false, Fwd, true, Speed.Sprint),
        }},
        new Case { name = "Loslassen beendet", phases = new[] {
            P("tippen + anlaufen",   40, true,  false, Fwd, true,  Speed.Sprint),
            P("stehen bleiben",      30, false, false, Vector2.zero, false, Speed.Zero),
            P("wieder vorwärts",     40, false, false, Fwd, false, Speed.Walk),
        }},
        new Case { name = "Halten endet nie", phases = new[] {
            P("halten + anlaufen",   40, true, false, Fwd, true, Speed.Sprint),
            P("halten, stehen",      30, true, false, Vector2.zero, true, Speed.Zero),
            P("halten, vorwärts",    40, true, false, Fwd, true, Speed.Sprint),
        }},
        new Case { name = "Ducken beendet", phases = new[] {
            P("tippen + anlaufen",   40, true,  false, Fwd, true,  Speed.Sprint),
            P("ducken",              30, false, true,  Fwd, false, Speed.Crouch),
            P("aufrichten",          40, false, false, Fwd, false, Speed.Walk),
        }},
        new Case { name = "Ducken mit Halten", phases = new[] {
            P("halten + anlaufen",   40, true, false, Fwd, true, Speed.Sprint),
            P("halten + ducken",     30, true, true,  Fwd, true, Speed.Crouch),
            P("halten, aufrichten",  40, true, false, Fwd, true, Speed.Sprint),
        }},
        new Case { name = "Stoß beendet", phases = new[] {
            P("tippen + anlaufen",   40, true, false, Fwd, true, Speed.Sprint),
            // Two ticks: the limit reads the previous tick, so the push
            // lands in the first and ends the sprint in the second.
            new Phase { name = "Stoß entgegen", ticks = 2, move = Fwd,
                        push = true, wantSprint = false, wantSpeed = Speed.Any },
            P("danach vorwärts",     40, false, false, Fwd, false, Speed.Walk),
        }},
        new Case { name = "Wand, getippt", wall = true, phases = new[] {
            P("tippen + anlaufen",   40, true,  false, Fwd, true,  Speed.Sprint),
            P("in die Wand rennen",  40, false, false, Fwd, false, Speed.Blocked),
            P("zurück, vorwärts",    40, false, false, Back, false, Speed.Walk),
        }},
        new Case { name = "Wand, gehalten", wall = true, phases = new[] {
            P("halten + anlaufen",   40, true, false, Fwd, true, Speed.Sprint),
            P("in die Wand rennen",  40, true, false, Fwd, true, Speed.Blocked),
            P("halten, zurück",      40, true, false, Back, true, Speed.Sprint),
        }},
        // The open question about measuring the real step: uphill the
        // horizontal part of it is shorter than the distance travelled.
        new Case { name = "Rampe 20 getippt", where = Where.Ramp20, phases = new[] {
            P("tippen + anlaufen",   20, true,  false, Fwd, true, Speed.Any),
            P("Rampe hoch",          30, false, false, Fwd, true, Speed.Uphill),
        }},
        new Case { name = "Rampe 35 getippt", where = Where.Ramp35, phases = new[] {
            P("tippen + anlaufen",   20, true,  false, Fwd, true, Speed.Any),
            P("Rampe hoch",          30, false, false, Fwd, true, Speed.Uphill),
        }},
        new Case { name = "Rampe 45 getippt", where = Where.Ramp45, phases = new[] {
            P("tippen + anlaufen",   20, true,  false, Fwd, true, Speed.Any),
            P("Rampe hoch",          30, false, false, Fwd, true, Speed.Uphill),
        }},
        // Held, so the sprint survives the impact tick and the steady
        // uphill step becomes measurable at all.
        new Case { name = "Rampe 20 gehalten", where = Where.Ramp20, phases = new[] {
            P("anlaufen",            20, true, false, Fwd, true, Speed.Any),
            T("Rampe hoch",          30, true, false, Fwd, true, Speed.Uphill, 14),
        }},
        new Case { name = "Rampe 35 gehalten", where = Where.Ramp35, phases = new[] {
            P("anlaufen",            20, true, false, Fwd, true, Speed.Any),
            T("Rampe hoch",          30, true, false, Fwd, true, Speed.Uphill, 14),
        }},
        new Case { name = "Rampe 45 gehalten", where = Where.Ramp45, phases = new[] {
            P("anlaufen",            20, true, false, Fwd, true, Speed.Any),
            T("Rampe hoch",          30, true, false, Fwd, true, Speed.Uphill, 14),
        }},
        new Case { name = "Wand gehalten, Verlauf", where = Where.Flat,
                   wall = true, phases = new[] {
            P("anlaufen",            40, true, false, Fwd, true, Speed.Any),
            T("in die Wand",         40, true, false, Fwd, true, Speed.Blocked, 14),
        }},
    };

    PlayerMotor motor;
    GameObject wall;

    float walkSpeed;
    float sprintSpeed;
    float crouchSpeed;

    bool ready;
    int caseIndex = -1;
    int phaseIndex;
    int tick;
    int failures;
    float yaw;
    float lastReal;           // 3D distance of the last tick over dt
    float lastFlat;           // its horizontal part, which uphill is smaller
    float minRealLate;        // slowest of the phase's final third
    int sprintLostAt = -1;    // tick inside the phase the state went out

    void Start()
    {
        if (!ProbeSubject.TryBind(out motor))
            return;

        if (!ReadSpeed("walkSpeed", out walkSpeed)
            || !ReadSpeed("sprintSpeed", out sprintSpeed)
            || !ReadSpeed("crouchSpeed", out crouchSpeed))
            return;

        CheckSlopeHeadroom();
        BuildFlatGround();

        Debug.Log($"[sprint] Sollwerte: gehen {walkSpeed:F3}," +
                  $" sprint {sprintSpeed:F3}, geduckt {crouchSpeed:F3} m/s");

        // The first case waits for FixedUpdate on purpose: the ground was
        // created a moment ago and PhysX does not know it yet, so settling
        // here would drop the player straight through it.
        ready = true;
    }

    // Read instead of written down: a target value that merely repeats a
    // guessed number stands there silently wrong after the next tuning pass.
    bool ReadSpeed(string field, out float value)
    {
        value = 0f;
        FieldInfo f = typeof(PlayerMotor).GetField(
            field, BindingFlags.Instance | BindingFlags.NonPublic);

        if (f == null)
        {
            Debug.LogError($"[sprint] Feld {field} fehlt in PlayerMotor");
            UnityEditor.EditorApplication.Exit(3);
            return false;
        }

        value = (float)f.GetValue(motor);
        return true;
    }

    // Uphill the real step shrinks to sprintSpeed * cos(angle), so the
    // sprint only survives the steepest walkable ramp while walk over sprint
    // stays under cos(slopeLimit). Checked, not assumed: retuning either
    // speed or the slope limit can break it silently.
    void CheckSlopeHeadroom()
    {
        float ratio = walkSpeed / sprintSpeed;
        float limit = Mathf.Cos(motor.SlopeLimit * Mathf.Deg2Rad);

        Debug.Log($"[sprint] gehen/sprint {ratio:F3} gegen" +
                  $" cos({motor.SlopeLimit:F0}°) = {limit:F3}:" +
                  $" {(ratio < limit ? "Luft da" : "ZU KNAPP, Sprint stirbt bergauf")}");

        if (ratio >= limit)
            failures++;
    }

    void BuildFlatGround()
    {
        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        floor.name = "SprintProbeFloor";
        floor.transform.position = new Vector3(Spot.x, -0.5f, Spot.z + 15f);
        floor.transform.localScale = new Vector3(40f, 1f, 60f);

        // 10 m ahead, so a 40 tick run-up at sprint speed still ends clear
        // of it and the collision is a measurement of its own.
        wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = "SprintProbeWall";
        wall.transform.position = new Vector3(Spot.x, 1.5f, Spot.z + 10f);
        wall.transform.localScale = new Vector3(12f, 3f, 0.5f);
        wall.SetActive(false);

        // CreatePrimitive places the collider at the origin and PhysX keeps
        // that until the next sync, so without this the fresh ground sits
        // 100 m away and the first case falls through it.
        Physics.SyncTransforms();
    }

    void NextCase()
    {
        caseIndex++;
        if (caseIndex >= Cases.Length)
        {
            Debug.Log(failures == 0
                ? "[sprint] ALLE FÄLLE WIE ERWARTET"
                : $"[sprint] {failures} ABWEICHUNGEN");
            UnityEditor.EditorApplication.Exit(failures == 0 ? 0 : 1);
            return;
        }

        Case c = Cases[caseIndex];
        wall.SetActive(c.wall);
        Place(c.where);

        // The motor keeps velocity and sprint state across cases, so both
        // are cleared by standing still: no case may inherit the last one.
        for (int i = 0; i < 60; i++)
            motor.Tick(new MoveInput { Yaw = yaw }, Time.fixedDeltaTime);

        Debug.Log($"[sprint] == {c.name}");
        phaseIndex = 0;
        StartPhase();
    }

    // Placed once per case, never between phases: the phases run on without
    // a break, so a lost sprint cannot be an artefact of a teleport.
    void Place(Where where)
    {
        Vector3 start = Spot;
        yaw = 0f;

        if (where != Where.Flat)
        {
            Transform t = GameObject.Find(where.ToString()).transform;
            Vector3 surface = t.position + t.up * (t.localScale.y * 0.5f);
            Vector3 uphill = -new Vector3(t.forward.x, 0f, t.forward.z).normalized;
            Vector3 foot = surface + t.forward * (t.localScale.z * 0.5f);

            start = foot + uphill * -4f;
            start.y = 0.9f;
            yaw = Mathf.Atan2(uphill.x, uphill.z) * Mathf.Rad2Deg;
        }

        motor.Teleport(start);
    }

    void StartPhase()
    {
        tick = 0;
        lastReal = 0f;
        lastFlat = 0f;
        minRealLate = float.MaxValue;
        sprintLostAt = -1;

        if (Cases[caseIndex].phases[phaseIndex].push)
        {
            // The same path a body contact takes: SimulationSystem calls
            // AddVelocity, nothing else. A frontal push at two thirds of the
            // speed lands below walk speed, which is what ends the sprint.
            Vector3 v = motor.Velocity;
            Vector3 flat = new Vector3(v.x, 0f, v.z);
            motor.AddVelocity(-flat * 0.667f, 0.2f);
        }
    }

    void FixedUpdate()
    {
        if (ready && caseIndex < 0)
        {
            ready = false;
            NextCase();
            return;
        }

        if (caseIndex < 0 || caseIndex >= Cases.Length)
            return;

        Phase p = Cases[caseIndex].phases[phaseIndex];
        float dt = Time.fixedDeltaTime;

        Vector3 before = motor.transform.position;
        bool wasSprinting = motor.IsSprinting;

        motor.Tick(new MoveInput
        {
            Move = p.move,
            Yaw = yaw,
            Sprint = p.sprint,
            Crouch = p.crouch,
        }, dt);

        Vector3 after = motor.transform.position;
        Vector3 step = after - before;
        lastReal = step.magnitude / dt;
        lastFlat = new Vector2(step.x, step.z).magnitude / dt;

        if (wasSprinting && !motor.IsSprinting && sprintLostAt < 0)
            sprintLostAt = tick;

        if (tick >= p.ticks * 2 / 3 && lastReal < minRealLate)
            minRealLate = lastReal;

        if (tick < p.trace)
            Debug.Log($"[sprint]       Tick {tick,3}: echt {lastReal,6:F3}" +
                      $" {(lastReal < walkSpeed ? "UNTER" : "     ")}," +
                      $" waagerecht {lastFlat,6:F3}," +
                      $" sprint {(motor.IsSprinting ? "AN " : "aus")}," +
                      $" y {motor.transform.position.y,6:F2}," +
                      $" boden {(motor.IsGrounded ? "ja " : "nein")}");

        tick++;
        if (tick < p.ticks)
            return;

        Report(p);

        phaseIndex++;
        if (phaseIndex < Cases[caseIndex].phases.Length)
            StartPhase();
        else
            NextCase();
    }

    void Report(Phase p)
    {
        Vector3 v = motor.Velocity;
        float wish = new Vector2(v.x, v.z).magnitude;
        bool sprinting = motor.IsSprinting;

        float want;
        switch (p.wantSpeed)
        {
            case Speed.Walk: want = walkSpeed; break;
            case Speed.Sprint: want = sprintSpeed; break;
            case Speed.Crouch: want = crouchSpeed; break;
            case Speed.Blocked: want = 0f; break;
            case Speed.Zero: want = 0f; break;
            // Computed from the angle actually measured underfoot, so the
            // target follows the ramp instead of repeating a written number.
            case Speed.Uphill:
                want = sprintSpeed
                     * Mathf.Cos(motor.GroundAngle * Mathf.Deg2Rad);
                break;
            default: want = -1f; break;
        }

        float tolerance = p.wantSpeed == Speed.Blocked ? 0.2f : 0.05f;
        if (p.wantSpeed == Speed.Uphill)
            tolerance = 0.1f;
        bool speedOk = want < 0f || Mathf.Abs(lastReal - want) < tolerance;
        bool ok = sprinting == p.wantSprint && speedOk;

        if (!ok)
            failures++;

        string wanted = $"sprint {(p.wantSprint ? "an" : "aus")}" +
                        (want < 0f ? "" : $", echt {want:F2}");

        if (p.wantSpeed == Speed.Uphill)
            wanted += $" (sprint x cos {motor.GroundAngle:F1}°)";

        Debug.Log($"[sprint]    {p.name,-20} nach {p.ticks,3} Ticks:" +
                  $" sprint {(sprinting ? "AN " : "aus")}" +
                  $"{(sprintLostAt >= 0 ? $" (erlosch bei Tick {sprintLostAt,3})" : "")}," +
                  $" wunsch {wish,6:F3}, echt {lastReal,6:F3}" +
                  $" (waagerecht {lastFlat,6:F3})" +
                  $" (min letztes Drittel {minRealLate,6:F3})," +
                  $" x/y/z {motor.transform.position.x,8:F2}/" +
                  $"{motor.transform.position.y,6:F2}/{motor.transform.position.z,8:F2}" +
                  $"  -> {(ok ? "ok" : "ABWEICHUNG, erwartet " + wanted)}");
    }
}
#endif
