#:package Stride.Assets@4.4.0
#:property TargetFramework=net11.0
#:property ImplicitUsings=enable
#:property PublishAot=false
// Turns the recordings of EffectRecorder (KOTH_EFFECTLOG) into Stride's effect log
// asset, whose permutations the asset compiler builds into the bundle. Loads the
// game's assemblies from the build (Bin/Linux/Release/linux-x64) so their shader keys
// and colour transforms resolve; writes the log sorted, so it diffs well.
//   dotnet run Tools/effects/EffectLog.cs -- RECORD.bin [RECORD2.bin ...] OUT.sdeffectlog
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Stride.Assets.Effect;
using Stride.Core.Serialization;
using Stride.Core.Storage;
using Stride.Shaders;
using Stride.Shaders.Compiler;

string root = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFilePath") is string entry
    ? Path.GetDirectoryName(entry)! : ".", "../.."));
string build = Path.Combine(root, "Bin/Linux/Release/linux-x64");
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    string path = Path.Combine(build, name.Name + ".dll");
    return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
};
// Module initialisers register each assembly's serializers and shader keys.
foreach (string name in new[] { "Stride.Engine", "Stride.Rendering", "Stride.Particles", "Stride.Voxels", "KotH.Shared", "KotH.Game" })
{
    Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(name));
    RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
}

string output = args[^1];
var requests = new Dictionary<ObjectId, EffectCompileRequest>();
foreach (string input in args[..^1])
{
    byte[] data = File.ReadAllBytes(input);
    for (int at = 0; at < data.Length;)
    {
        int length = BitConverter.ToInt32(data, at);
        var reader = new BinarySerializationReader(new MemoryStream(data, at + 4, length));
        reader.Context.SerializerSelector = SerializerSelector.AssetWithReuse;
        EffectCompileRequest request = null;
        reader.SerializeExtended(ref request, ArchiveMode.Deserialize);
        requests[ShaderMixinObjectId.Compute(request.EffectName, request.UsedParameters)] = request;
        at += 4 + length;
    }
}

using (var stream = new MemoryStream())
{
    var store = new EffectLogStore(stream);
    foreach (var pair in requests.OrderBy(p => p.Value.EffectName, StringComparer.Ordinal).ThenBy(p => p.Key.ToString(), StringComparer.Ordinal))
        store.AddValue(new KeyValuePair<EffectCompileRequest, bool>(pair.Value, true));
    store.Save();
    File.WriteAllBytes(output, stream.ToArray());
}
Console.WriteLine($"{requests.Count} permutations to {output}");
