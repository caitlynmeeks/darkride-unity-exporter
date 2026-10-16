// The two client scripts written into every export, filled in: import-scene.js (paste the scene, then look from
// Unity's camera) and remove-scene.js (take it out again). Running Scripts > from URL with the import script's link
// is all a visitor needs. The templates in darkride/ of this repository are the same scripts with the blanks open.

using System.Globalization;
using System.IO;
using UnityEngine;

namespace Darkride.Exporter
{
    public static class ScriptsExport
    {
        public static void Write(string localPath, string baseUrl, string folder, Camera camera, Vector3 zoneCentre)
        {
            string view = "null";
            float fov = 60;
            if (camera != null)
            {
                Vector3 p = Conversions.Point(camera.transform.position);
                Quaternion q = Conversions.Rotation(camera.transform.rotation) * Conversions.SpotFlip;    // a camera looks along -Z in darkride
                view = "{ position: " + Vec(p) + ", orientation: " + Quat(q) + " }";
                fov = camera.fieldOfView;
            }
            string import = Template.IMPORT
                .Replace("@SCENE_JSON@", baseUrl + "/scene.json")
                .Replace("@VIEW@", view)
                .Replace("@FOV@", F(fov))
                .Replace("@FOLDER@", folder);
            File.WriteAllText(Path.Combine(localPath, "import-scene.js"), import);
            string look = Template.VIEW
                .Replace("@VIEW@", view)
                .Replace("@FOV@", F(fov))
                .Replace("@FOLDER@", folder);
            File.WriteAllText(Path.Combine(localPath, "view-from-unity-camera.js"), look);
            string remove = Template.REMOVE
                .Replace("@FOLDER@", folder)
                .Replace("@CENTRE@", Vec(zoneCentre));
            File.WriteAllText(Path.Combine(localPath, "remove-scene.js"), remove);
        }

        static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
        static string Vec(Vector3 v) => "{ x: " + F(v.x) + ", y: " + F(v.y) + ", z: " + F(v.z) + " }";
        static string Quat(Quaternion q) => "{ x: " + F(q.x) + ", y: " + F(q.y) + ", z: " + F(q.z) + ", w: " + F(q.w) + " }";

        static class Template
        {
            public const string IMPORT = @"//
//  import-scene.js (written by the Darkride Exporter for @FOLDER@)
//
//  Brings this export into the world you stand in: the engine's clipboard reads scene.json and pastes its entities
//  with new ids, parents kept, every position moved by AT (where Unity's origin lands; {0, 0, 0} keeps Unity's
//  coordinates). Then, when LOOK_FROM_UNITY_CAMERA is true, your camera goes to Unity's camera (Camera.mode
//  ""independent""; press Escape or pick a camera mode to get it back; Unity's vertical field of view was @FOV@
//  degrees, Settings > General > Field of view matches it). Load once from Running Scripts (from URL or from disk);
//  it stops itself. remove-scene.js takes the import out again.
//

(function () {
    var SCENE_JSON = ""@SCENE_JSON@"";
    var AT = { x: 0, y: 0, z: 0 };
    var LOOK_FROM_UNITY_CAMERA = true;
    var VIEW = @VIEW@;

    if (!Clipboard.importEntities(SCENE_JSON)) {
        print(""darkride import: could not read "" + SCENE_JSON);
        Script.stop();
        return;
    }
    var ids = Clipboard.pasteEntities(AT);
    print(""darkride import: "" + ids.length + "" entities from "" + SCENE_JSON + "" at "" + JSON.stringify(AT));
    if (LOOK_FROM_UNITY_CAMERA && VIEW) {
        Camera.mode = ""independent"";
        Camera.position = Vec3.sum(VIEW.position, AT);
        Camera.orientation = VIEW.orientation;
        print(""darkride import: the camera is at Unity's camera (independent mode; Escape or a camera mode gives it back)"");
    }
    Script.stop();
}());
";

            public const string VIEW = @"//
//  view-from-unity-camera.js (written by the Darkride Exporter for @FOLDER@)
//
//  Puts your camera where Unity's camera is, for the side by side with unity-view.png (Unity's vertical field of
//  view was @FOV@ degrees; Settings > General > Field of view matches it). The editor's build mode drives the
//  camera itself, so leave build mode first; Escape or a camera mode gives the camera back. AT is the offset
//  import-scene.js pasted the scene with ({0, 0, 0} unless you changed it).
//

(function () {
    var AT = { x: 0, y: 0, z: 0 };
    var VIEW = @VIEW@;

    if (!VIEW) {
        print(""darkride view: the scene had no camera"");
        Script.stop();
        return;
    }
    Camera.mode = ""independent"";
    Camera.position = Vec3.sum(VIEW.position, AT);
    Camera.orientation = VIEW.orientation;
    print(""darkride view: the camera is at Unity's camera, "" + JSON.stringify(Camera.position));
    Script.stop();
}());
";

            public const string REMOVE = @"//
//  remove-scene.js (written by the Darkride Exporter for @FOLDER@)
//
//  Removes the import: every entity whose userData ""export"" is the folder name. A client only knows the entities
//  the server has sent it, those in view, so run this standing at the scene; CENTRE and RADIUS bound the search.
//

(function () {
    var FOLDER = ""@FOLDER@"";
    var CENTRE = @CENTRE@;
    var RADIUS = 500;

    var found = Entities.findEntities(CENTRE, RADIUS);
    var removed = 0;
    for (var i = 0; i < found.length; i++) {
        var userData = Entities.getEntityProperties(found[i], [""userData""]).userData;
        if (!userData) {
            continue;
        }
        try {
            if (JSON.parse(userData)[""export""] === FOLDER) {
                Entities.deleteEntity(found[i]);
                removed++;
            }
        } catch (e) {
            // not JSON: not ours
        }
    }
    print(""darkride remove: "" + removed + "" entities of "" + FOLDER + "" removed ("" + found.length + "" looked at)"");
    Script.stop();
}());
";
        }
    }
}
