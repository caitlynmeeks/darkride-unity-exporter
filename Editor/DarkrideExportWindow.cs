// Darkride > Export Scene… and Export Selection…: the account (server, login), the export's folder and options,
// and the Export button. Settings persist in EditorPrefs.

using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Darkride.Exporter
{
    public class DarkrideExportWindow : EditorWindow
    {
        [MenuItem("Darkride/Export Scene…")]
        public static void OpenForScene()
        {
            var window = GetWindow<DarkrideExportWindow>("Darkride Export");
            window.selectionOnly = false;
            window.Show();
        }

        [MenuItem("Darkride/Export Selection…")]
        public static void OpenForSelection()
        {
            var window = GetWindow<DarkrideExportWindow>("Darkride Export");
            window.selectionOnly = true;
            window.Show();
        }

        [MenuItem("Darkride/Export Selection…", true)]
        static bool ValidateSelection() => Selection.gameObjects.Length > 0;

        MetaverseClient client;
        string password = "";
        string status = "";
        string lastResult = "";
        string lastLocalPath = "";
        bool selectionOnly;
        string folderName = "";
        string folderScene = "";
        Vector2 scroll;

        void OnEnable()
        {
            client = DarkridePrefs.Client();
            if (string.IsNullOrEmpty(client.Server)) client.Server = "https://vr.darkride.ai";
        }

        void OnGUI()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            if (folderScene != sceneName)
            {
                folderScene = sceneName;
                folderName = ExportOptions.DefaultFolderName(sceneName);
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            GUILayout.Label("Account", EditorStyles.boldLabel);
            string server = EditorGUILayout.TextField("Server", client.Server);
            if (server != client.Server)
            {
                client.Server = server.Trim().TrimEnd('/');
                DarkridePrefs.Server = client.Server;
            }
            if (client.LoggedIn)
            {
                EditorGUILayout.LabelField("Logged in as", string.IsNullOrEmpty(client.Username) ? "(unknown)" : client.Username);
                if (GUILayout.Button("Log out"))
                {
                    DarkridePrefs.LogOut();
                    client = DarkridePrefs.Client();
                    status = "Logged out.";
                }
            }
            else
            {
                client.Username = EditorGUILayout.TextField("Username or email", client.Username);
                password = EditorGUILayout.PasswordField("Password", password);
                if (GUILayout.Button("Log in")) LogIn();
            }
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);

            GUILayout.Space(10);
            GUILayout.Label("Export", EditorStyles.boldLabel);
            selectionOnly = EditorGUILayout.Toggle("Selection only", selectionOnly);
            EditorGUILayout.LabelField("Scene", sceneName + (selectionOnly ? " (" + Selection.gameObjects.Length + " selected objects)" : ""));
            folderName = EditorGUILayout.TextField("Folder in My Files", folderName);

            EditorGUILayout.BeginHorizontal();
            string outputRoot = DarkridePrefs.OutputRoot;
            if (string.IsNullOrEmpty(outputRoot)) outputRoot = ExportOptions.DefaultOutputRoot();
            string edited = EditorGUILayout.TextField("Write to", outputRoot);
            if (GUILayout.Button("…", GUILayout.Width(28)))
            {
                string chosen = EditorUtility.OpenFolderPanel("Darkride exports folder", outputRoot, "");
                if (!string.IsNullOrEmpty(chosen)) edited = chosen;
            }
            if (edited != outputRoot) DarkridePrefs.OutputRoot = edited;
            EditorGUILayout.EndHorizontal();

            DarkridePrefs.Upload = EditorGUILayout.Toggle(new GUIContent("Upload to My Files", "Off: only the local folder is written, with file: links"), DarkridePrefs.Upload);
            DarkridePrefs.KeepHierarchy = EditorGUILayout.Toggle(new GUIContent("Keep hierarchy", "Parents become invisible Empty entities so a prefab instance moves as one"), DarkridePrefs.KeepHierarchy);
            DarkridePrefs.ExportLightmaps = EditorGUILayout.Toggle("Lightmaps", DarkridePrefs.ExportLightmaps);
            DarkridePrefs.ExportReflectionProbe = EditorGUILayout.Toggle(new GUIContent("Reflection probe", "The largest baked reflection probe becomes the zone's ambient map"), DarkridePrefs.ExportReflectionProbe);
            DarkridePrefs.ExportZone = EditorGUILayout.Toggle(new GUIContent("Zone", "A Zone entity over the scene: key light, ambient map, sky colour"), DarkridePrefs.ExportZone);
            DarkridePrefs.ExportLights = EditorGUILayout.Toggle(new GUIContent("Mixed and Realtime lights", "Baked lights are in the lightmaps and are only listed"), DarkridePrefs.ExportLights);
            using (new EditorGUI.DisabledScope(true))
            {
                DarkridePrefs.ProbeSpacing = EditorGUILayout.FloatField(new GUIContent("Probe volume spacing (m)", "The light probe grid: not written yet (step 4)"), DarkridePrefs.ProbeSpacing);
            }

            GUILayout.Space(10);
            using (new EditorGUI.DisabledScope(DarkridePrefs.Upload && !client.LoggedIn))
            {
                if (GUILayout.Button(selectionOnly ? "Export Selection" : "Export Scene", GUILayout.Height(32))) Export();
            }
            if (DarkridePrefs.Upload && !client.LoggedIn) EditorGUILayout.HelpBox("Log in to upload, or turn Upload off to write the folder locally.", MessageType.Warning);

            if (!string.IsNullOrEmpty(lastResult))
            {
                GUILayout.Space(6);
                EditorGUILayout.HelpBox(lastResult, MessageType.None);
                if (!string.IsNullOrEmpty(lastLocalPath) && GUILayout.Button("Show the folder")) EditorUtility.RevealInFinder(lastLocalPath);
            }
            EditorGUILayout.EndScrollView();
        }

        void LogIn()
        {
            try
            {
                EditorUtility.DisplayProgressBar("Darkride", "Logging in to " + client.Server, 0.5f);
                client.Login(client.Username.Trim(), password);
                DarkridePrefs.Save(client);
                password = "";
                status = "Logged in as " + client.Username + ".";
            }
            catch (Exception e)
            {
                status = "Login failed: " + e.Message;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        void Export()
        {
            var options = new ExportOptions
            {
                FolderName = folderName,
                OutputRoot = DarkridePrefs.OutputRoot,
                Upload = DarkridePrefs.Upload,
                KeepHierarchy = DarkridePrefs.KeepHierarchy,
                ExportLightmaps = DarkridePrefs.ExportLightmaps,
                ExportReflectionProbe = DarkridePrefs.ExportReflectionProbe,
                ExportZone = DarkridePrefs.ExportZone,
                ExportLights = DarkridePrefs.ExportLights,
                ProbeSpacing = DarkridePrefs.ProbeSpacing,
                Roots = selectionOnly ? Selection.gameObjects : null,
            };
            try
            {
                var result = SceneExport.Run(options, client, (step, fraction) => EditorUtility.DisplayProgressBar("Darkride Export", step, fraction));
                DarkridePrefs.Save(client);
                lastLocalPath = result.LocalPath;
                lastResult = result.Report.Text();
                Debug.Log("[darkride] export done\n" + lastResult);
                EditorUtility.DisplayDialog("Darkride Export", result.EntityCount + " entities written.\n\nscene.json: " + result.SceneUrl + "\n\n" + Summary(result.Report), "OK");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                lastResult = "Export failed: " + e.Message;
                EditorUtility.DisplayDialog("Darkride Export", "Export failed:\n" + e.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        static string Summary(Report report)
        {
            string text = report.Text();
            return text.Length > 1200 ? text.Substring(0, 1200) + "…\n(the whole report is in report.txt)" : text;
        }
    }
}
