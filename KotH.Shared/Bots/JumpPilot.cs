using System;
using KotH.Shared.Mathematics;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;

namespace KotH.Shared.Bots
{
    /// <summary>Jumps a bot up a step the way a careful player does. A small motor model (keys,
    /// sprint, air control, jump, double jump) runs on the grid's surfaces: the bot flies each jump
    /// in the model first and only goes when it lands on top; in the air it picks the keys that
    /// bring it down there without touching the lip.</summary>
    public sealed class JumpPilot
    {
        /// <summary>One jump up (or across), measured along the line from its take-off floor.</summary>
        public struct Hop
        {
            public Vector3 From;         // feet on the take-off floor
            public float DX, DZ;         // unit, along the jump
            public float Edge;           // metres along to where the own level ends, MaxValue for a step
            public float Back;           // how far back (negative) the body can stand on the own level
            public float Lip;            // to where the top starts
            public float Target;         // where the feet should come down
            public float End;            // where the top ends
            public float Rise;           // the top above the take-off
            public float Need;           // the feet must be this high before the body passes the lip
            public bool Double;
        }

        /// <summary>The model's body: feet, velocity and the motor's few flags that matter.</summary>
        public struct Body
        {
            public float X, Y, Z, VX, VY, VZ;
            public bool Grounded, Doubled, Sprinting, Armed;
            public float LastSpeed;
            public int Standing;         // ticks on the ground; the model may touch down a tick early
            public bool Settled;         // seen on the ground and not off it since: the model knows where it stands
        }

        const float Dt = SimulationSystem.TickSeconds;
        const float Clearance = 0.1f;    // over the lip, metres
        const int MaxFlight = 60;

        readonly ClimbGrid grid;
        readonly float walk, sprint, groundAcc, groundDec, airAcc, jumpSpeed, doubleSpeed, doubleHeight, gravity, radius;

        public JumpPilot(ClimbGrid grid, PlayerMotor motor)
        {
            this.grid = grid;
            walk = motor.WalkSpeed;
            sprint = motor.SprintSpeed;
            groundAcc = motor.GroundAcceleration;
            groundDec = motor.GroundDeceleration;
            airAcc = motor.AirAcceleration;
            gravity = motor.Gravity;
            jumpSpeed = UMath.Sqrt(motor.JumpHeight * -2f * gravity);
            doubleSpeed = UMath.Sqrt(motor.DoubleJumpHeight * -2f * gravity);
            doubleHeight = motor.DoubleJumpHeight;
            radius = motor.BodyRadius;
        }

        public float Radius => radius;

        /// <summary>Takes off only once the bot has seen itself standing; a landing can turn its speed unseen.</summary>
        public bool SettledOnly = true;

        // ---------- the hop ----------

        float TopAt(in Hop h, float s, float above)
        {
            float y = grid.SurfaceAt(h.From.X + h.DX * s, h.From.Z + h.DZ * s, h.From.Y + above);
            return y == float.MinValue ? -100f : y - h.From.Y;
        }

        /// <summary>The hop from a take-off floor to a landing floor, out of the surfaces
        /// along the line: where the own level ends, where the top starts and ends, and
        /// how high the body must be to pass the lip.</summary>
        public bool Prepare(Vector3 from, Vector3 to, out Hop hop)
        {
            hop = default;
            float dx = to.X - from.X, dz = to.Z - from.Z;
            float reach = UMath.Sqrt(dx * dx + dz * dz);
            if (reach < 0.1f)
                return false;
            hop.From = from;
            hop.DX = dx / reach;
            hop.DZ = dz / reach;
            float rise = to.Y - from.Y;
            float above = UMath.Max(0f, rise) + 3f;
            hop.Edge = float.MaxValue;
            hop.Lip = float.MaxValue;
            float obstacle = 0f;
            for (float s = 0.1f; s <= reach + 0.25f; s += 0.1f)
            {
                float top = TopAt(hop, s, above);
                if (top >= rise - 0.35f && (rise > 0.35f || hop.Edge != float.MaxValue))
                {
                    hop.Lip = s;
                    break;
                }
                if (top < -0.6f && hop.Edge == float.MaxValue)
                    hop.Edge = s;
                obstacle = UMath.Max(obstacle, top);
            }
            if (hop.Lip == float.MaxValue)
                return false;
            // Behind the take-off: room for a run-up only while the own level goes on.
            hop.Back = -1.5f;
            for (float s = -0.1f; s >= -1.5f; s -= 0.1f)
                if (UMath.Abs(TopAt(hop, s, above)) > 0.35f)
                {
                    hop.Back = s + radius + 0.05f;
                    break;
                }
            hop.End = hop.Lip + 3f;
            for (float s = hop.Lip + 0.1f; s <= hop.Lip + 3f; s += 0.1f)
            {
                float top = TopAt(hop, s, above);
                if (UMath.Abs(top - rise) > 0.35f)
                {
                    hop.End = s;
                    break;
                }
            }
            hop.Rise = rise;
            hop.Need = UMath.Max(rise, obstacle) + Clearance;
            hop.Target = hop.Lip + UMath.Clamp((hop.End - hop.Lip) * 0.5f, 0.3f, 0.6f);
            return true;
        }

        // ---------- the model ----------

        /// <summary>Feet and velocity of a motor state, as the model keeps them.</summary>
        public Body From(in MotorState m, float standHeight)
        {
            float flat = UMath.Sqrt(m.Velocity.X * m.Velocity.X + m.Velocity.Z * m.Velocity.Z);
            return new Body
            {
                X = m.Position.X, Y = m.Position.Y - standHeight * 0.5f, Z = m.Position.Z,
                VX = m.Velocity.X, VY = m.Grounded ? 0f : m.Velocity.Y, VZ = m.Velocity.Z,
                Grounded = m.Grounded, Standing = m.Grounded ? 2 : 0, Settled = m.Grounded, Doubled = m.DoubleJumpUsed, Sprinting = m.Sprinting, Armed = m.SprintArmed,
                LastSpeed = m.LastStepSpeed,
            };
        }

        static void Axes(float yaw, out float fx, out float fz, out float rx, out float rz)
        {
            float r = yaw * (MathF.PI / 180f);
            fx = -UMath.Sin(r);
            fz = -UMath.Cos(r);
            rx = UMath.Cos(r);
            rz = -UMath.Sin(r);
        }

        // The horizontal velocity the motor makes of some keys in one tick.
        void Accelerate(ref Body b, float yaw, Vector2 move, bool wantsSprint, out float vx, out float vz)
        {
            Axes(yaw, out float fx, out float fz, out float rx, out float rz);
            Accelerate(ref b, fx, fz, rx, rz, move, wantsSprint, out vx, out vz);
        }

        // The same with the view's axes already worked out, for trying all keys at one yaw.
        void Accelerate(ref Body b, float fx, float fz, float rx, float rz, Vector2 move, bool wantsSprint,
                        out float vx, out float vz)
        {
            move = UMath.ClampMagnitude(move, 1f);
            bool has = move.X * move.X + move.Y * move.Y > 0.0001f;
            bool sprinting = b.Sprinting || wantsSprint;
            float speed = sprinting ? sprint : walk;
            float wx = (rx * move.X + fx * move.Y) * speed, wz = (rz * move.X + fz * move.Y) * speed;
            float rate = (b.Grounded ? (has ? groundAcc : groundDec) : (has ? airAcc : 0f)) * Dt;
            float ex = wx - b.VX, ez = wz - b.VZ, e = UMath.Sqrt(ex * ex + ez * ez);
            if (e <= rate || e < 1e-6f)
            {
                vx = wx;
                vz = wz;
            }
            else
            {
                vx = b.VX + ex / e * rate;
                vz = b.VZ + ez / e * rate;
            }
        }

        // The highest top under the body's lower sphere, against its height there.
        bool Hits(float x, float z, float feet, float ux, float uz)
        {
            for (int k = 1; k <= 4; k++)
            {
                float d = radius * k * 0.25f;
                float bottom = feet + radius - UMath.Sqrt(UMath.Max(0f, radius * radius - d * d));
                float top = grid.SurfaceAt(x + ux * d, z + uz * d, feet + 3f);
                if (top > bottom + 0.02f)
                    return true;
            }
            return false;
        }

        static readonly float[] RingX = new float[8], RingZ = new float[8];

        static JumpPilot()
        {
            for (int k = 0; k < 8; k++)
            {
                RingX[k] = DetMath.CosF(k * MathF.PI / 4f);
                RingZ[k] = DetMath.SinF(k * MathF.PI / 4f);
            }
        }

        // Where the feet rest with the body's round bottom on the surfaces
        // under it: the highest of them, each lowered by how far the sphere
        // curves up at its distance from the middle.
        float Support(float x, float z, float under)
        {
            float best = grid.SurfaceAt(x, z, under);
            for (int ring = 1; ring <= 2; ring++)
            {
                float d = radius * (ring == 1 ? 0.5f : 0.9f);
                float drop = radius - UMath.Sqrt(radius * radius - d * d);
                for (int k = 0; k < 8; k++)
                {
                    float top = grid.SurfaceAt(x + RingX[k] * d, z + RingZ[k] * d, under);
                    if (top != float.MinValue)
                        best = UMath.Max(best, top - drop);
                }
            }
            return best;
        }

        /// <summary>One tick of the model, in the motor's order: sprint, keys, jump, gravity, move, ground.</summary>
        public void Step(ref Body b, in MoveInput input)
        {
            // Contacts first, as the motor's tick: a body touching down now can jump now.
            if (!b.Grounded && b.VY <= 0f)
            {
                float under = Support(b.X, b.Z, b.Y + 0.05f);
                if (b.Y <= under + 0.05f)
                {
                    b.Y = under;
                    b.VY = 0f;
                    b.Grounded = true;
                }
            }
            if (input.Sprint)
            {
                b.Sprinting = true;
                b.Armed = false;
            }
            if (b.Sprinting)
            {
                if (!b.Armed)
                    b.Armed = b.LastSpeed > walk;
                else if (b.LastSpeed < walk)
                    b.Sprinting = false;
            }
            Accelerate(ref b, input.Yaw, input.Move, false, out float vx, out float vz);
            b.VX = vx;
            b.VZ = vz;

            bool jumped = false;
            if (input.JumpPresses > 0 && b.Grounded)
            {
                b.VY = jumpSpeed;
                b.Grounded = false;
                b.Doubled = false;
                jumped = true;
            }
            if (input.JumpPresses >= 2 && !jumped && !b.Grounded && !b.Doubled)
            {
                b.VY = doubleSpeed;
                b.Doubled = true;
            }
            if (!b.Grounded)
                b.VY += gravity * Dt;

            float x = b.X + b.VX * Dt, z = b.Z + b.VZ * Dt, y = b.Grounded ? b.Y : b.Y + b.VY * Dt;
            float flat = UMath.Sqrt(b.VX * b.VX + b.VZ * b.VZ);
            if (flat > 1e-4f && Hits(x, z, b.Grounded ? b.Y + 0.45f : y, b.VX / flat, b.VZ / flat))
            {
                // Against a wall or a lip: the way on is blocked.
                x = b.X;
                z = b.Z;
                b.VX = b.VZ = 0f;
                flat = 0f;
            }
            b.LastSpeed = flat;

            float ground = Support(x, z, (b.Grounded ? b.Y + 0.5f : UMath.Max(b.Y, y)) + 0.01f);
            if (b.Grounded)
            {
                if (ground < b.Y - 0.6f)
                {
                    b.Grounded = false;
                    b.VY = 0f;
                }
                else
                    y = ground;
            }
            else if (b.VY <= 0f && y <= ground)
            {
                y = ground;
                b.VY = 0f;
                b.Grounded = true;
            }
            b.X = x;
            b.Y = y;
            b.Z = z;
            b.Standing = b.Grounded ? Math.Min(b.Standing + 1, 100) : 0;
            b.Settled &= b.Grounded;
        }

        // ---------- flying ----------

        // When the second press of a double jump is due: once it lifts the
        // feet well over the lip, at the latest at the top of the first jump.
        bool DoubleDue(in Hop h, in Body b)
            => h.Double && !b.Doubled && !b.Grounded && (b.Y - h.From.Y + doubleHeight * 0.95f >= h.Need + 0.35f || b.VY <= 0.5f);

        // Ticks from now until the feet are past the lip's height, and until
        // they come down onto the top (or to their apex, if never above it).
        void Ahead(in Hop h, Body b, out int clear, out int land)
        {
            clear = -1;
            land = MaxFlight;
            float y = b.Y - h.From.Y, vy = b.VY;
            bool doubled = b.Doubled, above = false;
            for (int k = 0; k < MaxFlight; k++)
            {
                if (clear < 0 && y >= h.Need)
                    clear = k;
                above |= y >= h.Rise;
                if (vy <= 0f && (y <= h.Rise && above || !above && k > 0))
                {
                    land = k;
                    return;
                }
                if (h.Double && !doubled && (y + doubleHeight * 0.95f >= h.Need + 0.35f || vy <= 0.5f))
                {
                    vy = doubleSpeed;
                    doubled = true;
                }
                vy += gravity * Dt;
                y += vy * Dt;
            }
        }

        static float Want(float left, float time, float acc)
        {
            float far = UMath.Abs(left);
            float v = UMath.Min(far / UMath.Max(time, Dt), UMath.Sqrt(2f * acc * far));
            return left < 0f ? -v : v;
        }

        /// <summary>The keys for one tick of a hop, the body facing yaw: towards the
        /// target as fast as the time left allows and slow enough to stop on it; held
        /// back while the feet are still under the lip.</summary>
        public MoveInput Fly(in Hop h, in Body b, float yaw)
        {
            float ox = b.X - h.From.X, oz = b.Z - h.From.Z;
            float s = ox * h.DX + oz * h.DZ, w = -ox * h.DZ + oz * h.DX;
            Ahead(h, b, out int clear, out int land);
            float time = land * Dt;
            float along = Want(h.Target - s, time, airAcc);
            if (clear != 0 && s < h.Lip)
            {
                float room = h.Lip - radius - 0.02f - s;
                float hold = clear < 0 ? room / 0.3f : room / UMath.Max(clear * Dt, Dt);
                along = UMath.Min(along, hold);
            }
            float aside = Want(-w, UMath.Max(time, 0.15f), airAcc);
            float tx = h.DX * along - h.DZ * aside, tz = h.DZ * along + h.DX * aside;

            var input = new MoveInput { Yaw = yaw };
            Body probe = b;
            float best = float.MaxValue;
            Axes(yaw, out float fx, out float fz, out float rx, out float rz);
            for (int my = -1; my <= 1; my++)
                for (int mx = -1; mx <= 1; mx++)
                {
                    Accelerate(ref probe, fx, fz, rx, rz, new Vector2(mx, my), false, out float vx, out float vz);
                    float ex = vx - tx, ez = vz - tz, e = ex * ex + ez * ez;
                    if (e < best - 1e-6f)
                    {
                        best = e;
                        input.Move = new Vector2(mx, my);
                    }
                }
            if (DoubleDue(h, b))
                input.JumpPresses = 2;
            input.JumpHeld = true;
            return input;
        }

        /// <summary>
        /// Whether a jump now, flown with Fly, comes down on the top; how
        /// far from the target it lands and in how many ticks.
        /// </summary>
        public bool WouldLand(Hop h, Body b, float yaw, bool twice, out float miss, out int ticks)
        {
            h.Double = twice;
            miss = float.MaxValue;
            ticks = 0;
            if (!b.Grounded || b.Standing < 2 || SettledOnly && !b.Settled)
                return false;
            for (int k = 0; k < MaxFlight; k++)
            {
                MoveInput input = Fly(h, b, yaw);
                if (k == 0)
                    input.JumpPresses = 1;
                Step(ref b, input);
                ticks = k + 1;
                if (k > 0 && b.Grounded)
                    break;
            }
            float ox = b.X - h.From.X, oz = b.Z - h.From.Z;
            float s = ox * h.DX + oz * h.DZ, w = -ox * h.DZ + oz * h.DX;
            miss = UMath.Abs(s - h.Target);
            return b.Grounded && b.Y - h.From.Y >= h.Rise - 0.3f && s >= h.Lip + 0.05f && s <= h.End - 0.1f &&
                   UMath.Abs(w) < 0.6f;
        }

        /// <summary>Where a bot is with one hop: still on the ground, backing up for a run-up, or in the air.</summary>
        public struct Attempt
        {
            public bool Flying, Backing, Twice;
            public Vector2 Keys;         // the WASD held on the ground
        }

        /// <summary>One tick of a hop: in the air Fly; on the ground jump as soon as the
        /// model lands it (a single jump if it lands well, else a double), else walk on
        /// to the take-off, and back off for a run-up when standing at the lip does not
        /// do.</summary>
        public MoveInput Drive(in Hop hop, in Body b, float yaw, ref Attempt a)
        {
            Hop h = hop;
            if (a.Flying)
            {
                h.Double = a.Twice;
                return Fly(h, b, yaw);
            }
            if (b.Grounded)
            {
                bool single = WouldLand(hop, b, yaw, false, out float miss1, out _);
                bool twice = hop.Rise > 0.5f && (!single || miss1 > 0.25f) && WouldLand(hop, b, yaw, true, out _, out _);
                if (single || twice)
                {
                    a.Flying = true;
                    a.Twice = twice;
                    h.Double = twice;
                    MoveInput jump = Fly(h, b, yaw);
                    jump.JumpPresses = 1;
                    return jump;
                }
            }
            MoveInput input = Approach(hop, b, yaw, ref a, out float s);
            float stop = UMath.Min(hop.Lip - radius - 0.05f, hop.Edge - 0.1f);
            float speed = UMath.Sqrt(b.VX * b.VX + b.VZ * b.VZ);
            // Back for a run-up, as far as the own level lets it.
            float back = UMath.Max(stop - 1f, hop.Back);
            if (!a.Backing && s >= stop - 0.1f && speed < 0.5f && back < stop - 0.3f)
                a.Backing = true;
            else if (a.Backing && s <= back + 0.05f)
                a.Backing = false;
            return input;
        }

        /// <summary>The ground keys towards a hop's take-off: onto its line, then along it at walking
        /// speed up to the lip (backing to a metre of run-up first). Held keys stay while nearly as
        /// good, and a key never flips straight to its opposite unless the edge is near.</summary>
        public MoveInput Approach(in Hop h, in Body b, float yaw, ref Attempt a, out float s)
        {
            float ox = b.X - h.From.X, oz = b.Z - h.From.Z;
            s = ox * h.DX + oz * h.DZ;
            float w = -ox * h.DZ + oz * h.DX;
            float stop = UMath.Min(h.Lip - radius - 0.05f, h.Edge - 0.1f);
            // Just down and not yet seen standing: let go and catch the landing.
            if (!b.Settled && s < stop - 0.3f)
            {
                a.Keys = Vector2.Zero;
                return new MoveInput { Yaw = yaw };
            }
            float goal = a.Backing ? UMath.Max(stop - 1f, h.Back) : stop;
            float along = Want(goal - s, 0.25f, groundDec * 0.8f);
            along = UMath.Clamp(along, -walk, walk);
            float aside = UMath.Abs(w) < AsideSlack ? 0f : Want(-w, 0.2f, groundDec * 0.8f);
            float tx = h.DX * along - h.DZ * aside, tz = h.DZ * along + h.DX * aside;

            Vector2 move = Vector2.Zero;
            Body probe = b;
            float best = float.MaxValue, kept = float.MaxValue;
            Axes(yaw, out float fx, out float fz, out float rx, out float rz);
            for (int my = -1; my <= 1; my++)
                for (int mx = -1; mx <= 1; mx++)
                {
                    var keys = new Vector2(mx, my);
                    Accelerate(ref probe, fx, fz, rx, rz, keys, false, out float vx, out float vz);
                    float ex = vx - tx, ez = vz - tz, e = UMath.Sqrt(ex * ex + ez * ez);
                    if (keys == a.Keys)
                        kept = e;
                    if (e < best - 1e-6f)
                    {
                        best = e;
                        move = keys;
                    }
                }
            if (kept <= best + KeySlack)
                move = a.Keys;
            else if (s < h.Edge - 0.4f)
            {
                // Through nothing first, never straight from one key to its opposite.
                if (move.X == -a.Keys.X)
                    move.X = 0f;
                if (move.Y == -a.Keys.Y)
                    move.Y = 0f;
            }
            a.Keys = move;
            return new MoveInput { Yaw = yaw, Move = move };
        }

        // Metres off the line not worth a step aside, and m/s of speed a held
        // key may miss by before it changes.
        const float AsideSlack = 0.1f;
        const float KeySlack = 0.6f;

        /// <summary>WASD for a direction relative to the view (degrees, left positive),
        /// as a thumb holds them: one of eight, kept until the wanted direction is
        /// clearly another, nothing when not going.</summary>
        public static Vector2 Keys(float rel, bool go, Vector2 held)
        {
            if (!go)
                return Vector2.Zero;
            if (held != Vector2.Zero)
            {
                float heldRel = UMath.Atan2(-held.X, held.Y) * (180f / MathF.PI);
                if (UMath.Abs(MathF.IEEERemainder(rel - heldRel, 360f)) < 22.5f + KeyHysteresis)
                    return held;
            }
            int n = (int)MathF.Round(MathF.IEEERemainder(rel, 360f) / 45f);
            float r = n * 45f * (MathF.PI / 180f);
            float x = -UMath.Sin(r), y = UMath.Cos(r);
            return new Vector2(x > 0.5f ? 1f : x < -0.5f ? -1f : 0f, y > 0.5f ? 1f : y < -0.5f ? -1f : 0f);
        }

        const float KeyHysteresis = 12f;

    }
}
