using System;
using Stride.Core.Mathematics;
using Stride.Particles;
using Stride.Particles.Initializers;

namespace KotH.Client.Match
{
    /// <summary>Places a particle on a zone's outline as the stretched InitialPositionSeed of one particle
    /// system per stretch did: a stretch picked by its length, then evenly along it, across it and up. One
    /// system then serves the whole outline (Stride spends CPU time per emitter and frame).</summary>
    public sealed class OutlineSeed : ParticleInitializer
    {
        readonly Vector3[] centres, alongs, acrosses;
        readonly float[] lengths, ends;
        readonly Vector3 min, max;
        readonly uint seedOffset;

        /// <param name="seed">The prefab's seed for 1 m of edge: X along it, Y up, Z across.</param>
        /// <param name="centres">Each stretch's middle in the system's frame, on the zone's floor.</param>
        /// <param name="turns">Each stretch's turn about Y from +X.</param>
        public OutlineSeed(InitialPositionSeed seed, Vector3[] centres, float[] turns, float[] lengths)
        {
            this.centres = centres;
            this.lengths = lengths;
            alongs = new Vector3[centres.Length];
            acrosses = new Vector3[centres.Length];
            ends = new float[centres.Length];
            float sum = 0f;
            for (int i = 0; i < centres.Length; i++)
            {
                Quaternion turn = Quaternion.RotationY(turns[i]);
                alongs[i] = Vector3.Transform(Vector3.UnitX, turn);
                acrosses[i] = Vector3.Transform(Vector3.UnitZ, turn);
                sum += lengths[i];
                ends[i] = sum;
            }
            (min, max, seedOffset) = (seed.PositionMin, seed.PositionMax, seed.SeedOffset);
            (InheritPosition, InheritRotation, InheritScale) = (seed.InheritPosition, seed.InheritRotation, seed.InheritScale);
            RequiredFields.Add(ParticleFields.Position);
            RequiredFields.Add(ParticleFields.RandomSeed);
        }

        /// <summary>The outline's whole length in metres.</summary>
        public float Length => ends.Length > 0 ? ends[^1] : 0f;

        public override void Initialize(ParticlePool pool, int startIdx, int endIdx, int maxCapacity)
        {
            if (ends.Length == 0 || !pool.FieldExists(ParticleFields.Position) || !pool.FieldExists(ParticleFields.RandomSeed))
                return;
            ParticleFieldAccessor<Vector3> position = pool.GetField(ParticleFields.Position);
            ParticleFieldAccessor<Vector3> old = pool.GetField(ParticleFields.OldPosition);
            ParticleFieldAccessor<RandomSeed> random = pool.GetField(ParticleFields.RandomSeed);
            float total = ends[^1];
            for (int i = startIdx; i != endIdx; i = (i + 1) % maxCapacity)
            {
                Particle particle = pool.FromIndex(i);
                RandomSeed seed = particle.Get(random);
                // The same random channels as InitialPositionSeed: X picks the stretch and the place on it.
                float at = seed.GetFloat(4445 + seedOffset) * total;
                int s = Stretch(at);
                float t = Math.Clamp((at - (ends[s] - lengths[s])) / lengths[s], 0f, 1f);
                Vector3 local = centres[s] + alongs[s] * ((min.X + (max.X - min.X) * t) * lengths[s])
                                + Vector3.UnitY * (min.Y + (max.Y - min.Y) * seed.GetFloat(5556 + seedOffset))
                                + acrosses[s] * (min.Z + (max.Z - min.Z) * seed.GetFloat(6667 + seedOffset));
                Vector3 world = WorldPosition + Vector3.Transform(local * WorldScale, WorldRotation);
                particle.Set(position, world);
                if (old.IsValid())
                    particle.Set(old, world);
            }
        }

        // The first stretch whose end lies past the distance along the outline.
        int Stretch(float at)
        {
            int low = 0, high = ends.Length - 1;
            while (low < high)
            {
                int mid = (low + high) >> 1;
                if (ends[mid] > at)
                    high = mid;
                else
                    low = mid + 1;
            }
            return low;
        }
    }
}
