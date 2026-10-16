"""Writes ArenaCollision.sdprefab from the Unity dump of Prototype.unity.

One-off migration tool: since 16.10.2026 Game Studio owns the assets it
wrote (Luca's decision). Running it again overwrites every edit made there.

Input: Reference/unity/dump/colliders.txt and ground.txt, every float as its
exact bits. Unity (left-handed, +Z forward) maps to Stride (right-handed,
-Z forward) by z -> -z and quaternion (x, y, z, w) -> (-x, -y, z, w), which
is exact. Boxes keep scale 1 and carry their size in the collider instead.
Order is Unity's instance-id order, which is the order Unity's depenetration
summed its corrections in.
"""
import os, sys
sys.path.insert(0, os.path.dirname(__file__))
from stride_yaml import guid, key, f32, vec3, quat, bits_to_float

ROOT = os.path.join(os.path.dirname(__file__), "..")
DUMP = os.path.join(ROOT, "Reference", "unity", "dump")

# Unity's ground is a one-sided plane; which index order faces up in Bepu
# was measured (see WorldQueryProbe), not assumed.
FLIP_GROUND_WINDING = os.environ.get("FLIP_GROUND_WINDING", "0") == "1"


def floats(cells):
    return [bits_to_float(c.split("|")[1]) for c in cells]


def mirror_v(v):
    return (v[0], v[1], -v[2])


def mirror_q(q):
    return (-q[0], -q[1], q[2], q[3])


def read_spawns():
    """Where Prototype.unity placed the player and the dummy, as markers."""
    out = []
    for line in open(os.path.join(DUMP, "colliders.txt")):
        cells = line.rstrip("\n").split(";")
        if int(cells[2].split()[1]) != 6:
            continue
        i = cells.index("pos")
        out.append((cells[0] + "Spawn", floats(cells[i + 1:i + 4]), floats(cells[i + 5:i + 9])))
    return out


def read_colliders():
    out = []
    for line in open(os.path.join(DUMP, "colliders.txt")):
        cells = line.rstrip("\n").split(";")
        path, kind, layer = cells[0], cells[1], int(cells[2].split()[1])
        if layer == 6 or kind not in ("BoxCollider", "MeshCollider"):
            continue
        i = cells.index("pos")
        pos = floats(cells[i + 1:i + 4])
        rot = floats(cells[i + 5:i + 9])
        lossy = floats(cells[i + 10:i + 13])
        size = floats(cells[i + 13:i + 16]) if kind == "BoxCollider" else None
        out.append((path, kind, pos, rot, lossy, size))
    return out


def read_ground():
    verts, tris = {}, []
    for line in open(os.path.join(DUMP, "ground.txt")):
        cells = line.rstrip("\n").split(";")
        if cells[0] == "v":
            verts[int(cells[1])] = floats(cells[2:5])
        elif cells[0] == "t":
            tris.append(tuple(int(c) for c in cells[1:4]))
    return verts, tris


def transform(name, pos, rot):
    return """                    %s: !TransformComponent
                        Id: %s
                        Position: %s
                        Rotation: %s
                        Scale: {X: 1.0, Y: 1.0, Z: 1.0}
                        Children: {}""" % (key(name + "/transform"), guid(name + "/transform"), vec3(pos), quat(rot))


def static(name, colliders):
    return """                    %s: !Stride.BepuPhysics.StaticComponent,Stride.BepuPhysics
                        Id: %s
                        Collider: !Stride.BepuPhysics.Definitions.Colliders.CompoundCollider,Stride.BepuPhysics
                            Colliders:
%s
                        CollisionGroup: {}
                        ContactEventHandler: null""" % (key(name + "/static"), guid(name + "/static"), colliders)


def box(name, size):
    return """                                %s: !Stride.BepuPhysics.Definitions.Colliders.BoxCollider,Stride.BepuPhysics
                                    Mass: 1.0
                                    PositionLocal: {X: 0.0, Y: 0.0, Z: 0.0}
                                    RotationLocal: {X: 0.0, Y: 0.0, Z: 0.0, W: 1.0}
                                    Size: %s""" % (key(name + "/box"), vec3(size))


def triangle(name, i, a, b, c):
    return """                                %s: !Stride.BepuPhysics.Definitions.Colliders.TriangleCollider,Stride.BepuPhysics
                                    Mass: 1.0
                                    PositionLocal: {X: 0.0, Y: 0.0, Z: 0.0}
                                    RotationLocal: {X: 0.0, Y: 0.0, Z: 0.0, W: 1.0}
                                    A: %s
                                    B: %s
                                    C: %s""" % (key("%s/tri%d" % (name, i)), vec3(a), vec3(b), vec3(c))


def entity(name, components):
    eid = guid(name + "/entity")
    return eid, """        -   Entity:
                Id: %s
                Name: %s
                Components:
%s""" % (eid, name, "\n".join(components))


def main():
    parts = []
    for path, kind, pos, rot, lossy, size in read_colliders():
        name = path.split("/")[-1]
        if kind == "BoxCollider":
            full = tuple(s * l for s, l in zip(size, lossy))
            comps = [transform(name, mirror_v(pos), mirror_q(rot)), static(name, box(name, full))]
        else:
            verts, tris = read_ground()
            shapes = []
            for i, (ia, ib, ic) in enumerate(tris):
                a, b, c = (mirror_v(verts[j]) for j in (ia, ib, ic))
                if FLIP_GROUND_WINDING:
                    b, c = c, b
                shapes.append(triangle(name, i, a, b, c))
            comps = [transform(name, (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0)),
                     static(name, "\n".join(shapes))]
        parts.append(entity(name, comps))

    # Markers carry no collider, so they take no place in the query order.
    for name, pos, rot in read_spawns():
        parts.append(entity(name, [transform(name, mirror_v(pos), mirror_q(rot))]))

    asset = guid("ArenaCollision/asset")
    text = """!PrefabAsset
Id: %s
SerializedVersion: {Stride: 3.1.0.1}
Tags: []
Hierarchy:
    RootParts:
%s
    Parts:
%s
""" % (asset, "\n".join("        - ref!! " + eid for eid, _ in parts), "\n".join(p for _, p in parts))

    path = os.path.join(ROOT, "KotH.Shared", "Assets", "Arena", "ArenaCollision.sdprefab")
    with open(path, "w", newline="\n") as f:
        f.write(text)
    print(path, asset, len(parts), "entities")


if __name__ == "__main__":
    main()
