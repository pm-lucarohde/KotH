# Regression Unity → Stride

The eight movement and combat probes and the DeterminismProbe against the frozen Unity reference
(`Reference/unity/runs/run1..3`, three runs each): `Tools/Regress.cs` compares numbers (5 mm), `Tools/Verdict.cs` verdicts.

## Result (stage 6, 15.10.2026; still holds)

**94 of 94 verdicts as in Unity** (the 95th has no Unity counterpart); the DeterminismProbe is bit-exact, three runs
of each probe are byte-identical.

| Probe | Cases | within 5 mm | display rounding | only 1 % | over | Note |
|---|---|---|---|---|---|---|
| SlopeJump | 6 | 6 | | | | line-identical |
| Crest | 18 | 17 | | 1 | | crouched 0°: on top at tick 196 instead of 195 |
| Impact | 28 | 24 | 4 | | | half values like 2.875 shown as "2.87" |
| JumpHeight | 18 | 18 | | | | |
| Slide | 41 | 41 | | | | byte-identical |
| Sprint | 111 | 111 | | | | byte-identical |
| Melee | 5 (+1) | 5 | | | | "== Font" missing (HUD) |
| Step | 140 | 129 | | 1 | 2 | see below |

## Remaining deviations

1. **Display rounding:** Mono formats floats to 7 digits first, .NET rounds the exact value (2.87499… → "2.87"; also "-0.00").
2. **Precision:** Unity's editor Mono evaluates expressions in double (`RotationProbe`), the port in float like IL2CPP:
   Crest crouched one tick later (5.034 instead of 5.033 m).
3. **Step, right after a teleport:** feet exactly at y = 0, so whether an overlap reports mere touching is float noise
   (PhysX and Bepu alike); only "+ Steps40 still" ends elsewhere (x −14.1 / z 19.0 instead of −14.0 / 19.3), same verdict.
4. **Step, ground angle in wobble cases:** last displayed digit (2.1 instead of 2.0° at t018), from point 2.

## Bugs found during the regression

| Commit | Bug |
|---|---|
| 43c90b0 | Bepu's compound sweep saw only maxDistance² for maxDistance < 1; fix: path as velocity over T = 1 |
| d586828 | Bepu reports mere touching with noise depth; `Depenetrate` lifted by the skin (threshold 1e-5 m) |
| 3e36f07 | `StaticComponent.Teleport` writes pose − centre of mass back; crouched transforms sat up to 0.45 m high |
| d1999a5 | Single `CapsuleCast` (TryStepUp) counted a start overlap as blocked; Unity ignores colliders it starts in |
| d9d3b23 | Penetration direction pointed to the Bepu side instead of the PhysX side |

World queries against PhysX (`worldquery`, 6,500 per kind): identical hit sets, distances p99 < 5 µm.

## NativeAOT

All builds use **x86-64-v2** (`Directory.Build.props`); JIT matches only with `DOTNET_EnableAVX=0`, then AOT and JIT
are byte-identical on every probe. All peers need the same binary.

## Intended changes since the move (hash chain now `C857D39208F53C83`)

| # | Date | Change | Check |
|---|---|---|---|
| 1 | 16.10. | Held space no longer triggers the double jump (Stride's `IsKeyPressed` repeats) | `KeyPresses` |
| 2 | 16.10. | Slide right out of a sprint jump when landing with crouch held | 3 slide cases without Unity |
| 3 | 16.10. | No more stalling when sprinting up stairs (step-up within `stepSettleTicks`) | `stairrun`: 154/2916 → 0 |
| 4 | 20.10. | `Nudge` sweeps instead of pushing bodies through ramps | `rampfoot` crowd: 8/8 → 0 |
| 5 | 20.10. | Crouched at a ramp's side edge no longer sinks (downward push swept) | `rampfoot` 5,940 runs: 286 → 0 |
| 6 | 20.10. | Crouched edges between step height and sphere centre count as walls (no jitter) | `crouchedge`: 20 → 0 |
| 7 | 20.10. | Knockback by damage percent (12 m / 1.2 m at 100 %, speed carry from 10 %) | `knockback` |
| 8 | 20.10. | Hit area of three cylinders following the mushroom figure | MeleeProbe measures on purpose |
| 9 | 20.10. | Third-person camera (mouse wheel), simulation untouched | `thirdperson` |
| 10 | 20.10. | Knockback bounces off walls (40 %, `KnockWallBounce`) | `wallknock` |
| 11 | 20.10. | Capture zone and rounds (`HillMatch`) | `capture` |
| 12 | 20.10. | No static simulation state (several worlds per process) | determinism: two worlds |
| 13 | 20.10. | Same results under glibc 2.35 and 2.43; builds need glibc ≥ 2.43 | `.work/tools/libmcheck` |
| 14 | 20.10. | Snapshot as bytes (`StateCodec`), rollback across joins and leaves | `netstate` |
| 15 | 20.10. | Rollback core: host, prediction, repair | `netplay` (11 peers, lossy net) |
| 16 | 20.10. | Lobby, countdown, 12 spawn points | `capture`, `netplay` |
| 17 | 20.10. | Prediction continues foreign turns for 8 ticks | `netplay` |
| 18 | 20.10. | Real UDP (LiteNetLib), datagrams ≤ 1200 B, input clamping | `netplay`, UDP smoke test |
| 19 | 21.10. | Alone only while capturing; the holder keeps scoring with others inside | `capture` |
