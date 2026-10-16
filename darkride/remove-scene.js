//
//  remove-scene.js
//
//  Removes an import made by import-scene.js: every entity whose userData "export" is FOLDER (the exporter writes the
//  folder name into every entity). A client only knows the entities the server has sent it, those in view, so run
//  this standing at the scene; CENTRE and RADIUS bound the search.
//

(function () {
    var FOLDER = "Rainbow_Room-2026-10-16";
    var CENTRE = { x: 0, y: 0, z: 0 };
    var RADIUS = 500;

    var found = Entities.findEntities(CENTRE, RADIUS);
    var removed = 0;
    for (var i = 0; i < found.length; i++) {
        var userData = Entities.getEntityProperties(found[i], ["userData"]).userData;
        if (!userData) {
            continue;
        }
        try {
            if (JSON.parse(userData).export === FOLDER) {
                Entities.deleteEntity(found[i]);
                removed++;
            }
        } catch (e) {
            // not JSON: not ours
        }
    }
    print("darkride remove: " + removed + " entities of " + FOLDER + " removed (" + found.length + " looked at)");
    Script.stop();
}());
