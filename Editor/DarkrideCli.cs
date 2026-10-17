// Batch mode:
//   Unity -batchmode -projectPath <project> -executeMethod Darkride.Exporter.DarkrideCli.ExportScene
//         -darkrideScene Assets/Scenes/Room.unity -darkrideOut /path/to/exports [-darkrideFolder room-test]
//         [-darkrideUpload] [-darkrideFlat] [-darkrideProbeSpacing 1] [-darkrideNoProbeVolume]
//         [-darkrideProbeCheckSpheres] [-darkrideProbeDiagnostic [-darkrideWatch Chrome,Green]] -logFile export.log
// Without -darkrideUpload the folder is written locally with file: links. -darkrideUpload uses the login saved by
// the window. Not -nographics: the reflection probe is resampled on the GPU. -darkrideProbeSpacing is the probe
// volume grid's spacing in metres (1 by default), -darkrideNoProbeVolume leaves the probe volume out,
// -darkrideProbeCheckSpheres puts five white balls lit by the probes alone into Unity's view and scene.json.

using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Darkride.Exporter
{
    public static class DarkrideCli
    {
        public static void ExportScene()
        {
            int code = 0;
            try
            {
                string scene = Arg("-darkrideScene");
                if (!string.IsNullOrEmpty(scene)) EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
                var options = new ExportOptions
                {
                    OutputRoot = Arg("-darkrideOut"),
                    FolderName = Arg("-darkrideFolder"),
                    Upload = HasFlag("-darkrideUpload"),
                    KeepHierarchy = !HasFlag("-darkrideFlat"),
                    ExportProbeVolume = !HasFlag("-darkrideNoProbeVolume"),
                    ProbeCheckSpheres = HasFlag("-darkrideProbeCheckSpheres"),
                    ProbeSpacing = float.TryParse(Arg("-darkrideProbeSpacing") ?? "", NumberStyles.Float, CultureInfo.InvariantCulture, out float spacing) ? spacing : 1.0f,
                };
                if (HasFlag("-darkrideScreenshotOnly"))
                {
                    string root = string.IsNullOrEmpty(options.OutputRoot) ? ExportOptions.DefaultOutputRoot() : options.OutputRoot;
                    Directory.CreateDirectory(root);
                    var report = new Report();
                    Screenshot.Write(Screenshot.SceneCamera(), Path.Combine(root, "unity-view-check.png"), 1920, 1080, report);
                    Debug.Log("[darkride] " + report.Text());
                    if (Application.isBatchMode) EditorApplication.Exit(0);
                    return;
                }
                if (HasFlag("-darkrideProbeDiagnostic"))
                {
                    // the reflection probes as Unity applies them (ProbeDiagnostic.cs): the pipeline in force, what the
                    // watched materials' renderers get, and the camera's view as is, box projection off, probes off
                    string root = string.IsNullOrEmpty(options.OutputRoot) ? ExportOptions.DefaultOutputRoot() : options.OutputRoot;
                    string diagDir = Path.Combine(root, "probe-diagnostic");
                    Directory.CreateDirectory(diagDir);
                    string watch = Arg("-darkrideWatch") ?? "Chrome,Green";
                    ProbeDiagnostic.Run(diagDir, watch.Split(','));
                    if (Application.isBatchMode) EditorApplication.Exit(0);
                    return;
                }
                if (HasFlag("-darkrideDumpLightmaps"))
                {
                    // the lightmaps as the editor holds them (its imported textures, read back from the GPU), beside
                    // the files on disk, for telling a stale import from a bad export
                    string dumpDir = Path.Combine(string.IsNullOrEmpty(options.OutputRoot) ? ExportOptions.DefaultOutputRoot() : options.OutputRoot, "imported-lightmaps");
                    Directory.CreateDirectory(dumpDir);
                    var maps = LightmapSettings.lightmaps;
                    for (int i = 0; i < maps.Length; i++)
                    {
                        if (maps[i].lightmapColor == null) continue;
                        var image = GpuReadback.ReadFloat(maps[i].lightmapColor);
                        Exr.WriteHalfRgb(Path.Combine(dumpDir, "Lightmap-" + i + "-imported.exr"), image);
                        Debug.Log("[darkride] imported lightmap " + i + ": " + maps[i].lightmapColor.width + " x " + maps[i].lightmapColor.height + " " + (maps[i].lightmapColor is Texture2D t ? t.format.ToString() : "") + " written");
                    }
                    if (Application.isBatchMode) EditorApplication.Exit(0);
                    return;
                }
                var client = options.Upload ? DarkridePrefs.Client() : null;
                var result = SceneExport.Run(options, client, (step, fraction) => Debug.Log("[darkride] " + step));
                Debug.Log("[darkride] export done\n" + result.Report.Text());
                Debug.Log("[darkride] scene.json: " + result.SceneUrl);
                Debug.Log("[darkride] import script: " + result.ImportScriptUrl);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Debug.Log("[darkride] export FAILED: " + e.Message);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }

        static bool HasFlag(string name)
        {
            foreach (string arg in Environment.GetCommandLineArgs())
                if (arg == name) return true;
            return false;
        }
    }
}
