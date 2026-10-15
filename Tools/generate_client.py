"""Writes the client's visible assets: materials, procedural models and MainScene.

The look follows Prototype.unity: Ground, Rock (mountain), Prop (steps and
the crouch slab), Player (bodies); one directional light at Unity's angle,
camera at 85 degrees vertical FOV and 300 m far plane. Geometry comes from
the same dump as ArenaCollision, mirrored the same exact way. Ids derive
from names, so regenerating keeps every id and Game Studio edits survive
as long as they are made in the generator too.
"""
import os, sys
sys.path.insert(0, os.path.dirname(__file__))
from stride_yaml import guid, key, f32, vec3, quat
from generate_arena import read_colliders, mirror_v, mirror_q

ROOT = os.path.join(os.path.dirname(__file__), "..")
ASSETS = os.path.join(ROOT, "KotH.Game", "Assets")

# Unity _BaseColor (sRGB, as entered) and _Smoothness, from Assets/Materials.
MATERIALS = {
    "Ground": ((0.38, 0.4, 0.42), 0.15),
    "Rock": ((0.46, 0.42, 0.36), 0.1),
    "Prop": ((0.68, 0.7, 0.72), 0.25),
    "Player": ((0.85, 0.42, 0.12), 0.35),
}

# Which material each Unity object used (MeshRenderer in Prototype.unity).
ROCK = ("Plateau", "Ramp20", "Ramp35", "Ramp45", "Ramp55")

# Kept from the template scene so Game Studio keeps recognising them.
SKYBOX_ENTITY = "7A7852EF-BC8E-4CD6-AEFC-28048C231EE5"
CAMERA_SLOT = "A03721D3-C7B2-4AAB-A434-9425956E6C4C"


def write(rel, text):
    path = os.path.join(ASSETS, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", newline="\n") as f:
        f.write(text)
    return path


def ref(name):
    return "%s:%s" % (guid(name + "/asset"), name)


def material(name, color, smoothness):
    return """!MaterialAsset
Id: %s
SerializedVersion: {Stride: 2.0.0.0}
Tags: []
Attributes:
    MicroSurface: !MaterialGlossinessMapFeature
        GlossinessMap: !ComputeFloat
            Value: %s
    Diffuse: !MaterialDiffuseMapFeature
        DiffuseMap: !ComputeColor
            Value: {R: %s, G: %s, B: %s, A: 1.0}
    DiffuseModel: !MaterialDiffuseLambertModelFeature {}
    Specular: !MaterialMetalnessMapFeature
        MetalnessMap: !ComputeFloat
            Value: 0.0
    SpecularModel: !MaterialSpecularMicrofacetModelFeature
        Fresnel: !MaterialSpecularMicrofacetFresnelSchlick {}
        Visibility: !MaterialSpecularMicrofacetVisibilitySmithSchlickGGX {}
        NormalDistribution: !MaterialSpecularMicrofacetNormalDistributionGGX {}
        Environment: !MaterialSpecularMicrofacetEnvironmentGGXLUT {}
    Overrides:
        UVScale: {X: 1.0, Y: 1.0}
Layers: {}
""" % (guid("Materials/" + name + "/asset"), f32(smoothness), f32(color[0]), f32(color[1]), f32(color[2]))


def font(name, font_type, size):
    """OpenSans from Resources/Fonts, compiled into a texture at build time.
    No runtime rasterising, so nothing depends on FreeType when it runs.
    The region ends at DEL like Stride's samples: a bare "~" reads as null."""
    return """!SpriteFont
Id: %s
SerializedVersion: {Stride: 2.1.0.0}
Tags: []
FontSource: !FileFontProvider
    Source: !file ../../Resources/Fonts/OpenSans-Regular.ttf
FontType: %s
    Size: %s
    CharacterSet: null
    CharacterRegions:
        %s:
            Start: ' '
            End: "\\x7F"
""" % (guid("Fonts/" + name + "/asset"), font_type, f32(size), key("Fonts/" + name + "/region"))


def model(name, shape, mat):
    return """!ProceduralModelAsset
Id: %s
SerializedVersion: {Stride: 2.0.0.0}
Tags: []
Type: %s
    Scale: {X: 1.0, Y: 1.0, Z: 1.0}
    UvScale: {X: 1.0, Y: 1.0}
    LocalOffset: {X: 0.0, Y: 0.0, Z: 0.0}
    NumberOfTextureCoordinates: 10
    MaterialInstance:
        Material: %s
""" % (guid("Models/" + name + "/asset"), shape, ref("Materials/" + mat))


def transform(name, pos, rot, scale, children=()):
    kids = "{}" if not children else "\n" + "\n".join(
        "                            %s: ref!! %s" % (key(name + "/child/" + c), guid(c + "/transform")) for c in children)
    return """                    %s: !TransformComponent
                        Id: %s
                        Position: %s
                        Rotation: %s
                        Scale: %s
                        Children: %s""" % (key(name + "/transform"), guid(name + "/transform"),
                                           vec3(pos), quat(rot), vec3(scale), kids)


def model_component(name, model_name):
    return """                    %s: !ModelComponent
                        Id: %s
                        Model: %s
                        Materials: {}""" % (key(name + "/model"), guid(name + "/model"), ref("Models/" + model_name))


def entity(name, components, eid=None):
    return """        -   Entity:
                Id: %s
                Name: %s
                Components:
%s""" % (eid or guid(name + "/entity"), name, "\n".join(components))


def arena_visual():
    """The drawn arena: one child per collider, scaled unit cubes and the plane."""
    parts, names = [], []
    for path, kind, pos, rot, lossy, size in read_colliders():
        name = path.split("/")[-1]
        vis = "Visual/" + name
        if kind == "BoxCollider":
            full = tuple(s * l for s, l in zip(size, lossy))
            comps = [transform(vis, mirror_v(pos), mirror_q(rot), full),
                     model_component(vis, "RockCube" if name in ROCK else "PropCube")]
        else:
            comps = [transform(vis, (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0)),
                     model_component(vis, "Ground")]
        names.append(vis)
        parts.append(entity(name, comps, guid(vis + "/entity")))

    root = entity("ArenaVisual", [transform("ArenaVisual", (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0),
                                            (1.0, 1.0, 1.0), names)])
    return root, parts


def scene():
    camera = entity("Camera", [
        transform("Camera", (0.0, 1.68, 0.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0)),
        """                    %s: !CameraComponent
                        Id: %s
                        Name: null
                        Projection: Perspective
                        VerticalFieldOfView: 85.0
                        NearClipPlane: 0.04
                        FarClipPlane: 300.0
                        Slot: %s""" % (key("Camera/camera"), guid("Camera/camera"), CAMERA_SLOT),
        """                    %s: !KotH.FirstPersonCamera
                        Id: %s
                        NearClipMargin: 0.7""" % (key("Camera/fps"), guid("Camera/fps")),
    ])

    # Unity's light: rotation (0.408, -0.235, 0.109, 0.875), warm white.
    # Unity rendered in gamma space without tone mapping; these intensities
    # are fitted so lit and shadowed ground match its screenshot in linear.
    light = entity("Directional light", [
        transform("Light", (0.0, 3.0, 0.0), mirror_q((0.40821788, -0.23456968, 0.10938163, 0.8754261)),
                  (1.0, 1.0, 1.0)),
        """                    %s: !LightComponent
                        Id: %s
                        Type: !LightDirectional
                            Color: !ColorRgbProvider
                                Value: {R: 1.0, G: 0.95686275, B: 0.8392157}
                            Shadow:
                                Enabled: true
                                Filter: !LightShadowMapFilterTypePcf
                                    FilterSize: Filter5x5
                                Size: Large
                                DepthRange: {}
                                PartitionMode: !LightDirectionalShadowMap.PartitionLogarithmic {}
                                ComputeTransmittance: false
                                BiasParameters: {}
                        Intensity: 1.54""" % (key("Light/light"), guid("Light/light")),
    ])

    skybox = entity("Skybox", [
        transform("Skybox", (0.0, 2.0, -2.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0)),
        """                    50ab9d07393b0d80a6cff9ffb3104e4a: !BackgroundComponent
                        Id: F22FCA71-C8F9-4653-8466-CE6945A2DAB3
                        Texture: C8F1566C-4832-4943-B46D-E72AD417E250:Skybox texture
                        Intensity: 0.35
                    5733ca1e623fcf399f389e2b0e7c54ec: !LightComponent
                        Id: 1FACD078-5BD4-4D10-B178-959F2F56BDCE
                        Type: !LightSkybox
                            Skybox: F9A75D1E-7197-4A46-90CF-5A7F970DCDCA:Skybox
                        Intensity: 0.26""",
    ], SKYBOX_ENTITY)

    root = entity("GameRoot", [
        transform("GameRoot", (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0)),
        """                    %s: !KotH.GameRoot
                        Id: %s
                        BodyModel: %s
                        LabelFont: %s""" % (key("GameRoot/root"), guid("GameRoot/root"), ref("Models/Body"),
                                            ref("Fonts/Label")),
        """                    %s: !KotH.SimulationDriver
                        Id: %s""" % (key("GameRoot/driver"), guid("GameRoot/driver")),
        """                    %s: !KotH.PlayerVisualSystem
                        Id: %s""" % (key("GameRoot/visuals"), guid("GameRoot/visuals")),
    ])

    hud = entity("Hud", [
        transform("Hud", (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), (1.0, 1.0, 1.0)),
        """                    %s: !KotH.Crosshair
                        Id: %s""" % (key("Hud/crosshair"), guid("Hud/crosshair")),
        """                    %s: !KotH.PerformanceOverlay
                        Id: %s
                        Font: %s""" % (key("Hud/overlay"), guid("Hud/overlay"), ref("Fonts/Overlay")),
    ])

    arena_root, arena_parts = arena_visual()
    roots = [guid("GameRoot/entity"), guid("Camera/entity"), guid("Directional light/entity"),
             SKYBOX_ENTITY, guid("Hud/entity"), guid("ArenaVisual/entity")]
    parts = [root, camera, light, skybox, hud, arena_root] + arena_parts
    return """!SceneAsset
Id: A1A78B9B-C5BB-4CD0-B45E-BE04A2C3C984
SerializedVersion: {Stride: 3.1.0.1}
Tags: []
ChildrenIds: []
Offset: {X: 0.0, Y: 0.0, Z: 0.0}
Hierarchy:
    RootParts:
%s
    Parts:
%s
""" % ("\n".join("        - ref!! " + r for r in roots), "\n".join(parts))


def main():
    for name, (color, smooth) in MATERIALS.items():
        write("Materials/%s.sdmat" % name, material(name, color, smooth))

    # Unity's Plane: 10 m at scale 5, so 50 m with 10 x 10 quads, facing up.
    write("Models/Ground.sdpromodel", model("Ground", """!PlaneProceduralModel
    Size: {X: 50.0, Y: 50.0}
    Tessellation: {X: 10, Y: 10}""", "Ground"))
    write("Models/RockCube.sdpromodel", model("RockCube", """!CubeProceduralModel
    Size: {X: 1.0, Y: 1.0, Z: 1.0}""", "Rock"))
    write("Models/PropCube.sdpromodel", model("PropCube", """!CubeProceduralModel
    Size: {X: 1.0, Y: 1.0, Z: 1.0}""", "Prop"))
    # Unity's capsule primitive: 2 m tall, radius 0.5; the visual system scales it.
    write("Models/Body.sdpromodel", model("Body", """!CapsuleProceduralModel
    Length: 1.0
    Radius: 0.5
    Tessellation: 16""", "Player"))

    # The readout is drawn at exactly its size, pixel for pixel; the label
    # scales with distance, which a distance field survives.
    write("Fonts/Overlay.sdfnt", font("Overlay", "!OfflineRasterizedSpriteFontType", 18.0))
    write("Fonts/Label.sdfnt", font("Label", "!SignedDistanceFieldSpriteFontType", 48.0))

    print(write("MainScene.sdscene", scene()))


main()
