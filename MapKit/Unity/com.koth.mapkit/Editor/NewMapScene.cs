using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KotH.MapKit.Editor
{
    /// <summary>A new scene with what every map needs: Map info, a 64 m ground, the zone on it,
    /// four spawns with scale figures (1.8 m players), a camera for the preview and a sun.</summary>
    public static class NewMapScene
    {
        public static Scene Create()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var map = new GameObject("Map");
            map.AddComponent<MapInfo>();

            var sun = new GameObject("Sun");
            Light light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(50f, 150f, 0f);

            var camera = new GameObject("Camera (preview)") { tag = "MainCamera" };
            camera.AddComponent<Camera>().farClipPlane = 2000f;
            camera.transform.position = new Vector3(26f, 14f, -26f);
            camera.transform.LookAt(new Vector3(0f, 4f, 0f));

            GameObject ground = Primitive(PrimitiveType.Plane, "Ground", null, Vector3.zero, new Vector3(6.4f, 1f, 6.4f));
            MapObject surface = ground.AddComponent<MapObject>();
            surface.template = MapTemplate.Terrain;

            var zone = new GameObject("Zone");
            zone.AddComponent<MapZone>();
            zone.transform.position = new Vector3(0f, 1.75f, 0f);
            zone.transform.localScale = new Vector3(4f, 3.5f, 4f);

            int n = 0;
            foreach (Vector2 at in new[] { new Vector2(20, 20), new Vector2(-20, 20), new Vector2(20, -20), new Vector2(-20, -20) })
            {
                var spawn = new GameObject($"Spawn {++n}");
                spawn.AddComponent<MapSpawn>();
                spawn.transform.position = new Vector3(at.x, 0f, at.y);
                spawn.transform.LookAt(new Vector3(0f, 0f, 0f));
                GameObject figure = Primitive(PrimitiveType.Capsule, "Scale figure", spawn.transform, new Vector3(0f, 0.9f, 0f),
                                              new Vector3(0.8f, 0.9f, 0.8f));
                figure.AddComponent<MapIgnore>();
                figure.tag = "EditorOnly";
            }
            Selection.activeGameObject = map;
            return scene;
        }

        /// <summary>A Unity primitive without its collider (the game makes its own from the mesh).</summary>
        public static GameObject Primitive(PrimitiveType type, string name, Transform parent, Vector3 position, Vector3 scale)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            return go;
        }
    }
}
