#if UNITY_EDITOR
using System.Reflection;
using KotH.Shared.Player;
using UnityEngine;

/// <summary>
/// Measures the crouch slide. Brings its own ramps: every ramp in the scene
/// leads up to the plateau, while a slide needs a descent with run-up in
/// front of it. Expectations are computed from the motor's own fields.
/// </summary>
public sealed class SlideProbe : MonoBehaviour
{
    enum Check { Duration, EndsOnRelease, SpeedRises, SpeedCapped,
                 EndsEarly, JumpClamped, Turns, RollsOut, NoStart, Eyes,
                 CooldownBlocks, CooldownClears }

    struct Case
    {
        public string name;
        public int surface;        // index into Surfaces
        public bool uphill;
        public bool wall;
        public int runup;          // ticks of sprinting before crouching
        public int crouchHold;     // ticks to hold crouch, -1 = to the end
        public bool steer;         // hold forward during the slide
        public float yawTurn;      // degrees turned over the slide
        public int jumpAt;         // tick inside the slide, -1 = never
        public int retryAt;         // tick after the slide start to press again
        public bool shouldStart;
        public Check check;
    }

    // Flat, 20, 35 and 55 degrees, each its own patch far from the scene.
    static readonly float[] Surfaces = { 0f, 20f, 35f, 55f };
    const float SurfaceGap = 60f;
    static readonly Vector3 Origin = new Vector3(-110f, 0f, -140f);

    static readonly Case[] Cases =
    {
        new Case { name = "Flach, Dauer", surface = 0, runup = 30,
                   crouchHold = -1, steer = true, jumpAt = -1,
                   shouldStart = true, check = Check.Duration },
        new Case { name = "Flach, Augenhöhe", surface = 0, runup = 30,
                   crouchHold = 20, steer = true, jumpAt = -1,
                   shouldStart = true, check = Check.Eyes },
        new Case { name = "Ducken loslassen", surface = 0, runup = 30,
                   crouchHold = 8, steer = true, jumpAt = -1,
                   shouldStart = true, check = Check.EndsOnRelease },
        new Case { name = "Ohne Richtungstaste", surface = 0, runup = 30,
                   crouchHold = -1, steer = false, jumpAt = -1,
                   shouldStart = true, check = Check.RollsOut },
        new Case { name = "Lenken um 90 Grad", surface = 0, runup = 30,
                   crouchHold = -1, steer = true, yawTurn = 90f, jumpAt = -1,
                   shouldStart = true, check = Check.Turns },
        new Case { name = "Sprung aus dem Slide", surface = 0, runup = 30,
                   crouchHold = -1, steer = true, jumpAt = 6,
                   shouldStart = true, check = Check.JumpClamped },
        new Case { name = "Rampe 20 bergab", surface = 1, runup = 30,
                   crouchHold = -1, steer = true, jumpAt = -1,
                   shouldStart = true, check = Check.SpeedRises },
        new Case { name = "Rampe 35 bergab", surface = 2, runup = 30,
                   crouchHold = -1, steer = true, jumpAt = -1,
                   shouldStart = true, check = Check.SpeedCapped },
        new Case { name = "Rampe 35 bergab, Sprung", surface = 2, runup = 30,
                   crouchHold = -1, steer = true, jumpAt = 30,
                   shouldStart = true, check = Check.JumpClamped },
        new Case { name = "Rampe 20 bergauf", surface = 1, uphill = true,
                   runup = 30, crouchHold = -1, steer = true, jumpAt = -1,
                   shouldStart = true, check = Check.EndsEarly },
        new Case { name = "Aus dem Anlauf", surface = 0, runup = 1,
                   crouchHold = -1, steer = true, jumpAt = -1,
                   shouldStart = false, check = Check.NoStart },
        new Case { name = "Gegen eine Wand", surface = 0, wall = true,
                   runup = 60, crouchHold = -1, steer = true, jumpAt = -1,
                   shouldStart = false, check = Check.NoStart },
        new Case { name = "Auf 55 Grad, zu steil", surface = 3, runup = 30,
                   crouchHold = -1, steer = true, jumpAt = -1,
                   shouldStart = false, check = Check.NoStart },
        // Cooldown runs from the START, so a slide broken off early still
        // has to wait out the rest of it, while a full one never waits.
        new Case { name = "Erneut nach 12 Ticks", surface = 0, runup = 30,
                   crouchHold = 8, steer = true, jumpAt = -1, retryAt = 12,
                   shouldStart = true, check = Check.CooldownBlocks },
        new Case { name = "Erneut nach 22 Ticks", surface = 0, runup = 30,
                   crouchHold = 8, steer = true, jumpAt = -1, retryAt = 22,
                   shouldStart = true, check = Check.CooldownClears },
    };

    PlayerMotor motor;
    CharacterController controller;
    readonly GameObject[] grounds = new GameObject[Surfaces.Length];
    GameObject wall;

    float walkSpeed, sprintSpeed, slideDeceleration, maxSlideSpeed;
    float slideCooldown;
    float standEye, crouchEye;   // eye height above the body centre

    bool ready;
    int caseIndex = -1;
    int tick;
    int failures;
    float yaw;

    // --- per case ---
    bool started;
    bool ended;
    int slideStart = -1;
    int slideTicks;
    int slideCount;             // how often a slide started in this case
    float distance;
    float speedStart, speedEnd, speedMax;
    float airSpeed = float.NaN;
    float headingStart, headingEnd;
    float eyeLow = float.MaxValue;
    float eyeAfter = float.NaN;
    Vector3 slideFrom;

    void Start()
    {
        if (!ProbeSubject.TryBind(out motor, out controller))
            return;

        if (!Read("walkSpeed", out walkSpeed)
            || !Read("sprintSpeed", out sprintSpeed)
            || !Read("slideDeceleration", out slideDeceleration)
            || !Read("maxSlideSpeed", out maxSlideSpeed)
            || !Read("slideCooldown", out slideCooldown)
            || !ReadEye(out standEye, out crouchEye))
            return;

        BuildGround();

        float ticks = (sprintSpeed - walkSpeed) / slideDeceleration
                    / Time.fixedDeltaTime;
        Debug.Log($"[slide] Sollwerte: gehen {walkSpeed:F3}," +
                  $" sprint {sprintSpeed:F3}, Reibung {slideDeceleration:F1}," +
                  $" Deckel {maxSlideSpeed:F1} m/s," +
                  $" Pause {slideCooldown * 1000f:F0} ms" +
                  $" ({slideCooldown / Time.fixedDeltaTime:F0} Ticks)");
        Debug.Log($"[slide] flach erwartet: {ticks:F1} Ticks," +
                  $" {(sprintSpeed + walkSpeed) * 0.5f * ticks * Time.fixedDeltaTime:F2} m;" +
                  $" Auge {standEye:F3} stehend, {crouchEye:F3} geduckt;" +
                  $" Schub ab {Mathf.Asin(slideDeceleration / 20f) * Mathf.Rad2Deg:F1}°");

        // The first case waits for FixedUpdate: PhysX does not know the
        // ground created a moment ago, and settling would drop right through.
        ready = true;
    }

    // Read instead of written down: a target value that merely repeats a
    // guessed number stands there silently wrong after the next tuning pass.
    bool Read(string field, out float value)
    {
        value = 0f;
        FieldInfo f = typeof(PlayerMotor).GetField(
            field, BindingFlags.Instance | BindingFlags.NonPublic);

        if (f == null)
        {
            Debug.LogError($"[slide] Feld {field} fehlt in PlayerMotor");
            UnityEditor.EditorApplication.Exit(3);
            return false;
        }

        value = (float)f.GetValue(motor);
        return true;
    }

    // Eye height is stored above the FEET, the probe measures against the
    // transform -- and that stays at half the STANDING height whatever the
    // pose, because ApplyHeight moves the controller's centre to keep the
    // feet in place. Crouched the eyes land below it, at -0.12.
    bool ReadEye(out float stand, out float crouch)
    {
        stand = 0f;
        crouch = 0f;

        if (!Read("standEyeHeight", out float se) || !Read("crouchEyeHeight", out float ce)
            || !Read("standHeight", out float sh))
            return false;

        stand = se - sh * 0.5f;
        crouch = ce - sh * 0.5f;
        return true;
    }

    void BuildGround()
    {
        for (int i = 0; i < Surfaces.Length; i++)
        {
            // Rotated about its start, so the player always begins near y 0
            // whatever the angle, and the descent runs off in +z.
            Quaternion rot = Quaternion.Euler(Surfaces[i], 0f, 0f);
            Vector3 pivot = Origin + Vector3.left * (SurfaceGap * i);
            Vector3 centre = pivot + rot * new Vector3(0f, -0.5f, 30f);

            grounds[i] = GameObject.CreatePrimitive(PrimitiveType.Cube);
            grounds[i].name = $"SlideProbeGround{Surfaces[i]:F0}";
            grounds[i].transform.rotation = rot;
            grounds[i].transform.position = centre;
            grounds[i].transform.localScale = new Vector3(40f, 1f, 60f);
        }

        wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = "SlideProbeWall";
        wall.transform.position = Origin + new Vector3(0f, 1.5f, 12f);
        wall.transform.localScale = new Vector3(12f, 3f, 0.5f);
        wall.SetActive(false);

        // CreatePrimitive leaves the collider at the origin until the next
        // sync, so without this every surface sits 100 m away.
        Physics.SyncTransforms();
    }

    void NextCase()
    {
        caseIndex++;
        if (caseIndex >= Cases.Length)
        {
            Debug.Log(failures == 0
                ? "[slide] ALLE FÄLLE WIE ERWARTET"
                : $"[slide] {failures} ABWEICHUNGEN");
            UnityEditor.EditorApplication.Exit(failures == 0 ? 0 : 1);
            return;
        }

        Case c = Cases[caseIndex];
        wall.SetActive(c.wall);

        Quaternion rot = Quaternion.Euler(Surfaces[c.surface], 0f, 0f);
        Vector3 pivot = Origin + Vector3.left * (SurfaceGap * c.surface);

        // Uphill starts further down the face and looks back up it.
        Vector3 spot = pivot + rot * new Vector3(0f, 0f, c.uphill ? 40f : 3f);
        spot.y += 1.2f;
        yaw = c.uphill ? 180f : 0f;

        controller.enabled = false;
        motor.transform.position = spot;
        controller.enabled = true;

        // Dropped onto the face rather than placed on it: that way no angle
        // needs its start height computed, and no case inherits a velocity.
        for (int i = 0; i < 80; i++)
            motor.Tick(new MoveInput { Yaw = yaw }, Time.fixedDeltaTime);

        if (!motor.IsGrounded && c.surface != 3)
        {
            Debug.LogError($"[slide] {c.name}: kein Boden bei" +
                           $" {motor.transform.position}, Messung sinnlos");
            UnityEditor.EditorApplication.Exit(4);
            return;
        }

        tick = 0;
        started = false;
        ended = false;
        slideStart = -1;
        slideTicks = 0;
        slideCount = 0;
        distance = 0f;
        speedStart = 0f;
        speedEnd = 0f;
        speedMax = 0f;
        airSpeed = float.NaN;
        headingStart = 0f;
        headingEnd = 0f;
        eyeLow = float.MaxValue;
        eyeAfter = float.NaN;
        slideFrom = Vector3.zero;

        Debug.Log($"[slide] == {c.name} ({Surfaces[c.surface]:F0}°" +
                  $"{(c.uphill ? " bergauf" : "")}), Boden bei" +
                  $" {motor.GroundAngle:F1}°");
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

        Case c = Cases[caseIndex];
        float dt = Time.fixedDeltaTime;

        bool crouch = tick >= c.runup
                   && (c.crouchHold < 0 || tick < c.runup + c.crouchHold);

        // Pressed again after the first slide was let go, to see whether the
        // cooldown still blocks. Held from there on.
        if (c.retryAt > 0 && started && tick >= slideStart + c.retryAt)
            crouch = true;
        bool inSlide = started && !ended;

        byte jump = 0;
        if (c.jumpAt >= 0 && inSlide && tick == slideStart + c.jumpAt)
            jump = 1;


        float turn = inSlide && c.yawTurn != 0f
            ? c.yawTurn * Mathf.Clamp01((tick - slideStart) / 12f)
            : 0f;

        Vector3 before = motor.transform.position;
        bool wasSliding = motor.IsSliding;

        motor.Tick(new MoveInput
        {
            Move = (tick < c.runup || c.steer) ? new Vector2(0f, 1f) : Vector2.zero,
            Yaw = yaw + turn,
            Sprint = tick < c.runup,
            Crouch = crouch,
            JumpPresses = jump,
        }, dt);

        Vector3 step = motor.transform.position - before;
        float flat = new Vector2(step.x, step.z).magnitude / dt;

        // Only the FIRST slide of a case is measured. Downhill the player
        // slides again the moment he lands, which otherwise overwrote the
        // start values and fired a second jump.
        if (motor.IsSliding && !wasSliding)
            slideCount++;

        if (motor.IsSliding && !ended)
        {
            if (!wasSliding)
            {
                started = true;
                slideStart = tick;
                speedStart = flat;
                slideFrom = before;
                headingStart = Heading();
            }

            slideTicks++;
            if (flat > speedMax)
                speedMax = flat;
            speedEnd = flat;
            headingEnd = Heading();

            // Tracked every tick, not only at the end: a slide that never
            // ends (downhill, key held) would otherwise report zero.
            distance = new Vector2(motor.transform.position.x - slideFrom.x,
                                   motor.transform.position.z - slideFrom.z).magnitude;

            float eye = motor.EyePosition.y - motor.transform.position.y;
            if (eye < eyeLow)
                eyeLow = eye;
        }
        else if (wasSliding && !ended)
        {
            ended = true;

            // The first airborne tick after a slide jump: whatever it keeps
            // here is what the flight will carry.
            if (!motor.IsGrounded)
                airSpeed = new Vector2(motor.Velocity.x, motor.Velocity.z).magnitude;
        }

        // Watched for a while after the slide, so the eyes have time to rise.
        if (ended && float.IsNaN(eyeAfter) && motor.IsGrounded && !crouch
            && motor.EyePosition.y - motor.transform.position.y > standEye - 0.005f)
            eyeAfter = motor.EyePosition.y - motor.transform.position.y;

        tick++;

        bool done = tick >= c.runup + 160
                 || (ended && tick > slideStart + slideTicks + 40);
        if (done)
        {
            Report(c);
            NextCase();
        }
    }

    float Heading()
    {
        Vector3 v = motor.Velocity;
        return Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
    }

    void Report(Case c)
    {
        if (started != c.shouldStart)
        {
            failures++;
            Debug.Log($"[slide]    {(started ? "startete, sollte nicht" : "startete NICHT, sollte")}" +
                      $" -> ABWEICHUNG");
            return;
        }

        if (!started)
        {
            Debug.Log("[slide]    kein Slide, wie erwartet -> ok");
            return;
        }

        float wantTicks = (sprintSpeed - walkSpeed) / slideDeceleration
                        / Time.fixedDeltaTime;
        bool ok;
        string note;

        switch (c.check)
        {
            case Check.Duration:
                ok = Mathf.Abs(slideTicks - wantTicks) <= 2f
                     && speedEnd < walkSpeed + 0.3f;
                note = $"{slideTicks} Ticks gegen {wantTicks:F1} erwartet";
                break;
            case Check.EndsOnRelease:
                // Ends the tick after the key goes, and still fast doing it.
                ok = slideTicks <= c.crouchHold + 1 && speedEnd > walkSpeed;
                note = $"{slideTicks} Ticks bei {c.crouchHold} gehalten," +
                       $" Ende noch {speedEnd:F3} m/s";
                break;
            case Check.SpeedRises:
                ok = speedMax > speedStart + 0.2f;
                note = $"von {speedStart:F3} auf {speedMax:F3} m/s";
                break;
            case Check.SpeedCapped:
                ok = speedMax > sprintSpeed && speedMax <= maxSlideSpeed + 0.3f;
                note = $"Spitze {speedMax:F3} gegen Deckel {maxSlideSpeed:F1}";
                break;
            case Check.EndsEarly:
                ok = slideTicks < wantTicks - 2f;
                note = $"{slideTicks} Ticks, flach waeren {wantTicks:F1}";
                break;
            case Check.JumpClamped:
                ok = !float.IsNaN(airSpeed) && airSpeed <= sprintSpeed + 0.05f;
                note = $"in der Luft {airSpeed:F3} gegen {sprintSpeed:F3} erlaubt";
                break;
            case Check.Turns:
                float turned = Mathf.Abs(Mathf.DeltaAngle(headingStart, headingEnd));
                ok = turned > 60f;
                note = $"Bahn drehte {turned:F1}° von {c.yawTurn:F0}° Blick";
                break;
            case Check.CooldownBlocks:
                ok = slideCount == 1;
                note = $"{slideCount} Slide(s), erneut {c.retryAt} Ticks" +
                       $" nach dem Start versucht, Pause ist" +
                       $" {slideCooldown / Time.fixedDeltaTime:F0} Ticks";
                break;
            case Check.CooldownClears:
                ok = slideCount == 2;
                note = $"{slideCount} Slide(s), erneut {c.retryAt} Ticks" +
                       $" nach dem Start versucht, Pause ist" +
                       $" {slideCooldown / Time.fixedDeltaTime:F0} Ticks";
                break;
            case Check.RollsOut:
                ok = distance > 2.5f && slideTicks > wantTicks - 3f;
                note = $"{distance:F2} m ohne Taste, {slideTicks} Ticks";
                break;
            default:   // Eyes
                ok = Mathf.Abs(eyeLow - crouchEye) < 0.02f
                     && !float.IsNaN(eyeAfter);
                note = $"Auge sank auf {eyeLow:F3} (soll {crouchEye:F3})," +
                       $" danach {(float.IsNaN(eyeAfter) ? "NICHT zurueck" : $"zurueck auf {eyeAfter:F3}")}";
                break;
        }

        if (!ok)
            failures++;

        Debug.Log($"[slide]    {note}; Start {speedStart:F3}," +
                  $" Spitze {speedMax:F3}, Ende {speedEnd:F3} m/s," +
                  $" {distance:F2} m -> {(ok ? "ok" : "ABWEICHUNG")}");
    }
}
#endif
