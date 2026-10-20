using KotH.Shared.Mathematics;
using KotH.Shared.Physics;
using Stride.BepuPhysics;
using Stride.Core.Mathematics;

namespace KotH.Shared.Player
{
    /// <summary>
    /// Moves an upright capsule through the world and slides it along what it
    /// hits. Replaces a character controller whose state lives inside the
    /// physics engine where it can neither be read nor restored. Every query
    /// used here is a pure function of the world, so all of the state stays
    /// in the caller's fields.
    /// </summary>
    public sealed class CapsuleSweep
    {
        // Four passes resolve a corner; more only matters in geometry we do
        // not build. Depenetration gets its own, smaller budget.
        const int MovePasses = 4;
        const int PushPasses = 3;

        // Below this a step is noise: chasing it costs a pass and moves the
        // body by less than the skin width it keeps anyway.
        const float MinStep = 1e-5f;

        readonly QueryHit[] hits = new QueryHit[8];
        readonly CollidableComponent[] overlaps = new CollidableComponent[8];

        /// <summary>What a sweep ran into, for the caller to act on.</summary>
        public struct Result
        {
            public Vector3 position;
            public Vector3 groundNormal;  // most upright contact, up only
            public bool hasGround;
            public bool blocked;          // a contact refused part of the move
            public Vector3 lastNormal;    // most recent contact, any direction
            public bool hasContact;
            public Vector3 velocity;      // what the contacts left of it
        }

        /// <summary>
        /// Advances from position by delta, sliding along contacts. The
        /// capsule is given by its foot point, height and radius; skin is the
        /// gap it keeps from everything, so it never rests exactly on a face.
        /// hullOffsetY is where the hit hull's own centre sits, see Depenetrate.
        /// </summary>
        public Result Move(WorldQueries world, float hullOffsetY, Vector3 position,
                           float footOffset, float height, float radius, float skin,
                           float walkableY, float stepHeight, Vector3 delta,
                           Vector3 velocity, CollisionMask mask)
        {
            Result r = default;
            r.position = position;
            r.groundNormal = UMath.Up;
            r.velocity = velocity;

            // Out of anything it already overlaps first: a cast that starts
            // inside a collider reports distance zero and a normal that
            // points nowhere useful.
            r.position = Depenetrate(world, hullOffsetY, r.position, footOffset,
                                     height, radius, skin, mask);

            Vector3 remaining = delta;

            for (int pass = 0; pass < MovePasses; pass++)
            {
                Vector3 before = remaining;
                float distance = UMath.Magnitude(remaining);
                if (distance < MinStep)
                    break;

                Vector3 direction = UMath.Div(remaining, distance);

                // Started a skin width back along the direction: a face
                // the capsule already touches then sits at distance skin
                // instead of zero, where the cast reports an initial overlap
                // and hands back a normal that points nowhere.
                if (!Cast(world, UMath.Sub(r.position, UMath.Mul(direction, skin)),
                          footOffset, height, radius, direction,
                          distance + skin * 2f, mask, out QueryHit hit))
                {
                    r.position = UMath.Add(r.position, remaining);
                    break;
                }

                // Two skins: one for the shifted origin, one to keep.
                float advance = UMath.Max(0f, hit.Distance - skin * 2f);
                r.position = UMath.Add(r.position, UMath.Mul(direction, advance));

                r.hasContact = true;
                r.lastNormal = hit.Normal;
                r.blocked = true;

                if (hit.Normal.Y > 0f
                    && (!r.hasGround || hit.Normal.Y > r.groundNormal.Y))
                {
                    r.groundNormal = hit.Normal;
                    r.hasGround = true;
                }

                // What is left of the move continues along the surface.
                remaining = UMath.Sub(remaining, UMath.Mul(direction, advance));

                if (hit.Normal.Y >= walkableY)
                {
                    // Walkable ground carries the body, it does not redirect
                    // a fall into a slide. Only what was asked for
                    // horizontally goes on, laid onto the surface - without
                    // this, standing on a ramp slid downhill by itself.
                    Vector3 flat = new Vector3(remaining.X, 0f, remaining.Z);
                    remaining = UMath.ProjectOnPlane(flat, hit.Normal);
                }
                else
                {
                    remaining = UMath.ProjectOnPlane(remaining, hit.Normal);

                    // A wall takes the speed it refuses with it. Walkable
                    // ground does not: the motor owns the vertical there,
                    // and laying speed onto a ramp would shave cos squared
                    // off every uphill tick.
                    if (!IsRiser(world, r.position, footOffset, radius, skin,
                                 stepHeight, hit.Normal, mask))
                        r.velocity = UMath.ProjectOnPlane(r.velocity, hit.Normal);
                }

                // A pass that neither moved the body nor turned what is
                // left would do exactly the same on the next try.
                if (advance <= 0f
                    && UMath.SqrMagnitude(UMath.Sub(remaining, before)) < MinStep * MinStep)
                    break;
            }

            return r;
        }

        /// <summary>
        /// Pushes the capsule out of everything it overlaps. Corrections are
        /// collected in a stable collider order before they are applied:
        /// overlaps come back in no defined order, and float addition is not
        /// associative, so the same set in another order gives other bits.
        /// </summary>
        Vector3 Depenetrate(WorldQueries world, float hullOffsetY, Vector3 position,
                            float footOffset, float height, float radius, float skin,
                            CollisionMask mask)
        {
            for (int pass = 0; pass < PushPasses; pass++)
            {
                Bottom(position, footOffset, height, radius,
                       out Vector3 bottom, out Vector3 top);

                // Real overlaps only. Merely touching a face is handled
                // by the shifted cast origin in Move, not here.
                int count = world.OverlapCapsule(bottom, top, radius, mask, overlaps);
                if (count == 0)
                    return position;

                // Unity's ComputePenetration put the hull's own centre on top
                // of the pose it was handed, so a crouched hull was measured
                // that far too low. Kept on purpose, the port changes nothing.
                Vector3 centre = UMath.Add(Centre(position, footOffset, height),
                                           UMath.Mul(UMath.Up, hullOffsetY));
                Vector3 penBottom = UMath.Sub(centre, UMath.Mul(UMath.Up, height * 0.5f - radius));
                Vector3 penTop = UMath.Add(centre, UMath.Mul(UMath.Up, height * 0.5f - radius));

                Vector3 push = Vector3.Zero;
                bool any = false;

                for (int i = 0; i < count; i++)
                {
                    if (!world.ComputePenetration(penBottom, penTop, radius, overlaps[i],
                            out Vector3 direction, out float depth))
                        continue;

                    push = UMath.Add(push, UMath.Mul(direction, depth + skin));
                    any = true;
                }

                // Nothing resolved is the normal case: an overlap test also
                // reports a face the capsule merely touches, where the
                // penetration test rightly finds no depth at all.
                if (!any)
                    return position;

                // Never pushed down into something: under a ramp's edge the
                // ramp pushed a crouched body through the one-sided ground (a
                // deliberate change from Unity). Blocked, it slides along the
                // ground instead. Solid boxes resolve on the next pass, so
                // other pushes stay unchecked, exactly as before.
                if (push.Y < 0f)
                    push = Shift(world, position, footOffset, height, radius, skin, push, mask, 2);
                position = UMath.Add(position, push);
            }

            return position;
        }

        /// <summary>
        /// True when the face ends below stepHeight, so the motor climbs it
        /// instead of stopping. Its step-up aims along velocity: trimmed at
        /// a riser hit at an angle, it then looked along the step and not
        /// onto it, and stairs taken diagonally climbed at half the height.
        /// </summary>
        static bool IsRiser(WorldQueries world, Vector3 position, float footOffset,
                            float radius, float skin, float stepHeight,
                            Vector3 normal, CollisionMask mask)
        {
            Vector3 into = new Vector3(-normal.X, 0f, -normal.Z);
            if (stepHeight <= 0f || UMath.SqrMagnitude(into) < 1e-4f)
                return false;

            Vector3 above = UMath.Add(position,
                UMath.Mul(UMath.Up, footOffset + stepHeight));

            return !world.Raycast(above, UMath.Normalized(into),
                radius + skin * 2f, mask, out _);
        }

        /// <summary>
        /// How much of delta the capsule can move straight without entering
        /// anything, keeping the skin: no sliding, no contacts reported. For
        /// corrections that must not push a body into the world. Returns
        /// delta itself, bit for bit, when nothing is in the way.
        /// </summary>
        public Vector3 Clear(WorldQueries world, Vector3 position, float footOffset,
                             float height, float radius, float skin, Vector3 delta,
                             CollisionMask mask)
            => Shift(world, position, footOffset, height, radius, skin, delta, mask, 1);

        /// <summary>
        /// Moves along delta like Clear, but what a face refuses slides along
        /// it for up to passes - 1 more casts. Colliders the capsule already
        /// overlaps do not stop it, Cast skips them. Unblocked, delta comes
        /// back bit for bit.
        /// </summary>
        Vector3 Shift(WorldQueries world, Vector3 position, float footOffset,
                      float height, float radius, float skin, Vector3 delta,
                      CollisionMask mask, int passes)
        {
            Vector3 moved = Vector3.Zero;
            Vector3 remaining = delta;

            for (int pass = 0; pass < passes; pass++)
            {
                float distance = UMath.Magnitude(remaining);
                if (distance < MinStep)
                    return pass == 0 ? delta : moved;

                // Started a skin back and stopped two skins short, as in Move.
                Vector3 direction = UMath.Div(remaining, distance);
                Vector3 from = UMath.Add(position, moved);
                if (!Cast(world, UMath.Sub(from, UMath.Mul(direction, skin)),
                          footOffset, height, radius, direction,
                          distance + skin * 2f, mask, out QueryHit hit))
                    return pass == 0 ? delta : UMath.Add(moved, remaining);

                Vector3 step = UMath.Mul(direction, UMath.Max(0f, hit.Distance - skin * 2f));
                moved = pass == 0 ? step : UMath.Add(moved, step);
                remaining = UMath.ProjectOnPlane(UMath.Sub(remaining, step), hit.Normal);
            }

            return moved;
        }

        bool Cast(WorldQueries world, Vector3 position, float footOffset, float height,
                  float radius, Vector3 direction, float distance,
                  CollisionMask mask, out QueryHit hit)
        {
            Bottom(position, footOffset, height, radius,
                   out Vector3 bottom, out Vector3 top);

            int count = world.CapsuleCastAll(bottom, top, radius, direction,
                                             distance, mask, hits);

            hit = default;
            float nearest = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                // Zero distance now means a real overlap, deeper than the
                // shifted origin covers, and its normal points nowhere.
                // Depenetrate clears those; a hit kept here would block the
                // body against the very floor it stands on.
                if (hits[i].Distance <= 0f
                    || UMath.SqrMagnitude(hits[i].Normal) < 0.5f)
                    continue;

                // A face only stops what moves into it. The floor under a
                // walking body is grazed, not run into, and counting it
                // left the body standing still on open ground: the graze
                // ate the whole move and the slide had nothing to turn.
                if (UMath.Dot(hits[i].Normal, direction) > -1e-4f)
                    continue;

                if (hits[i].Distance < nearest)
                {
                    nearest = hits[i].Distance;
                    hit = hits[i];
                }
            }

            return nearest < float.MaxValue;
        }

        /// <summary>Centres of the two end spheres of the capsule.</summary>
        static void Bottom(Vector3 position, float footOffset, float height,
                           float radius, out Vector3 bottom, out Vector3 top)
        {
            Vector3 feet = UMath.Add(position, UMath.Mul(UMath.Up, footOffset));
            bottom = UMath.Add(feet, UMath.Mul(UMath.Up, radius));
            top = UMath.Add(feet, UMath.Mul(UMath.Up, height - radius));
        }

        static Vector3 Centre(Vector3 position, float footOffset, float height)
            => UMath.Add(position, UMath.Mul(UMath.Up, footOffset + height * 0.5f));
    }
}
