using System;
using System.Collections.Generic;
using System.IO;
using KotH.MapFormat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace KotH.MapKit.Editor
{
    /// <summary>The kit's menu (KotH → Export map, Open maps folder, New map scene), the export
    /// itself with its preview picture, and the batch entry for the command line:
    /// Unity -batchmode -projectPath P -executeMethod KotH.MapKit.Editor.MapKitMenu.Batch [-kothScene S] [-kothOut DIR]</summary>
    public static class MapKitMenu
    {
        const int PreviewSize = 512;

        /// <summary>The game's maps folder, or another chosen in the kit (saved per machine).</summary>
        public static string MapsRoot
        {
            get => EditorPrefs.GetString("KotH.MapKit.MapsRoot", MapPackage.UserFolder());
            set => EditorPrefs.SetString("KotH.MapKit.MapsRoot", value);
        }

        [MenuItem("KotH/Export map %#e", priority = 1)]
        static void ExportMenu() => Export(SceneManager.GetActiveScene(), MapsRoot);

        [MenuItem("KotH/Open maps folder", priority = 2)]
        static void OpenFolder()
        {
            Directory.CreateDirectory(MapsRoot);
            EditorUtility.RevealInFinder(MapsRoot + Path.DirectorySeparatorChar);
        }

        [MenuItem("KotH/Choose maps folder…", priority = 3)]
        static void ChooseFolder()
        {
            string chosen = EditorUtility.OpenFolderPanel("Maps folder", MapsRoot, "");
            if (!string.IsNullOrEmpty(chosen))
                MapsRoot = chosen;
        }

        [MenuItem("KotH/New map scene", priority = 20)]
        static void NewScene()
        {
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                NewMapScene.Create();
        }

        /// <summary>Exports the scene into root/NAME and lists the outcome in the console.</summary>
        public static MapBuilder.Result Export(Scene scene, string root)
        {
            byte[] preview = Preview(scene);
            MapBuilder.Result result;
            List<string> notes;
            try
            {
                result = new MapExporter().Export(scene, root, preview, out notes);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException)
            {
                result = new MapBuilder.Result();
                result.Errors.Add(e.Message);
                notes = new List<string>();
            }
            foreach (string e in result.Errors)
                Debug.LogError("[KotH] " + e);
            foreach (string w in result.Warnings)
                Debug.LogWarning("[KotH] " + w);
            foreach (string n in notes)
                Debug.LogWarning("[KotH] " + n);
            string said = result.Ok
                ? $"Exported {result.Package.Manifest.Title} to {result.Package.Folder}; in the game host a round and pick it in the lobby (Esc → Map)."
                : $"{result.Errors.Count} error(s), nothing written: {result.Errors[0]}";
            if (result.Ok)
                Debug.Log("[KotH] " + said);
            if (!Application.isBatchMode)
                EditorUtility.DisplayDialog("KotH map kit", said + (result.Warnings.Count + notes.Count > 0 ? "\n\nWarnings in the Console." : ""), "OK");
            return result;
        }

        /// <summary>The camera's view (the scene's main camera, else the scene view's), its centre
        /// square at 512², without the kit's helpers; null without graphics.</summary>
        static byte[] Preview(Scene scene)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
                return null;
            Camera camera = null;
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Camera c in root.GetComponentsInChildren<Camera>())
                    if (camera == null || c.CompareTag("MainCamera"))
                        camera = c;
            if (camera == null && SceneView.lastActiveSceneView != null)
                camera = SceneView.lastActiveSceneView.camera;
            if (camera == null)
                return null;

            var hidden = new List<Renderer>();
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (MapIgnore ignore in root.GetComponentsInChildren<MapIgnore>())
                    foreach (Renderer r in ignore.GetComponentsInChildren<Renderer>())
                        if (r.enabled)
                        {
                            r.enabled = false;
                            hidden.Add(r);
                        }
            RenderTexture target = RenderTexture.GetTemporary(PreviewSize, PreviewSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture before = camera.targetTexture, active = RenderTexture.active;
            float aspect = camera.aspect;
            var read = new Texture2D(PreviewSize, PreviewSize, TextureFormat.RGBA32, false, false);
            try
            {
                camera.targetTexture = target;
                camera.aspect = 1f;
                camera.Render();
                return TextureExport.Read(target, read);
            }
            finally
            {
                camera.targetTexture = before;
                camera.aspect = aspect;
                camera.ResetAspect();
                RenderTexture.active = active;
                RenderTexture.ReleaseTemporary(target);
                Object.DestroyImmediate(read);
                foreach (Renderer r in hidden)
                    r.enabled = true;
            }
        }

        /// <summary>Command line: exports -kothScene (else the first scene in the build settings or
        /// the open one) into -kothOut (else the maps folder); exit code 0 on success.</summary>
        public static void Batch()
        {
            string[] args = Environment.GetCommandLineArgs();
            string Arg(string name)
            {
                int i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
            string path = Arg("-kothScene");
            if (path == null && EditorBuildSettings.scenes.Length > 0)
                path = EditorBuildSettings.scenes[0].path;
            Scene scene = path != null ? EditorSceneManager.OpenScene(path) : SceneManager.GetActiveScene();
            MapBuilder.Result result = Export(scene, Arg("-kothOut") ?? MapsRoot);
            EditorApplication.Exit(result.Ok ? 0 : 1);
        }
    }
}
