using KotH.Probes.Harness;
using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using Stride.Core.Mathematics;

namespace KotH.Probes.Probes
{
    /// <summary>
    /// Measures how the jump height follows the hold time. Checked at the cut
    /// itself, not at the peak: Euler integration loses v*dt/2 of every jump, an
    /// amount that shrinks with the cut and would distort a peak comparison.
    /// </summary>
    public sealed class JumpHeightProbe : Probe
    {
        protected override string Tag => "jump";

        const int MaxFlightTicks = 140;
        const int SettleTicks = 40;

        // The heights Luca asked for, in metres actually reached. They live here
        // rather than in the motor because the motor's fields carry the
        // pre-correction for the Euler loss and no longer state the real peak.
        const float WantedFullPeak = 1.5f;
        const float WantedMinPeak = 1.0f;
        const float WantedDoublePeak = 1.0f;
        const float PeakTolerance = 0.01f;

        // Flat and clear: CrouchTest sits within z +-1.5, StepTest near z -10,
        // the mountain beyond z 10. The ground reaches x and z -25. Unity coordinates.
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
        float doubleJumpFrom;                // height it fired at, peak counts from there
        readonly float[] peaks = new float[Cases.Length];

        protected override void Start()
        {
            motor = Prototype.Open(World).Motor;

            // Read instead of written down: a target value that merely repeats a
            // guessed number stands there silently wrong after the next tuning
            // pass. Unity read them by reflection, here they are public fields.
            jumpHeight = motor.JumpHeight;
            minJumpHeight = motor.MinJumpHeight;
            minJumpHold = motor.MinJumpHold;
            fullJumpHold = motor.FullJumpHold;
            doubleJumpHeight = motor.DoubleJumpHeight;
            gravity = motor.Gravity;

            Log($"[jump] Sollwerte: voll {jumpHeight:F3} m," +
                $" minimal {minJumpHeight:F3} m, Haltespanne" +
                $" {minJumpHold * 1000f:F0}-{fullJumpHold * 1000f:F0} ms," +
                $" doppelt {doubleJumpHeight:F3} m, g {gravity:F1}");
            NextCase();
        }

        void NextCase()
        {
            caseIndex++;
            if (caseIndex >= Cases.Length)
            {
                CheckCurve();
                return;
            }

            motor.Teleport(Ref.Pos(Spot));

            // Standing still until grounded is certain: a jump that starts in
            // the air would measure the fall, not the jump.
            for (int i = 0; i < SettleTicks; i++)
                motor.Tick(new MoveInput { Yaw = Ref.Angle(0f) }, FixedDeltaTime);

            if (!motor.IsGrounded)
            {
                LogError($"[jump] {Cases[caseIndex].name}: kein Boden unter" +
                         $" {Spot.X:F1}/{Spot.Z:F1}, Messung waere sinnlos");
                Exit(4);
                return;
            }

            tick = 0;
            startY = motor.Position.Y;
            peak = 0f;
            risenAtCut = 0f;
            speedAfterCut = float.NaN;
            doubleJumpSpeed = float.NaN;
        }

        protected override void FixedUpdate()
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
                        && c.hold * FixedDeltaTime < fullJumpHold;
            if (cutTick)
                risenAtCut = motor.Position.Y - startY;

            // Same reason: the double jump climbs from where the body stands
            // BEFORE this tick moves it, not from where it ends up.
            if (c.doubleJump && tick == 4)
                doubleJumpFrom = motor.Position.Y - startY;

            motor.Tick(new MoveInput
            {
                Yaw = Ref.Angle(0f),
                JumpPresses = presses,
                JumpHeld = tick < c.hold,
            }, FixedDeltaTime);

            if (cutTick)
                speedAfterCut = motor.Velocity.Y;

            if (c.doubleJump && tick == 4)
                doubleJumpSpeed = motor.Velocity.Y;

            float risen = motor.Position.Y - startY;
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
                float held = c.hold * FixedDeltaTime;
                float t = UMath.InverseLerp(minJumpHold, fullJumpHold, held);
                float target = UMath.Lerp(minJumpHeight, jumpHeight, t);
                float want = UMath.Sqrt(UMath.Max(0f, (target - risenAtCut) * -2f * gravity))
                           + gravity * FixedDeltaTime;

                ok = UMath.Abs(speedAfterCut - want) < 0.005f;
                extra = $", Soll {target:F3} m, gestiegen {risenAtCut:F3}," +
                        $" Schnitt {speedAfterCut:F3} gegen {want:F3} m/s" +
                        $" {(ok ? "ok" : "ABWEICHUNG")}";
            }

            if (c.doubleJump)
            {
                // Measured against the formula, including the gravity of the very
                // tick it fired in: that is the order inside Tick.
                float want = UMath.Sqrt(doubleJumpHeight * -2f * gravity)
                           + gravity * FixedDeltaTime;
                ok = UMath.Abs(doubleJumpSpeed - want) < 0.02f;

                // Its peak counts from where it fired, not from the ground.
                float climbed = peak - doubleJumpFrom;
                bool peakOk = UMath.Abs(climbed - WantedDoublePeak) < PeakTolerance;
                if (!peakOk)
                    failures++;

                extra = $", Absprung {doubleJumpSpeed:F3} gegen {want:F3} m/s" +
                        $" {(ok ? "ok" : "ABWEICHUNG")}, ab {doubleJumpFrom:F3} m" +
                        $" noch {climbed:F3} gegen {WantedDoublePeak:F3}" +
                        $" {(peakOk ? "ok" : "ABWEICHUNG")}";
            }

            if (!ok)
                failures++;

            Log($"[jump] {c.name,-24} gehalten {c.hold,2} Ticks" +
                $" ({c.hold * FixedDeltaTime * 1000f,3:F0} ms):" +
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

            Log($"[jump] == Gipfel von {low:F3} bis {high:F3} m," +
                $" Spanne {span:F3} gegen eingestellte" +
                $" {jumpHeight - minJumpHeight:F3} m");
            Log($"[jump]    Euler frisst oben {jumpHeight - high:F3} m," +
                $" unten {minJumpHeight - low:F3} m" +
                $" -- die Felder tragen das vorkorrigiert");

            // The point of the pre-correction: the real peaks, not the fields.
            CheckPeak("volle Hoehe", high, WantedFullPeak);
            CheckPeak("Mindesthoehe", low, WantedMinPeak);

            if (span < 0.9f * (jumpHeight - minJumpHeight))
            {
                LogError("[jump] Spanne zu klein, die Kappung greift falsch");
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
                    Log($"[jump]    {Cases[i].hold,2} Ticks:" +
                        $" {peaks[i]:F3} m NICHT MONOTON");
                }
            }

            Log(failures == 0
                ? "[jump] ALLE FÄLLE WIE ERWARTET"
                : $"[jump] {failures} ABWEICHUNGEN");
            Exit(failures == 0 ? 0 : 1);
        }

        void CheckPeak(string what, float measured, float wanted)
        {
            bool ok = UMath.Abs(measured - wanted) < PeakTolerance;
            if (!ok)
                failures++;

            Log($"[jump]    {what,-14} erreicht {measured:F3} m," +
                $" gewollt {wanted:F3} m, ab {measured - wanted:+0.000;-0.000}" +
                $" -> {(ok ? "ok" : "ABWEICHUNG")}");
        }
    }
}
