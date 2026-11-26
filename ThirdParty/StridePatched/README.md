# Stride (patched assemblies)

Stride 4.4.1 (`stride3d/stride`, 1be8907, MIT) rebuilt with `patches/koth.patch`, so the running round and the main
menu allocate nothing (Luca's choice, 24.11.2026). Same assembly names as the NuGet packages; the root
`Directory.Build.targets` swaps the DLLs in `lib/` for theirs, on Linux and Windows alike: like the packages' `net10.0`
assemblies they check the OS at run time (`-p:KotHStridePatchedOff=true` builds without them).

- `build.sh` fetches the sources sparse (`sparse-checkout`) into `src/`, the Linux LFS binaries by hash, applies the
  patch and builds Core, Graphics (Vulkan), Rendering, Engine, UI, Particles and BepuPhysics into `lib/` (box `aot`).
- `src/` and `lib/` are not in git. After a change in `src/`: `git add -N sources && git diff -- sources >
  ../patches/koth.patch` (from `src/`), then `build.sh`. Every change is marked `KotH patch`.

## Patches

- Dispatcher and sorting: no closures, delegates cached, `KotHSort` instead of `Comparison` sorts, pooled batch state.
- Rendering: render effects, lighting and permutations without per-frame closures; light arrays and the clustered
  lights' dictionary only grow; shadow and output validator reuse; `RenderContext.GetShared` cached; struct keys
  compared without boxing; no shader source watcher (`EffectSystem`); render data arrays, the descriptor set pool and
  sort keys grow by doubling.
- Engine: `ProcessorManager` and animation without enumerators or LINQ; a clip's evaluator from its first frame.
- Particles: a restart kills particles in place instead of freeing pool and vertex layout; the seed generator reseeds;
  a vertex buffer that only changes size keeps its layout.
- UI render states pooled; Bepu `UnsortedO1List` reuses indexers; `BufferPool`, `BatchBase` without allocations.
