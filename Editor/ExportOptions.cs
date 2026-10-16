// What one export does, and the settings kept between sessions (EditorPrefs; the tokens are stored in plain text,
// as Unity's own cloud login is, so log out on a shared machine).

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Darkride.Exporter
{
    public class ExportOptions
    {
        public string FolderName;              // the folder in My Files and on disk: <scene>-<yyyy-MM-dd>
        public string OutputRoot;              // where the folder is written: <project>/DarkrideExports
        public bool Upload = true;             // upload into My Files and write the files' URLs into scene.json
        public bool KeepHierarchy = true;      // parents become Empty entities, children follow them
        public bool ExportLightmaps = true;
        public bool ExportReflectionProbe = true;
        public bool ExportZone = true;
        public bool ExportLights = true;       // Mixed and Realtime lights as Light entities
        public GameObject[] Roots;             // null: the whole scene; else these objects and their children
        public float ProbeSpacing = 1.0f;      // the probe volume's grid spacing (step 4, not written yet)

        public static string DefaultOutputRoot()
        {
            string project = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(project, "DarkrideExports");
        }

        public static string DefaultFolderName(string sceneName)
        {
            string name = string.IsNullOrEmpty(sceneName) ? "scene" : sceneName;
            return SafeName(name) + "-" + DateTime.Now.ToString("yyyy-MM-dd");
        }

        // A file or folder name the files host and every file system take: letters, digits, '-', '_' and '.'
        public static string SafeName(string name)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) && c < 128) sb.Append(c);
                else if (c == '-' || c == '_' || c == '.') sb.Append(c);
                else if (c == ' ') sb.Append('_');
            }
            string s = sb.ToString().Trim('.');
            return s.Length == 0 ? "item" : s;
        }
    }

    public static class DarkridePrefs
    {
        const string PREFIX = "Darkride.Exporter.";

        public static string Server { get => EditorPrefs.GetString(PREFIX + "Server", "https://vr.darkride.ai"); set => EditorPrefs.SetString(PREFIX + "Server", value); }
        public static string Username { get => EditorPrefs.GetString(PREFIX + "Username", ""); set => EditorPrefs.SetString(PREFIX + "Username", value); }
        public static string AccessToken { get => EditorPrefs.GetString(PREFIX + "AccessToken", ""); set => EditorPrefs.SetString(PREFIX + "AccessToken", value); }
        public static string RefreshToken { get => EditorPrefs.GetString(PREFIX + "RefreshToken", ""); set => EditorPrefs.SetString(PREFIX + "RefreshToken", value); }
        public static string OutputRoot { get => EditorPrefs.GetString(PREFIX + "OutputRoot", ""); set => EditorPrefs.SetString(PREFIX + "OutputRoot", value); }
        public static bool Upload { get => EditorPrefs.GetBool(PREFIX + "Upload", true); set => EditorPrefs.SetBool(PREFIX + "Upload", value); }
        public static bool KeepHierarchy { get => EditorPrefs.GetBool(PREFIX + "KeepHierarchy", true); set => EditorPrefs.SetBool(PREFIX + "KeepHierarchy", value); }
        public static bool ExportLightmaps { get => EditorPrefs.GetBool(PREFIX + "ExportLightmaps", true); set => EditorPrefs.SetBool(PREFIX + "ExportLightmaps", value); }
        public static bool ExportReflectionProbe { get => EditorPrefs.GetBool(PREFIX + "ExportReflectionProbe", true); set => EditorPrefs.SetBool(PREFIX + "ExportReflectionProbe", value); }
        public static bool ExportZone { get => EditorPrefs.GetBool(PREFIX + "ExportZone", true); set => EditorPrefs.SetBool(PREFIX + "ExportZone", value); }
        public static bool ExportLights { get => EditorPrefs.GetBool(PREFIX + "ExportLights", true); set => EditorPrefs.SetBool(PREFIX + "ExportLights", value); }
        public static float ProbeSpacing { get => EditorPrefs.GetFloat(PREFIX + "ProbeSpacing", 1.0f); set => EditorPrefs.SetFloat(PREFIX + "ProbeSpacing", value); }

        public static MetaverseClient Client()
        {
            return new MetaverseClient { Server = Server.TrimEnd('/'), Username = Username, AccessToken = AccessToken, RefreshToken = RefreshToken };
        }

        public static void Save(MetaverseClient client)
        {
            Server = client.Server;
            Username = client.Username ?? "";
            AccessToken = client.AccessToken ?? "";
            RefreshToken = client.RefreshToken ?? "";
        }

        public static void LogOut()
        {
            AccessToken = "";
            RefreshToken = "";
        }
    }
}
