using UnityEngine;

namespace KotH.Shared.Player
{
    /// <summary>
    /// Moves an upright capsule through the world and slides it along what it
    /// hits. Replaces CharacterController.Move, whose state lives inside PhysX
    /// where it can neither be read nor restored - which is what makes a
    /// rollback impossible. Every query used here is a pure function of the
    /// scene, so all of the state stays in the caller's fields.
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

        readonly RaycastHit[] hits = new RaycastHit[8];
        readonly Collider[] overlaps = new Collider[8];

        /// <summary>What a sweep ran into, for the caller to act on.</summary>
        public struct Result
        {
            public Vector3 position;
            public Vector3 groundNormal;  // most upright contact, up only
            public bool hasGround;
            public bool blocked;          // a contact refused part of the move
            public Vector3 lastNormal;    // most recent contact, any direction
            public bool hasContact;
        }

        /// <summary>
        /// Advances from position by delta, sliding along contacts. The
        /// capsule is given by its foot point, height and radius; skin is the
        /// gap it keeps from everything, so it never rests exactly on a face.
        /// </summary>
        public Result Move(Collider self, Vector3 position, float footOffset,
                           float height, float radius, float skin,
                           float walkableY, Vector3 delta, LayerMask mask)
        {
            Result r = default;
            r.position = position;
            r.groundNormal = Vector3.up;

            // Out of anything it already overlaps first: a cast that starts
            // inside a collider reports distance zero and a normal that
            // points nowhere useful.
            r.position = Depenetrate(self, r.position, footOffset, height,
                                     radius, skin, mask);

            Vector3 remaining = delta;

            for (int pass = 0; pass < MovePasses; pass++)
            {
                Vector3 before = remaining;
                float distance = remaining.magnitude;
                if (distance < MinStep)
                    break;

                Vector3 direction = remaining / distance;

                // Started a skin width back along the direction: a face
                // the capsule already touches then sits at distance skin
                // instead of zero, where PhysX reports an initial overlap
                // and hands back a normal that points nowhere.
                if (!Cast(r.position - direction * skin, footOffset, height,
                          radius, direction, distance + skin * 2f, mask,
                          out RaycastHit hit))
                {
                    r.position += remaining;
                    break;
                }

                // Two skins: one for the shifted origin, one to keep.
                float advance = Mathf.Max(0f, hit.distance - skin * 2f);
                r.position += direction * advance;

                r.hasContact = true;
                r.lastNormal = hit.normal;
                r.blocked = true;

                if (hit.normal.y > 0f
                    && (!r.hasGround || hit.normal.y > r.groundNormal.y))
                {
                    r.groundNormal = hit.normal;
                    r.hasGround = true;
                }

                // What is left of the move continues along the surface.
                remaining -= direction * advance;

                if (hit.normal.y >= walkableY)
                {
                    // Walkable ground carries the body, it does not redirect
                    // a fall into a slide. Only what was asked for
                    // horizontally goes on, laid onto the surface - without
                    // this, standing on a ramp slid downhill by itself.
                    Vector3 flat = new Vector3(remaining.x, 0f, remaining.z);
                    remaining = Vector3.ProjectOnPlane(flat, hit.normal);
                }
                else
                {
                    remaining = Vector3.ProjectOnPlane(remaining, hit.normal);
                }

                // A pass that neither moved the body nor turned what is
                // left would do exactly the same on the next try.
                if (advance <= 0f
                    && (remaining - before).sqrMagnitude < MinStep * MinStep)
                    break;
            }

            return r;
        }

        /// <summary>
        /// Pushes the capsule out of everything it overlaps. Corrections are
        /// collected and sorted before they are applied: OverlapCapsule
        /// returns its hits in no defined order, and float addition is not
        /// associative, so the same set in another order gives other bits.
        /// </summary>
        Vector3 Depenetrate(Collider self, Vector3 position, float footOffset,
                            float height, float radius, float skin,
                            LayerMask mask)
        {
            for (int pass = 0; pass < PushPasses; pass++)
            {
                Bottom(position, footOffset, height, radius,
                       out Vector3 bottom, out Vector3 top);

                // Real overlaps only. Merely touching a face is handled
                // by the shifted cast origin in Move, not here.
                int count = Physics.OverlapCapsuleNonAlloc(
                    bottom, top, radius, overlaps, mask,
                    QueryTriggerInteraction.Ignore);

                if (count == 0)
                    return position;

                // Sorted by instance id: stable within a process, which is
                // what a rollback needs. Across processes it is not, and
                // that would need our own baked world geometry.
                SortById(count);

                Vector3 push = Vector3.zero;
                bool any = false;

                for (int i = 0; i < count; i++)
                {
                    Transform t = overlaps[i].transform;

                    // The body's own hit hull stands in for the shape: it
                    // has the right size already and ApplyHeight keeps it
                    // that way. A collider on an inactive object has no
                    // PhysX shape and makes this return false every time.
                    if (!Physics.ComputePenetration(
                            self, Centre(position, footOffset, height),
                            Quaternion.identity,
                            overlaps[i], t.position, t.rotation,
                            out Vector3 direction, out float depth))
                        continue;

                    push += direction * (depth + skin);
                    any = true;
                }

                // Nothing resolved is the normal case: OverlapCapsule
                // also reports a face the capsule merely touches, where
                // ComputePenetration rightly finds no penetration at all.
                if (!any)
                    return position;

                position += push;
            }

            return position;
        }

        /// <summary>
        /// Insertion sort over the overlap buffer. At most eight entries, so
        /// the simplest stable order beats anything cleverer.
        /// </summary>
        void SortById(int count)
        {
            for (int i = 1; i < count; i++)
            {
                Collider c = overlaps[i];
                int id = c.GetInstanceID();
                int j = i - 1;

                while (j >= 0 && overlaps[j].GetInstanceID() > id)
                {
                    overlaps[j + 1] = overlaps[j];
                    j--;
                }

                overlaps[j + 1] = c;
            }
        }

        bool Cast(Vector3 position, float footOffset, float height,
                  float radius, Vector3 direction, float distance,
                  LayerMask mask, out RaycastHit hit)
        {
            Bottom(position, footOffset, height, radius,
                   out Vector3 bottom, out Vector3 top);

            int count = Physics.CapsuleCastNonAlloc(
                bottom, top, radius, direction, hits, distance, mask,
                QueryTriggerInteraction.Ignore);

            hit = default;
            float nearest = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                // Zero distance now means a real overlap, deeper than the
                // shifted origin covers, and its normal points nowhere.
                // Depenetrate clears those; a hit kept here would block the
                // body against the very floor it stands on.
                if (hits[i].distance <= 0f
                    || hits[i].normal.sqrMagnitude < 0.5f)
                    continue;

                // A face only stops what moves into it. The floor under a
                // walking body is grazed, not run into, and counting it
                // left the body standing still on open ground: the graze
                // ate the whole move and the slide had nothing to turn.
                if (Vector3.Dot(hits[i].normal, direction) > -1e-4f)
                    continue;

                if (hits[i].distance < nearest)
                {
                    nearest = hits[i].distance;
                    hit = hits[i];
                }
            }

            return nearest < float.MaxValue;
        }

        /// <summary>Centres of the two end spheres of the capsule.</summary>
        static void Bottom(Vector3 position, float footOffset, float height,
                           float radius, out Vector3 bottom, out Vector3 top)
        {
            Vector3 feet = position + Vector3.up * footOffset;
            bottom = feet + Vector3.up * radius;
            top = feet + Vector3.up * (height - radius);
        }

        static Vector3 Centre(Vector3 position, float footOffset, float height)
            => position + Vector3.up * (footOffset + height * 0.5f);

    }
}
