using System;
using KotH.Shared.Combat;
using Stride.Core.Mathematics;
using Stride.Rendering;

namespace KotH.Client.Player
{
    /// <summary>
    /// Where the right hand goes in a swing, relative to the shoulder in the
    /// chest's frame (X right, Y up, Z back). The downward swing runs from
    /// High through the two bulges to Low; the upward one runs it back.
    /// </summary>
    readonly struct SwingPath
    {
        public readonly Vector3 High, Low, BulgeHigh, BulgeLow, Pole;

        public SwingPath(Vector3 high, Vector3 low, Vector3 bulgeHigh, Vector3 bulgeLow, Vector3 pole)
        {
            High = high;
            Low = low;
            BulgeHigh = bulgeHigh;
            BulgeLow = bulgeLow;
            Pole = pole;
        }

        // The figure: from above the right shoulder to before the left hip,
        // bowed out in front of the belly so the cane never cuts the body.
        public static readonly SwingPath Figure = new SwingPath(
            new Vector3(0.20f, 0.16f, 0.02f), new Vector3(-0.30f, -0.32f, -0.28f),
            new Vector3(0.18f, 0.18f, -0.23f), new Vector3(-0.12f, -0.20f, -0.40f),
            new Vector3(0.8f, -0.3f, 0.5f));

        // The first-person arms hang ahead of the eye: from the upper right
        // of the view across to its lower left.
        public static readonly SwingPath Arms = new SwingPath(
            new Vector3(0.16f, 0.42f, -0.22f), new Vector3(-0.38f, 0.02f, -0.30f),
            new Vector3(0.11f, 0.42f, -0.40f), new Vector3(-0.23f, 0.12f, -0.42f),
            new Vector3(0.8f, -0.5f, 0.3f));
    }

    /// <summary>The swing's timing and shape, from PlayerVisualSystem's settings.</summary>
    struct SwingSettings
    {
        public float Windup, Strike, Follow, Back;
        public float DownShare, Variation, TorsoDegrees, BendDegrees;
    }

    /// <summary>
    /// The swing of one right arm with the cane, on the game bones
    /// UpperArmSwing.R and LowerArmSwing.R (.work/rig/rig.py): a quick
    /// windup, a stroke along a bowed path that speeds up, a little follow
    /// through and an easy way back. Each swing draws its direction (mostly
    /// from the upper right down to the lower left) and small variations
    /// from the body's NetId and the swing's tick, so every client shows the
    /// same one. A two-bone IK puts the hand on the path; the clip's own arm
    /// pose of the frame before is taken out, so walking never doubles it.
    /// </summary>
    sealed class ArmSwing
    {
        const string UpperBone = "UpperArm.R", UpperSwingBone = "UpperArmSwing.R";
        const string LowerBone = "LowerArm.R", LowerSwingBone = "LowerArmSwing.R";
        const string GripBone = "Grip.R", RecoilBone = "Recoil";

        int upper = -2, upperSwing, lower, lowerSwing, grip;
        float upperLength, lowerLength;
        Vector3 hingeLocal;

        int seen = int.MinValue;
        float age = float.MaxValue;
        bool down;
        Vector3 from, high, bulgeHigh, bulgeLow, low, pole;
        float startWeight, pace = 1f, torso = 1f;

        /// <summary>The hand's goal relative to the shoulder and how much it counts.</summary>
        public Vector3 Hand { get; private set; }
        public float Weight { get; private set; }

        /// <summary>The torso's share of the swing, already weighted; identity at rest.</summary>
        public Quaternion Torso { get; private set; } = Quaternion.Identity;

        /// <summary>The recoil bone of this skeleton, -1 without.</summary>
        public int RecoilNode { get; private set; } = -1;

        /// <summary>
        /// Follows melee's swings and poses the arm. A swing already there
        /// when the figure appears is not shown, like a hit.
        /// </summary>
        public void Update(SkeletonUpdater skeleton, MeleeAttack melee, int netId, in SwingSettings settings,
                           in SwingPath path, float dt)
        {
            if (skeleton == null || !Bind(skeleton))
                return;
            if (melee != null && melee.LastSwingTick != seen)
            {
                if (seen != int.MinValue && melee.LastSwingTick > seen)
                    Start(skeleton, netId, melee.LastSwingTick, settings, path);
                seen = melee.LastSwingTick;
            }
            Step(settings, dt);
            Pose(skeleton);
        }

        /// <summary>Starts a swing now whatever melee says, for KOTH_SHOT_STRIKE.</summary>
        public void Force(SkeletonUpdater skeleton, int netId, int tick, in SwingSettings settings, in SwingPath path)
        {
            if (skeleton != null && Bind(skeleton))
                Start(skeleton, netId, tick, settings, path);
        }

        bool Bind(SkeletonUpdater skeleton)
        {
            if (upper != -2)
                return upper >= 0;
            ModelNodeDefinition[] nodes = skeleton.Nodes;
            int Find(string name) => Array.FindIndex(nodes, n => n.Name == name);
            upper = Find(UpperBone);
            upperSwing = Find(UpperSwingBone);
            lower = Find(LowerBone);
            lowerSwing = Find(LowerSwingBone);
            grip = Find(GripBone);
            RecoilNode = Find(RecoilBone);
            if (upperSwing < 0 || lower < 0 || lowerSwing < 0 || grip < 0)
                upper = -1;
            if (upper < 0)
                return false;
            upperLength = nodes[lower].Transform.Position.Length();
            lowerLength = nodes[grip].Transform.Position.Length();
            // The elbow bends about the figure's right axis (the clips do);
            // in the forearm's own frame at rest that is this axis.
            Quaternion restLower = nodes[lower].Transform.Rotation * nodes[upper].Transform.Rotation;
            hingeLocal = Vector3.Transform(Vector3.UnitX, Quaternion.Invert(restLower));
            return true;
        }

        void Start(SkeletonUpdater skeleton, int netId, int tick, in SwingSettings s, in SwingPath path)
        {
            // From wherever the hand is shown now: mid-swing or the clip's own.
            from = Weight > 1e-3f ? Hand : ClipHand(skeleton);
            startWeight = Weight;
            age = 0f;

            uint seed = unchecked((uint)netId * 0x9E3779B1u ^ (uint)tick * 0x85EBCA77u) | 1u;
            float Next()
            {
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                return (seed >> 8) / 16777216f;
            }
            float v = s.Variation;
            Vector3 Jitter(float x, float y, float z) =>
                new Vector3((Next() * 2f - 1f) * x, (Next() * 2f - 1f) * y, (Next() * 2f - 1f) * z) * v;
            down = Next() < s.DownShare;
            Vector3 top = path.High + Jitter(0.05f, 0.05f, 0.04f);
            Vector3 bottom = path.Low + Jitter(0.06f, 0.06f, 0.05f);
            Vector3 bowTop = path.BulgeHigh + Jitter(0.05f, 0.05f, 0.05f);
            Vector3 bowBottom = path.BulgeLow + Jitter(0.05f, 0.05f, 0.05f);
            // The upward swing runs the same bow backwards.
            (high, bulgeHigh, bulgeLow, low) = down ? (top, bowTop, bowBottom, bottom) : (bottom, bowBottom, bowTop, top);
            pole = path.Pole;
            pace = 1f + (Next() * 2f - 1f) * 0.15f * v;
            torso = 1f + (Next() * 2f - 1f) * 0.25f * v;
            Hand = from;
        }

        void Step(in SwingSettings s, float dt)
        {
            if (age == float.MaxValue)
            {
                Weight = 0f;
                Torso = Quaternion.Identity;
                return;
            }
            age += dt;
            float windup = MathF.Max(1e-3f, s.Windup * pace), strike = MathF.Max(1e-3f, s.Strike * pace);
            float follow = s.Follow * pace, back = MathF.Max(1e-3f, s.Back);
            float t = age;
            // Torso phase: -1 wound up, +1 struck through.
            float phase;
            if (t < windup)
            {
                float k = Smooth(t / windup);
                Hand = Vector3.Lerp(from, high, k);
                Weight = startWeight + (1f - startWeight) * k;
                phase = -k;
            }
            else if ((t -= windup) < strike)
            {
                // Speeds up into the stroke and eases only at its very end.
                float s1 = t / strike;
                float u = s1 * s1 * (2.2f - 1.2f * s1);
                Hand = Bezier(high, bulgeHigh, bulgeLow, low, u);
                Weight = 1f;
                phase = -1f + 2f * u;
            }
            else if ((t -= strike) < follow)
            {
                Hand = low;
                Weight = 1f;
                phase = 1f;
            }
            else if ((t -= follow) < back)
            {
                Hand = low;
                Weight = 1f - Smooth(t / back);
                phase = 1f;
            }
            else
            {
                age = float.MaxValue;
                Weight = 0f;
                Torso = Quaternion.Identity;
                return;
            }
            // Down: wound up turned to the right, struck through to the left.
            float yaw = (down ? phase : -phase) * s.TorsoDegrees * torso * Weight;
            float bend = -MathF.Max(0f, phase) * s.BendDegrees * Weight;
            Torso = Quaternion.RotationAxis(Vector3.UnitY, MathUtil.DegreesToRadians(yaw))
                  * Quaternion.RotationAxis(Vector3.UnitX, MathUtil.DegreesToRadians(bend));
        }

        /// <summary>The hand relative to the shoulder as the clip shows it (the frame before).</summary>
        Vector3 ClipHand(SkeletonUpdater skeleton)
        {
            ModelNodeTransformation[] t = skeleton.NodeTransformations;
            Quaternion a = t[upper].Transform.Rotation, b = t[lower].Transform.Rotation;
            Vector3 elbow = Vector3.Transform(t[lower].Transform.Position, a);
            return elbow + Vector3.Transform(t[grip].Transform.Position, b * a);
        }

        /// <summary>
        /// Two-bone IK in the chest's frame, from the clip's own arm: bend
        /// the elbow until the hand is as far from the shoulder as the goal,
        /// turn the arm onto the goal, then about that line so the elbow
        /// points to the pole. Both swing bones take what is left after the
        /// clip, blended in by Weight.
        /// </summary>
        void Pose(SkeletonUpdater skeleton)
        {
            ModelNodeTransformation[] t = skeleton.NodeTransformations;
            if (Weight < 1e-3f)
            {
                t[upperSwing].Transform.Rotation = Quaternion.Identity;
                t[lowerSwing].Transform.Rotation = Quaternion.Identity;
                return;
            }
            Quaternion a = t[upper].Transform.Rotation, b = t[lower].Transform.Rotation;
            Vector3 elbow = Vector3.Transform(t[lower].Transform.Position, a);
            Vector3 forearm = t[grip].Transform.Position;
            Quaternion lowerRot = b * a;
            Vector3 hinge = Vector3.Transform(hingeLocal, lowerRot);
            Vector3 HandAt(float bendBy) =>
                elbow + Vector3.Transform(forearm, lowerRot * Quaternion.RotationAxis(hinge, bendBy));

            float reachMin = MathF.Abs(upperLength - lowerLength) + 0.02f, reachMax = upperLength + lowerLength - 0.005f;
            float want = Math.Clamp(Hand.Length(), reachMin, reachMax);
            // Straightest first, then bend on: the hand only comes closer.
            float lo = -0.6f, best = 0f;
            for (float x = -0.6f; x <= 0.4f; x += 0.05f)
                if (HandAt(x).Length() > best)
                {
                    best = HandAt(x).Length();
                    lo = x;
                }
            float hi = 2.8f;
            for (int i = 0; i < 24; i++)
            {
                float mid = 0.5f * (lo + hi);
                if (HandAt(mid).Length() > want)
                    lo = mid;
                else
                    hi = mid;
            }
            float flex = 0.5f * (lo + hi);

            Vector3 reached = HandAt(flex);
            Vector3 goal = Hand.LengthSquared() > 1e-8f ? Vector3.Normalize(Hand) : Vector3.Normalize(reached);
            Quaternion onto = Between(Vector3.Normalize(reached), goal);
            Vector3 e = Vector3.Transform(elbow, onto);
            Vector3 ep = e - goal * Vector3.Dot(goal, e), pp = pole - goal * Vector3.Dot(goal, pole);
            float roll = MathF.Atan2(Vector3.Dot(goal, Vector3.Cross(ep, pp)), Vector3.Dot(ep, pp));
            Quaternion turn = onto * Quaternion.RotationAxis(goal, roll);

            Quaternion upperGoal = a * turn;
            Quaternion lowerGoal = lowerRot * Quaternion.RotationAxis(hinge, flex) * turn;
            Quaternion q1 = upperGoal * Quaternion.Invert(a);
            Quaternion q2 = lowerGoal * Quaternion.Invert(b * upperGoal);
            t[upperSwing].Transform.Rotation = Quaternion.Slerp(Quaternion.Identity, q1, Weight);
            t[lowerSwing].Transform.Rotation = Quaternion.Slerp(Quaternion.Identity, q2, Weight);
        }

        /// <summary>The shortest turn from one unit direction to another.</summary>
        static Quaternion Between(Vector3 from, Vector3 to)
        {
            Vector3 axis = Vector3.Cross(from, to);
            float sin = axis.Length(), cos = Vector3.Dot(from, to);
            if (sin < 1e-6f)
                return Quaternion.Identity;
            return Quaternion.RotationAxis(axis / sin, MathF.Atan2(sin, cos));
        }

        static Vector3 Bezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u)
        {
            float v = 1f - u;
            return p0 * (v * v * v) + p1 * (3f * v * v * u) + p2 * (3f * v * u * u) + p3 * (u * u * u);
        }

        static float Smooth(float x) => x * x * (3f - 2f * x);
    }
}
