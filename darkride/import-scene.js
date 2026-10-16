//
//  import-scene.js
//
//  Brings a Darkride export (scene.json from the Unity exporter) into the world you stand in: the engine's clipboard
//  reads the file and pastes its entities with new ids, parents kept, every position moved by AT (so AT is where
//  Unity's origin lands; {0, 0, 0} keeps Unity's coordinates). Set SCENE_JSON to the export's scene.json link, the
//  one the exporter printed, then load this script once from Running Scripts (from disk or from URL); it stops itself.
//  remove-scene.js takes an import out again.
//

(function () {
    var SCENE_JSON = "https://files.vr.darkride.ai/caitlyn/Rainbow_Room-2026-10-16/scene.json";
    var AT = { x: 0, y: 0, z: 0 };

    if (!Clipboard.importEntities(SCENE_JSON)) {
        print("darkride import: could not read " + SCENE_JSON);
        Script.stop();
        return;
    }
    var ids = Clipboard.pasteEntities(AT);
    print("darkride import: " + ids.length + " entities from " + SCENE_JSON + " at " + JSON.stringify(AT));
    Script.stop();
}());
