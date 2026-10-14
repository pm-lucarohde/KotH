#if UNITY_EDITOR
using System.Reflection;
using KotH.Shared.Player;
using UnityEngine;

/// <summary>
/// Measures how the jump height follows the hold time. Checked at the cut
/// itself, not at the peak: Euler integration loses v*dt/2 of every jump, an
/// amount that shrinks with the cut and would distort a peak comparison.
/// </summary>
public sealed class JumpHeightProbe : MonoBehaviour
{
    const int MaxFlightTicks = 140;
    const int SettleTicks = 40;

    // Flat and clear: CrouchTest sits within z +-1.5, StepTest near z -10,
    // the mountain beyond z 10. The ground reaches x and z -25.
    static readonly Vector3 Spot = new Vector3(-20f, 0.9f, -20f);

    struct Case
    {
        public string name;
        public int hold;          // ticks the key stays down, 0 = never
        public bool doubleJump;   // tap twice in the air afterwards
    }

    static readonly Case[] Cases =
    {
        new Case { name = "JumpHeld vergessen", hold = 0 },
        new Case { name = "1 Tick (25 ms)",     hold = 1 },
        new Case { name = "2 Ticks (50 ms)",    hold = 2 },
        new Case { name = "3 Ticks (75 ms)",    hold = 3 },
        new Case { name = "4 Ticks (100 ms)",   hold = 4 },
        new Case { name = "5 Ticks (125 ms)",   hold = 5 },
        new Case { name = "6 Ticks (150 ms)",   hold = 6 },
        new Case { name = "7 Ticks (175 ms)",   hold = 7 },
        new Case { name = "8 Ticks (200 ms)",   hold = 8 },
        new Case { name = "9 Ticks (225 ms)",   hold = 9 },
        new Case { name = "12 Ticks, ungekappt", hold = 12 },
        new Case { name = "Doppelsprung nach Tipp", hold = 1, doubleJump = true },
    };

    PlayerMotor motor;
    CharacterController controller;

    float jumpHeight;
    float minJumpHeight;
    float minJumpHold;
    float fullJumpHold;
    float doubleJumpHeight;
    float gravity;

    int caseIndex = -1;
    int tick;
    int failures;
    float startY;
    float peak;
    float risenAtCut;                    // height climbed when the cut landed
    float speedAfterCut = float.NaN;     // vertical speed the cut tick left
    float doubleJumpSpeed = float.NaN;   // vertical speed right after it fired
    readonly float[] peaks = new float[Cases.Length];

    void Start()
    {
        if (!ProbeSubject.TryBind(out motor, out controller))
            return;

        if (!Read("jumpHeight", out jumpHeight)
            || !Read("minJumpHeight", out minJumpHeight)
            || !Read("minJumpHold", out minJumpHold)
            || !Read("fullJumpHold", out fullJumpHold)
            || !Read("doubleJumpHeight", out doubleJumpHeight)
            || !Read("gravity", out gravity))
            return;

        Debug.Log($"[jump] Sollwerte: voll {jumpHeight:F3} m," +
                  $" minimal {minJumpHeight:F3} m, Haltespanne" +
                  $" {minJumpHold * 1000f:F0}-{fullJumpHold * 1000f:F0} ms," +
                  $" doppelt {doubleJumpHeight:F3} m, g {gravity:F1}");
        NextCase();
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
            Debug.LogError($"[jump] Feld {field} fehlt in PlayerMotor");
            UnityEditor.EditorApplication.Exit(3);
            return false;
        }

        value = (float)f.GetValue(motor);
        return true;
    }

    void NextCase()
    {
        caseIndex++;
        if (caseIndex >= Cases.Length)
        {
            CheckCurve();
            return;
        }

        controller.enabled = false;
        motor.transform.position = Spot;
        controller.enabled = true;

        // Standing still until grounded is certain: a jump that starts in
        // the air would measure the fall, not the jump.
        for (int i = 0; i < SettleTicks; i++)
            motor.Tick(new MoveInput { Yaw = 0f }, Time.fixedDeltaTime);

        if (!motor.IsGrounded)
        {
            Debug.LogError($"[jump] {Cases[caseIndex].name}: kein Boden unter" +
                           $" {Spot.x:F1}/{Spot.z:F1}, Messung waere sinnlos");
            UnityEditor.EditorApplication.Exit(4);
            return;
        }

        tick = 0;
        startY = motor.transform.position.y;
        peak = 0f;
        risenAtCut = 0f;
        speedAfterCut = float.NaN;
        doubleJumpSpeed = float.NaN;
    }

    void FixedUpdate()
    {
        if (caseIndex < 0 || caseIndex >= Cases.Length)
            return;

        Case c = Cases[caseIndex];

        byte presses = 0;
        if (tick == 0)
            presses = 1;
        else if (c.doubleJump && (tick == 3 || tick == 4))
            presses = 1;   // two air taps: the second one fires

        // The cut reads the height climbed SO FAR, before this tick moves
        // the body, so it has to be sampled before the tick.
        bool cutTick = tick == c.hold
                    && c.hold * Time.fixedDeltaTime < fullJumpHold;
        if (cutTick)
            risenAtCut = motor.transform.position.y - startY;

        motor.Tick(new MoveInput
        {
            Yaw = 0f,
            JumpPresses = presses,
            JumpHeld = tick < c.hold,
        }, Time.fixedDeltaTime);

        if (cutTick)
            speedAfterCut = motor.Velocity.y;

        if (c.doubleJump && tick == 4)
            doubleJumpSpeed = motor.Velocity.y;

        float risen = motor.transform.position.y - startY;
        if (risen > peak)
            peak = risen;

        tick++;

        // Landed again, or out of patience. The grace ticks keep the take-off
        // tick itself from ending the case before the body has left ground.
        if ((motor.IsGrounded && tick > 6) || tick >= MaxFlightTicks)
        {
            Report(c);
            NextCase();
        }
    }

    void Report(Case c)
    {
        peaks[caseIndex] = peak;

        string extra = "";
        bool ok = true;

        // The cut itself: trimmed speed must be exactly what is needed to
        // climb the rest of the earned height, plus this tick's gravity.
        if (!float.IsNaN(speedAfterCut))
        {
            float held = c.hold * Time.fixedDeltaTime;
            float t = Mathf.InverseLerp(minJumpHold, fullJumpHold, held);
            float target = Mathf.Lerp(minJumpHeight, jumpHeight, t);
            float want = Mathf.Sqrt(Mathf.Max(0f, (target - risenAtCut) * -2f * gravity))
                       + gravity * Time.fixedDeltaTime;

            ok = Mathf.Abs(speedAfterCut - want) < 0.005f;
            extra = $", Soll {target:F3} m, gestiegen {risenAtCut:F3}," +
                    $" Schnitt {speedAfterCut:F3} gegen {want:F3} m/s" +
                    $" {(ok ? "ok" : "ABWEICHUNG")}";
        }

        if (c.doubleJump)
        {
            // Measured against the formula, including the gravity of the very
            // tick it fired in: that is the order inside Tick.
            float want = Mathf.Sqrt(doubleJumpHeight * -2f * gravity)
                       + gravity * Time.fixedDeltaTime;
            ok = Mathf.Abs(doubleJumpSpeed - want) < 0.02f;
            extra = $", Absprung doppelt {doubleJumpSpeed:F3}" +
                    $" gegen {want:F3} m/s {(ok ? "ok" : "ABWEICHUNG")}";
        }

        if (!ok)
            failures++;

        Debug.Log($"[jump] {c.name,-24} gehalten {c.hold,2} Ticks" +
                  $" ({c.hold * Time.fixedDeltaTime * 1000f,3:F0} ms):" +
                  $" Gipfel {peak:F3} m nach {tick,3} Ticks{extra}");
    }

    // The peaks are judged on shape only -- monotone and spanning the range.
    // Their absolute values fall short of the target by v*dt/2 of Euler
    // error, which is the integrator's doing and not the cut's.
    void CheckCurve()
    {
        float low = peaks[1];                 // 1 tick, the minimum
        float high = peaks[Cases.Length - 2]; // 12 ticks, uncut
        float span = high - low;

        Debug.Log($"[jump] == Gipfel von {low:F3} bis {high:F3} m," +
                  $" Spanne {span:F3} gegen eingestellte" +
                  $" {jumpHeight - minJumpHeight:F3} m");
        Debug.Log($"[jump]    Euler frisst oben {jumpHeight - high:F3} m," +
                  $" unten {minJumpHeight - low:F3} m" +
                  $" -- gilt genauso fuer den ungekappten Sprung");

        if (span < 0.9f * (jumpHeight - minJumpHeight))
        {
            Debug.LogError("[jump] Spanne zu klein, die Kappung greift falsch");
            failures++;
        }

        float previous = -1f;
        for (int i = 0; i < Cases.Length; i++)
        {
            if (Cases[i].doubleJump)
                continue;

            // Monotone: a longer hold may never end up lower than a shorter.
            bool up = peaks[i] >= previous - 0.001f;
            previous = peaks[i];

            if (!up)
            {
                failures++;
                Debug.Log($"[jump]    {Cases[i].hold,2} Ticks:" +
                          $" {peaks[i]:F3} m NICHT MONOTON");
            }
        }

        Debug.Log(failures == 0
            ? "[jump] ALLE FÄLLE WIE ERWARTET"
            : $"[jump] {failures} ABWEICHUNGEN");
        UnityEditor.EditorApplication.Exit(failures == 0 ? 0 : 1);
    }
}
#endif
