using System;
using KotH.Shared.Combat;
using Stride.Core.Mathematics;
using Stride.Rendering;

namespace KotH.Client.Player
{
    /// <summary>Where the right hand goes in a swing, relative to the shoulder in the chest's frame
    /// (X right, Y up, Z back): down from High through the two bulges to Low, up the other way.
    /// CaneHigh/CaneLow, where given, aim the cane there: the forearm turns and the cane tips in
    /// the fist as near as they can.</summary>
    readonly struct SwingPath
    {
        public readonly Vector3 High, Low, BulgeHigh, BulgeLow, Pole, CaneHigh, CaneLow;

        public SwingPath(Vector3 high, Vector3 low, Vector3 bulgeHigh, Vector3 bulgeLow, Vector3 pole,
                         Vector3 caneHigh = default, Vector3 caneLow = default)
        {
            High = high;
            Low = low;
            BulgeHigh = bulgeHigh;
            BulgeLow = bulgeLow;
            Pole = pole;
            CaneHigh = caneHigh;
            CaneLow = caneLow;
        }

        // The figure: from above the right shoulder down to before the belly,
        // the cane from over the shoulder to the lower left. Its arm reaches
        // only 0.48 m from a shoulder deep in the wide body: the path keeps the
        // elbow bent, the forearm in front of the body and the elbow out.
        public static readonly SwingPath Figure = new SwingPath(
            new Vector3(0.32f, 0.02f, 0.24f), new Vector3(-0.13f, -0.26f, -0.29f),
            new Vector3(0.30f, 0.06f, -0.26f), new Vector3(0.02f, -0.16f, -0.38f),
            new Vector3(0.6f, -0.8f, 0.0f),
            new Vector3(0.2f, 0.8f, 0.55f), new Vector3(-0.6f, -0.5f, -0.6f));

        // The first-person arms: the shoulder sits 0.5 m under the eye and
        // 0.24 m right of it, the arm reaches 0.48 m. From the right of the
        // view, cane up, across to its lower middle.
        public static readonly SwingPath Arms = new SwingPath(
            new Vector3(-0.04f, 0.38f, -0.30f), new Vector3(-0.33f, 0.12f, -0.32f),
            new Vector3(-0.10f, 0.38f, -0.40f), new Vector3(-0.21f, 0.18f, -0.42f),
            new Vector3(0.8f, -0.5f, 0.3f));
    }

    /// <summary>The swing's timing and shape, from PlayerVisualSystem's settings.</summary>
    struct SwingSettings
    {
        public float Windup, Strike, Follow, Back;
        public float DownShare, Variation, TorsoDegrees, BendDegrees;
    }

    /// <summary>One right arm's swing with the cane on UpperArmSwing.R/LowerArmSwing.R: quick
    /// windup, a speeding stroke along a bowed path, follow-through, easy way back. Direction and
    /// variations come from NetId and tick (same on every client); a two-bone IK puts the hand on
    /// the path, minus the clip's own arm pose of the frame before.</summary>
    sealed class ArmSwing
    {
        const string UpperBone = "UpperArm.R", UpperSwingBone = "UpperArmSwing.R";
        const string LowerBone = "LowerArm.R", LowerSwingBone = "LowerArmSwing.R";
        const string GripBone = "Grip.R", RecoilBone = "Recoil";
        // Beyond this the forearm's skin wrings at the elbow, or the cane
        // leaves the fist.
        const float MaxTwist = 60f * MathF.PI / 180f, MaxTip = 40f * MathF.PI / 180f;

        int upper = -2, upperSwing, lower, lowerSwing, grip;
        float upperLength, lowerLength;
        Vector3 hingeLocal;

        int seen;
        bool watching;
        float age = float.MaxValue;
        bool down;
        Vector3 from, high, bulgeHigh, bulgeLow, low, pole, caneHigh, caneLow;
        float startWeight, pace = 1f, torso = 1f;
        float yawNow, bendNow, startYaw, startBend;
        Vector3 holdHand, holdAim, holdUp, holdGrip, barrel, gunUp;
        float holdWeight, holdFollow;
        // The clip's hand and its slow mean: what it swings walking and running.
        Vector3 clipHand, clipMean;
        bool hasClip;

        /// <summary>The hand's goal relative to the shoulder and how much it counts.</summary>
        public Vector3 Hand { get; private set; }
        public float Weight { get; private set; }

        /// <summary>Where the cane should point, zero for no wish.</summary>
        public Vector3 Cane { get; private set; }

        /// <summary>The cane's tip in the fist, in the grip bone's frame; identity at rest.</summary>
        public Quaternion CaneTip { get; private set; } = Quaternion.Identity;

        /// <summary>The gun's turn in the fist onto Hold's aim, in the grip bone's frame like CaneTip.</summary>
        public Quaternion GunTip { get; private set; } = Quaternion.Identity;

        /// <summary>The torso's share of the swing, already weighted; identity at rest.</summary>
        public Quaternion Torso { get; private set; } = Quaternion.Identity;

        /// <summary>Swings started so far, the cane's whoosh follows it.</summary>
        public int Started { get; private set; }

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
            clipHand = ClipHand(skeleton);
            clipMean = hasClip ? clipMean + (clipHand - clipMean) * (1f - MathF.Exp(-dt / 0.5f)) : clipHand;
            hasClip = true;
            if (melee != null && (!watching || melee.LastSwingTick != seen))
            {
                if (watching && melee.LastSwingTick > seen)
                    Start(skeleton, netId, melee.LastSwingTick, settings, path);
                seen = melee.LastSwingTick;
                watching = true;
            }
            Step(settings, dt);
            Pose(skeleton);
        }

        /// <summary>While no swing runs the hand goes to hand (shoulder frame), blended in by weight,
        /// moved by follow times the clip's own swing about its mean, so the gun bobs in step.
        /// With an aim GunTip turns the gun onto it in the fist: barrel and gunUp are the gun's in the
        /// grip bone's frame, up is where its top should face.</summary>
        public void Hold(Vector3 hand, float weight, Vector3 aim, Vector3 up, Vector3 barrel, Vector3 gunUp,
                         float follow = 0f)
        {
            holdHand = hand;
            holdFollow = follow;
            holdWeight = weight;
            holdAim = aim;
            this.barrel = barrel;
            this.gunUp = gunUp;
            holdUp = Vector3.Normalize(up - aim * Vector3.Dot(aim, up));
            // The grip bone's Y in the gun's frame, carried over onto the aim.
            holdGrip = aim == Vector3.Zero ? Vector3.Zero : aim * barrel.Y + holdUp * gunUp.Y;
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
            Started++;
            startWeight = Weight;
            startYaw = yawNow;
            startBend = bendNow;
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
            if (path.CaneHigh != Vector3.Zero)
            {
                Vector3 caneTop = Vector3.Normalize(path.CaneHigh) + Jitter(0.15f, 0.15f, 0.15f);
                Vector3 caneBottom = Vector3.Normalize(path.CaneLow) + Jitter(0.15f, 0.15f, 0.15f);
                (caneHigh, caneLow) = down ? (caneTop, caneBottom) : (caneBottom, caneTop);
            }
            else
                caneHigh = caneLow = Vector3.Zero;
            pace = 1f + (Next() * 2f - 1f) * 0.15f * v;
            torso = 1f + (Next() * 2f - 1f) * 0.25f * v;
            Hand = from;
        }

        void Step(in SwingSettings s, float dt)
        {
            if (age == float.MaxValue)
            {
                Weight = holdWeight;
                Hand = holdHand + (clipHand - clipMean) * holdFollow;
                Cane = holdGrip;
                Torso = Quaternion.Identity;
                return;
            }
            age += dt;
            float windup = MathF.Max(1e-3f, s.Windup * pace), strike = MathF.Max(1e-3f, s.Strike * pace);
            float follow = s.Follow * pace, back = MathF.Max(1e-3f, s.Back);
            float t = age;
            // Torso phase: -1 wound up, +1 struck through. A swing that comes
            // before the last one is back starts from its turn, not from zero.
            float phase, carried = 0f;
            if (t < windup)
            {
                float k = Smooth(t / windup);
                Hand = Vector3.Lerp(from, high, k);
                Cane = caneHigh;
                Weight = startWeight + (1f - startWeight) * k;
                phase = -k;
                carried = 1f - k;
            }
            else if ((t -= windup) < strike)
            {
                // Speeds up into the stroke and eases only at its very end.
                float s1 = t / strike;
                float u = s1 * s1 * (2.2f - 1.2f * s1);
                Hand = Bezier(high, bulgeHigh, bulgeLow, low, u);
                Cane = Vector3.Lerp(caneHigh, caneLow, u);
                // On an arc about the shoulder: the elbow never folds midway.
                if (Cane != Vector3.Zero && Hand.LengthSquared() > 1e-6f)
                    Hand = Vector3.Normalize(Hand) * MathUtil.Lerp(high.Length(), low.Length(), u);
                Weight = 1f;
                phase = -1f + 2f * u;
            }
            else if ((t -= strike) < follow)
            {
                Hand = low;
                Cane = caneLow;
                Weight = 1f;
                phase = 1f;
            }
            else if ((t -= follow) < back)
            {
                Hand = low;
                Cane = caneLow;
                Weight = 1f - Smooth(t / back);
                phase = 1f;
            }
            else
            {
                age = float.MaxValue;
                Weight = 0f;
                yawNow = bendNow = 0f;
                Torso = Quaternion.Identity;
                return;
            }
            // Down: wound up turned to the right, struck through to the left.
            float yaw = (down ? phase : -phase) * s.TorsoDegrees * torso * Weight;
            float bend = -MathF.Max(0f, phase) * s.BendDegrees * Weight;
            yawNow = yaw = startYaw * carried + yaw * (1f - carried);
            bendNow = bend = startBend * carried + bend * (1f - carried);
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

        /// <summary>Two-bone IK in the chest's frame, from the clip's own arm: bend the
        /// elbow until the hand is as far from the shoulder as the goal, turn the arm
        /// onto the goal, then about that line so the elbow points to the pole. Both
        /// swing bones take what is left after the clip, blended in by Weight.</summary>
        void Pose(SkeletonUpdater skeleton)
        {
            ModelNodeTransformation[] t = skeleton.NodeTransformations;
            CaneTip = Quaternion.Identity;
            GunTip = Quaternion.Identity;
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
            if (Cane.LengthSquared() > 1e-6f)
            {
                // Twist the forearm about itself (elbow to hand) until the
                // cane, held at a slant in the fist, is nearest its wish.
                Vector3 fore = Vector3.Normalize(Vector3.Transform(reached, turn) - e);
                Vector3 cane = Vector3.Transform(Vector3.Transform(Vector3.UnitY, t[grip].Transform.Rotation), lowerGoal);
                Vector3 c = cane - fore * Vector3.Dot(fore, cane), d = Cane - fore * Vector3.Dot(fore, Cane);
                float twist = Math.Clamp(MathF.Atan2(Vector3.Dot(fore, Vector3.Cross(c, d)), Vector3.Dot(c, d)),
                                         -MaxTwist, MaxTwist);
                lowerGoal *= Quaternion.RotationAxis(fore, twist);

                // What the twist leaves, the cane tips in the fist, about the grip.
                Quaternion held = t[grip].Transform.Rotation * lowerGoal;
                Vector3 now = Vector3.Normalize(Vector3.Transform(Vector3.UnitY, held)), wish = Vector3.Normalize(Cane);
                Vector3 axis = Vector3.Cross(now, wish);
                float sin = axis.Length();
                if (sin > 1e-5f)
                {
                    float tip = MathF.Min(MathF.Atan2(sin, Vector3.Dot(now, wish)), MaxTip) * Weight;
                    CaneTip = held * Quaternion.RotationAxis(axis / sin, tip) * Quaternion.Invert(held);
                }
            }
            if (age == float.MaxValue && holdAim != Vector3.Zero)
            {
                // What the forearm left, the gun turns in the fist: barrel onto the aim,
                // then about it until its top faces up.
                Quaternion held = t[grip].Transform.Rotation * lowerGoal;
                Quaternion onAim = Between(Vector3.Normalize(Vector3.Transform(barrel, held)), holdAim);
                Vector3 top = Vector3.Transform(Vector3.Transform(gunUp, held), onAim);
                top -= holdAim * Vector3.Dot(holdAim, top);
                float spin = MathF.Atan2(Vector3.Dot(holdAim, Vector3.Cross(top, holdUp)), Vector3.Dot(top, holdUp));
                Quaternion full = onAim * Quaternion.RotationAxis(holdAim, spin);
                GunTip = held * Quaternion.Slerp(Quaternion.Identity, full, Weight) * Quaternion.Invert(held);
            }
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
