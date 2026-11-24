using System.Collections.Concurrent;

namespace Stride.Rendering.Voxels
{
    /// <summary>KotH patch: composed parameter keys and names, made once and then looked up, since the
    /// layouts ask for them every frame (ComposeWith and string concatenation allocated each time).</summary>
    public static class VoxelNames
    {
        static readonly ConcurrentDictionary<(ParameterKey Key, string Name), ParameterKey> composed = new();
        static readonly ConcurrentDictionary<(string First, string Second), string> joined = new();
        static readonly string[] samplers = new string[16];

        public static T Compose<T>(T key, string name) where T : ParameterKey =>
            (T)(composed.TryGetValue((key, name), out ParameterKey known) ? known : composed.GetOrAdd((key, name), key.ComposeWith(name)));

        public static string Join(string first, string second) =>
            joined.TryGetValue((first, second), out string known) ? known : joined.GetOrAdd((first, second), first + second);

        /// <summary>"AttributeSamplers[id]." + name.</summary>
        public static string Sampler(int id, string name) =>
            Join(id < samplers.Length ? samplers[id] ??= $"AttributeSamplers[{id}]." : $"AttributeSamplers[{id}].", name);
    }
}
