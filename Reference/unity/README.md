# Unity reference (b09cb6b, Unity 6000.3.21f1, editor Mono)

Frozen on 15.10.2026 before Unity left the repo (its source is in Git up to `577ed3f`). All in Unity coordinates
(+Z forward); `KotH.Probes` mirrors through `Ref`. The editor Mono rounds locals to float but evaluates every
expression in double (`RotationProbe`), the port is pure float like IL2CPP: compare with tolerance, never bit-exactly.

- `runs/run1..3/`: the eight movement and combat probes, three times (probe lines only, all exit 0). Translated to
  English with decimal points on 28.10.2026, values unchanged.
- `runs/DeterminismProbe.txt`: all cases deterministic.
- `dump/ground.txt`: ground mesh in world coordinates with winding; `dump/colliders.txt`: the arena's colliders.
- `dump/rotations.txt.gz`: bits of `Quaternion.Euler` and `Quaternion*Vector3`; `dump/queries.txt.gz`: PhysX answers
  to 6,500 queries per kind.
- `dump/clock.txt`: Unity's player loop over 200 frames with fixed sleeps (ticks per frame, alpha, camera, eye, near plane).
- `dump/crouchoffset.txt`: proof that `ComputePenetration` measured the crouched hull 0.45 m too low.
