// The metaverse's HTTP API as the exporter uses it: the OAuth password grant (POST /oauth/token), the user's profile
// (GET /api/v1/user/profile), the user's files base URL (GET /api/user/files), folders (PUT /api/user/files/folder)
// and uploads (PUT /api/user/files, multipart "path" then "file"; an upload to an existing path keeps the file's id,
// so a re-export into the same folder updates every world using it). Requests block the editor; the window shows a
// progress bar meanwhile.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace Darkride.Exporter
{
    public class MetaverseClient
    {
        public string Server;          // https://vr.darkride.ai
        public string AccessToken;
        public string RefreshToken;
        public string Username;

        public class ApiException : Exception
        {
            public long Code;
            public ApiException(string message, long code) : base(message) { Code = code; }
        }

        [Serializable] class TokenResponse { public string access_token; public string refresh_token; public string token_type; public long expires_in; }
        [Serializable] class FilesResponse { public string url; }
        [Serializable] class ProfileResponse { public string status; public ProfileData data; }
        [Serializable] class ProfileData { public ProfileUser user; }
        [Serializable] class ProfileUser { public string username; }
        [Serializable] public class UploadResponse { public string id; public string url; public string pathUrl; public long size; }

        public bool LoggedIn => !string.IsNullOrEmpty(AccessToken);

        public void Login(string username, string password)
        {
            var token = Grant(new Dictionary<string, string>
            {
                { "grant_type", "password" },
                { "username", username },
                { "password", password },
                { "scope", "owner" },
            });
            AccessToken = token.access_token;
            RefreshToken = token.refresh_token;
            Username = FetchUsername();
        }

        public void Refresh()
        {
            if (string.IsNullOrEmpty(RefreshToken)) throw new ApiException("Not logged in", 401);
            var token = Grant(new Dictionary<string, string>
            {
                { "grant_type", "refresh_token" },
                { "refresh_token", RefreshToken },
            });
            AccessToken = token.access_token;
            if (!string.IsNullOrEmpty(token.refresh_token)) RefreshToken = token.refresh_token;
        }

        TokenResponse Grant(Dictionary<string, string> fields)
        {
            var form = new WWWForm();
            foreach (var kv in fields) form.AddField(kv.Key, kv.Value);
            string body = Send(() => UnityWebRequest.Post(Server + "/oauth/token", form), false);
            var token = JsonUtility.FromJson<TokenResponse>(body);
            if (token == null || string.IsNullOrEmpty(token.access_token))
                throw new ApiException("The server sent no access token: " + body, 0);
            return token;
        }

        public string FetchUsername()
        {
            string body = Send(() => UnityWebRequest.Get(Server + "/api/v1/user/profile"), true);
            var profile = JsonUtility.FromJson<ProfileResponse>(body);
            if (profile == null || profile.data == null || profile.data.user == null || string.IsNullOrEmpty(profile.data.user.username))
                throw new ApiException("The profile reply has no username: " + body, 0);
            Username = profile.data.user.username;
            return Username;
        }

        // The user's files base URL, e.g. https://files.vr.darkride.ai/caitlyn
        public string FilesBase()
        {
            string body = Send(() => UnityWebRequest.Get(Server + "/api/user/files"), true);
            var files = JsonUtility.FromJson<FilesResponse>(body);
            if (files == null || string.IsNullOrEmpty(files.url))
                throw new ApiException("The files reply has no base URL: " + body, 0);
            return files.url.TrimEnd('/');
        }

        public void CreateFolder(string path)
        {
            Send(() =>
            {
                var request = UnityWebRequest.Put(Server + "/api/user/files/folder?path=" + Uri.EscapeDataString(path), new byte[0]);
                return request;
            }, true);
        }

        public UploadResponse Upload(string remotePath, string localFile)
        {
            byte[] bytes = File.ReadAllBytes(localFile);
            string fileName = Path.GetFileName(localFile);
            string contentType = ContentType(fileName);
            string body = Send(() =>
            {
                var sections = new List<IMultipartFormSection>
                {
                    new MultipartFormDataSection("path", remotePath),
                    new MultipartFormFileSection("file", bytes, fileName, contentType),
                };
                var request = UnityWebRequest.Post(Server + "/api/user/files", sections);
                request.method = "PUT";
                return request;
            }, true);
            return JsonUtility.FromJson<UploadResponse>(body);
        }

        static string ContentType(string fileName)
        {
            switch (Path.GetExtension(fileName).ToLowerInvariant())
            {
                case ".gltf": return "model/gltf+json";
                case ".glb": return "model/gltf-binary";
                case ".bin": return "application/octet-stream";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".exr": return "image/x-exr";
                case ".json": return "application/json";
                case ".txt": return "text/plain";
                default: return "application/octet-stream";
            }
        }

        // Sends a request, refreshing the access token once on a 401.
        string Send(Func<UnityWebRequest> make, bool authenticated)
        {
            for (int attempt = 0; ; attempt++)
            {
                using (var request = make())
                {
                    if (authenticated)
                    {
                        if (string.IsNullOrEmpty(AccessToken)) throw new ApiException("Not logged in", 401);
                        request.SetRequestHeader("Authorization", "Bearer " + AccessToken);
                    }
                    request.timeout = 900;
                    var operation = request.SendWebRequest();
                    while (!operation.isDone) Thread.Sleep(10);
                    string text = request.downloadHandler != null ? request.downloadHandler.text : "";
                    if (request.result == UnityWebRequest.Result.Success) return text;
                    if (authenticated && request.responseCode == 401 && attempt == 0 && !string.IsNullOrEmpty(RefreshToken))
                    {
                        Refresh();
                        continue;
                    }
                    throw new ApiException(request.error + (string.IsNullOrEmpty(text) ? "" : ": " + Truncate(text, 300)), request.responseCode);
                }
            }
        }

        static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
