# Darkride Exporter for Unity

A Unity editor package that exports a scene to [darkride](https://vr.darkride.ai): one glTF per mesh with its
materials and textures, the baked lightmaps and direction maps, the reflection probe as the zone's ambient map,
Mixed and Realtime lights, and `scene.json` in the engine's entity format, written to a folder and uploaded into
My Files. The design is in darkride-ops `LIGHTMAPS.md` (section 3 and step 3).

Unity 6000.x, URP. Materials should be URP/Lit (UnityGLTF converts them to glTF PBR).

## Install

Window > Package Manager > + > *Install package from git URL…*, in this order:

1. `https://github.com/KhronosGroup/UnityGLTF.git#release/2.23.0` (UnityGLTF, Khronos, MIT)
2. this package's git URL (or *Install package from disk…* and pick its `package.json`)

## Use

1. Bake the scene (Window > Rendering > Lighting > Generate Lighting) so the lightmaps are on disk.
2. **Darkride > Export Scene…** Log in with your darkride account (the server is `https://vr.darkride.ai`).
3. Check the folder name (`<scene>-<date>`; exporting into the same folder again updates the files, and every
   world using them) and the options, then **Export Scene**. The folder is also written locally
   (`<project>/DarkrideExports/<folder>` by default) with `report.txt`.
4. In darkride, Running Scripts > from URL with the `import-scene.js` link the exporter printed (the script is
   written into the export folder, filled in): the scene is pasted with Unity's coordinates and your camera goes
   to Unity's camera, for a side by side with `unity-view.png`. `remove-scene.js` in the same folder takes the
   import out again. (`darkride/` in this repository holds the same two scripts with the blanks open.)

**Darkride > Export Selection…** exports the selected objects and their children.

With *Upload* off the folder is written locally only and `scene.json` links to it with `file:` links, for a
darkride client on the same machine.

## What it writes

```
<folder>/
  scene.json            the entities: Model, Light, Shape (invisible parents), Zone
  report.txt            counts, what was skipped and why, warnings
  import-scene.js       pastes the scene in darkride (Running Scripts > from URL), remove-scene.js removes it,
                        view-from-unity-camera.js puts the camera where Unity's is (outside build mode)
  unity-view.png        Unity's render from the scene camera, for comparing
  models/<mesh>.gltf    one per distinct mesh and material set, + <mesh>.bin
  models/textures/      every texture once, PNG or JPEG copied from the source where possible
  lightmaps/Lightmap-N.exr       half RGB, ZIP (from Unity's float EXR)
  lightmaps/Lightmap-N_dir.png   the direction map, x mirrored
  probes/ReflectionProbe.exr     the largest baked probe as a 1:6 vertical strip (the zone's ambientURL)
```

- A MeshRenderer becomes a **Model** entity: `modelURL`, position (the centre of the mesh bounds), rotation,
  `dimensions` (bounds × scale), `shapeType` from its collider (MeshCollider → static-mesh, Box → box, Sphere →
  sphere, Capsule → capsule-y, none → none and collisionless), `userData.lightmap` = `{ url, scaleOffset,
  directionalUrl, mode: "indirect" }` when it is lightmapped.
- Parents of exported things become **Empty** entities (an invisible, collisionless 0.2 m cube, as the editor's
  Create > Empty) so a prefab instance moves as one; children use `parentID` with `position` and `rotation` relative to the parent. *Keep
  hierarchy* off exports everything flat.
- **Baked** lights are in the lightmaps and are only listed in the report. **Mixed** and **Realtime** point and
  spot lights become Light entities (intensity as in Unity, not yet calibrated); a Mixed or Realtime directional
  light becomes the zone's key light with shadows.
- The **Zone** covers the scene bounds plus 2 m: key light off for a baked-only scene, `ambientURL` the
  reflection probe strip with the probe's intensity, the sky a flat colour from the environment lighting.
- Axes: Unity is left-handed, glTF and darkride right-handed; X is mirrored for meshes, positions, rotations,
  light directions and the direction maps (UnityGLTF's convention).

## Batch mode

```
Unity -batchmode -projectPath <project> -executeMethod Darkride.Exporter.DarkrideCli.ExportScene \
      -darkrideScene Assets/Scenes/Room.unity -darkrideOut /path/to/exports [-darkrideFolder room-test] \
      [-darkrideUpload] [-darkrideFlat] -logFile export.log
```

Not `-nographics`: the reflection probe is resampled on the GPU. `-darkrideUpload` uses the login saved by the
window.

## Not yet

Skinned meshes, Unity Terrain, particles, UI, skybox materials, several reflection probes (box projection), the
light probe volume (step 4 of LIGHTMAPS.md; the spacing option is already in the window), light intensity
calibration, single-prefab export with the lightmap inside the material.
