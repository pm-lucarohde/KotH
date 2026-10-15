"""Writes the simulation halves of the bodies: PlayerBody and DummyBody.

These carry no model, so the server and the probes can load them without
a GPU. The game's Player and Dummy prefabs nest them and add the visuals.
Every tuning value is written out and equals the C# default, which in turn
equals Unity's Player.prefab; ProbeDefaults checks both stay that way.
"""
import os, sys
sys.path.insert(0, os.path.dirname(__file__))
from stride_yaml import guid, key, f32

ROOT = os.path.join(os.path.dirname(__file__), "..")

MOTOR = [
    ("WalkSpeed", 5.175), ("SprintSpeed", 8.625), ("CrouchSpeed", 2.1),
    ("GroundAcceleration", 50.0), ("GroundDeceleration", 35.0), ("AirAcceleration", 7.5),
    ("JumpHeight", 1.6), ("DoubleJumpHeight", 1.0822), ("MinJumpHeight", 1.0744),
    ("MinJumpHold", 0.032), ("FullJumpHold", 0.2), ("DoubleJumpWindow", 1.0),
    ("DoubleTapWindow", 0.25), ("Gravity", -20.0),
    ("BodyRadius", 0.4), ("SkinWidth", 0.04), ("StandHeight", 1.8), ("CrouchHeight", 0.9),
    ("StandEyeHeight", 1.68), ("CrouchEyeHeight", 0.78), ("PoseChangeSpeed", 6.0),
    ("StandStepOffset", 0.45), ("CrouchStepOffset", 0.25), ("LedgeGuardDrop", 0.5),
    ("HeadroomMask", "Everything"),
    ("SlopeLimit", 50.0), ("GroundMask", "Everything"), ("GroundProbeExtra", 0.1),
    ("GroundSnapDistance", 0.3), ("MinSlideSpeed", 3.0), ("SlideConfirmTime", 0.075),
    ("StepProbeAhead", 0.05),
    ("SlideDeceleration", 6.0), ("MaxSlideSpeed", 15.0), ("SlideCooldown", 0.5),
]


def fields(pairs, pad):
    out = []
    for name, value in pairs:
        text = value if isinstance(value, str) else (str(value) if isinstance(value, int) else f32(value))
        out.append(" " * pad + "%s: %s" % (name, text))
    return "\n".join(out)


def body(name, dummy):
    eid = guid(name + "/entity")
    comps = []
    comps.append("""                    %s: !TransformComponent
                        Id: %s
                        Position: {X: 0.0, Y: 0.0, Z: 0.0}
                        Rotation: {X: 0.0, Y: 0.0, Z: 0.0, W: 1.0}
                        Scale: {X: 1.0, Y: 1.0, Z: 1.0}
                        Children: {}""" % (key(name + "/transform"), guid(name + "/transform")))
    comps.append("""                    %s: !Stride.BepuPhysics.StaticComponent,Stride.BepuPhysics
                        Id: %s
                        CollisionLayer: Layer6
                        Collider: !Stride.BepuPhysics.Definitions.Colliders.CompoundCollider,Stride.BepuPhysics
                            Colliders:
                                %s: !Stride.BepuPhysics.Definitions.Colliders.CapsuleCollider,Stride.BepuPhysics
                                    Mass: 1.0
                                    PositionLocal: {X: 0.0, Y: 0.0, Z: 0.0}
                                    RotationLocal: {X: 0.0, Y: 0.0, Z: 0.0, W: 1.0}
                                    Radius: 0.4
                                    Length: 1.0
                        CollisionGroup: {}
                        ContactEventHandler: null""" % (key(name + "/hull"), guid(name + "/hull"), key(name + "/hullshape")))
    comps.append("""                    %s: !KotH.PlayerMotor
                        Id: %s
%s""" % (key(name + "/motor"), guid(name + "/motor"), fields(MOTOR, 24)))
    comps.append("""                    %s: !KotH.MeleeAttack
                        Id: %s
                        Range: 3.5
                        DamagePercent: 1.2
                        CooldownTicks: 13""" % (key(name + "/melee"), guid(name + "/melee")))
    comps.append("""                    %s: !KotH.Hittable
                        Id: %s""" % (key(name + "/hittable"), guid(name + "/hittable")))
    if dummy:
        comps.append("""                    %s: !KotH.DamagePercent
                        Id: %s
                        MaxPercent: 1000.0""" % (key(name + "/damage"), guid(name + "/damage")))
        comps.append("""                    %s: !KotH.TrainingDummy
                        Id: %s""" % (key(name + "/dummy"), guid(name + "/dummy")))

    return """!PrefabAsset
Id: %s
SerializedVersion: {Stride: 3.1.0.1}
Tags: []
Hierarchy:
    RootParts:
        - ref!! %s
    Parts:
        -   Entity:
                Id: %s
                Name: %s
                Components:
%s
""" % (guid(name + "/asset"), eid, eid, name, "\n".join(comps))


for name, dummy in (("PlayerBody", False), ("DummyBody", True)):
    path = os.path.join(ROOT, "KotH.Shared", "Assets", "Bodies", name + ".sdprefab")
    with open(path, "w", newline="\n") as f:
        f.write(body(name, dummy))
    print(path, guid(name + "/asset"))
