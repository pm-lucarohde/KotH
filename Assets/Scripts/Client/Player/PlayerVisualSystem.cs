using System.Collections.Generic;
using KotH.Shared.Player;
using Unity.Burst;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Jobs;

namespace KotH.Client.Player
{
    /// <summary>
    /// Interpolates every player body in one place: one LateUpdate for the
    /// whole game instead of one per player. The transform writes happen in
    /// a Burst job over a TransformAccessArray, so they spread across worker
    /// threads instead of queueing up on the main thread.
    /// </summary>
    public sealed class PlayerVisualSystem : MonoBehaviour
    {
        // Static, so a body can register before this object's Awake runs and
        // long after scene load once netcode spawns players.
        static readonly List<PlayerVisual> visuals = new List<PlayerVisual>();
        static bool listChanged;
        static PlayerVisualSystem active;

        TransformAccessArray transforms;
        NativeArray<Sample> samples;

        public static void Register(PlayerVisual visual)
        {
            if (visuals.Contains(visual))
                return;

            visuals.Add(visual);
            listChanged = true;
        }

        public static void Unregister(PlayerVisual visual)
        {
            if (visuals.Remove(visual))
                listChanged = true;
        }

        void Awake()
        {
            // Explicit == null, Unity's fake null breaks the other checks.
            if (active != null && active != this)
            {
                Debug.LogWarning(
                    "Second PlayerVisualSystem in the scene, disabling it.", this);
                enabled = false;
                return;
            }

            active = this;

            // Picks up everyone who registered before this object existed.
            listChanged = true;
        }

        void OnDestroy()
        {
            if (active == this)
                active = null;

            Release();
        }

        void LateUpdate()
        {
            if (listChanged)
                Rebuild();

            int count = visuals.Count;
            if (count == 0)
                return;

            // Motor state lives on MonoBehaviours, which a job may not touch,
            // so the values are copied here. No transform is read.
            for (int i = 0; i < count; i++)
                samples[i] = new Sample(visuals[i].Motor);

            new PlayerVisualJob
            {
                samples = samples,
                alpha = TickInterpolation.Alpha,
            }.Schedule(transforms).Complete();
        }

        /// <summary>
        /// Builds the native side from the registration list. Runs when a
        /// player appeared or left, never per frame.
        /// </summary>
        void Rebuild()
        {
            listChanged = false;
            Release();

            int count = visuals.Count;
            if (count == 0)
                return;

            Transform[] bodies = new Transform[count];
            for (int i = 0; i < count; i++)
                bodies[i] = visuals[i].transform;

            transforms = new TransformAccessArray(bodies);
            samples = new NativeArray<Sample>(count, Allocator.Persistent);
        }

        /// <summary>
        /// Native memory sits outside the garbage collector, so leaving this
        /// out leaks on every scene change.
        /// </summary>
        void Release()
        {
            if (transforms.isCreated)
                transforms.Dispose();

            if (samples.IsCreated)
                samples.Dispose();
        }

        /// <summary>
        /// Everything the job needs about one body, in one value type so the
        /// gather loop writes once per player.
        /// </summary>
        struct Sample
        {
            public Vector3 previousCenter;
            public Vector3 center;
            public float height;
            public float radius;

            public Sample(PlayerMotor motor)
            {
                previousCenter = motor.PreviousBodyCenter;
                center = motor.BodyCenter;
                height = motor.Height;
                radius = motor.Radius;
            }
        }

        /// <summary>
        /// Transforms are grouped by hierarchy root and each root goes to one
        /// worker. Every player is its own root, so the work really splits.
        /// </summary>
        [BurstCompile]
        struct PlayerVisualJob : IJobParallelForTransform
        {
            [ReadOnly] public NativeArray<Sample> samples;
            public float alpha;

            public void Execute(int index, TransformAccess transform)
            {
                Sample sample = samples[index];

                // World position on purpose: it overrides the parent, which
                // snaps to the tick position 40 times a second.
                transform.position =
                    Vector3.Lerp(sample.previousCenter, sample.center, alpha);

                // A primitive capsule is 2 m tall with radius 0.5 at scale 1,
                // so this matches the mesh to the collider in both measures.
                float xz = sample.radius * 2f;
                transform.localScale =
                    new Vector3(xz, sample.height * 0.5f, xz);
            }
        }
    }
}
