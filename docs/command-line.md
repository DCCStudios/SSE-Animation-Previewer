# Command line

[Back to the README](../README.md)

The exe takes switches for scripted runs. Most exist to test the program itself. The first few below are useful to anyone.

## Background mode

Every run started with one of the tool or test switches on this page works in the background without being asked: off-screen, out of the taskbar and never as the foreground window. A script therefore cannot take the keyboard from whoever is using the machine. It renders with the same code as a normal run.

- `--background`, or the environment variable `SSE_PREVIEW_BACKGROUND=1`, asks for the same thing explicitly. Older scripts that pass it keep working.
- A background run refuses every activation of its own windows. OpenTK's GL window becomes a child window before OpenTK parents it into the form; the old order (parent first, child style after) activated it. The run also holds the system foreground lock for that fraction of a second. Before 2026-11-03 every background start took the foreground for about two seconds, pulling focus from a running game. Each run appends one line to `%TEMP%\sse-previewer-focus-guard.log`.
- `--show` keeps a tool run on screen, for watching what a check does.
- A background run never shows a dialog. A scene that references a missing file or folder skips that reference, as **No** would, and the run writes a line naming it to standard error and `previewer.log`.
- `--no-activate`, or `SSE_PREVIEW_NO_ACTIVATE=1`, is for a normal start. The window opens on screen as usual but leaves the focus where it was, for example `"SSE Animation Previewer.exe" --no-activate --data <Data folder>`.

`--data <folder>` mounts a Data folder at start.

`scripts	est-focus.ps1` checks all of this without disturbing the desktop in use: it starts the exe on a separate hidden desktop and reads the log the previewer writes when `SSE_PREVIEW_FOCUS_LOG=<file>` is set, which records when the window is shown and every time it is activated.

## Reading clips from a script

```
"SSE Animation Previewer.exe" --background --dump-annotations <clip.hkx> [more clips]
```

Writes each clip's duration and annotations, with times and track numbers, to `annotations.txt` beside the exe.

```
"SSE Animation Previewer.exe" --background --track-bone <bone> <reference bone> <clip.hkx> [more clips]
```

Writes `track-bone.txt` beside the exe, a log of how a bone moves relative to another across each clip. It takes the skeleton from your last session, so run the previewer once and mount your data first, or point the `SSE_SESSION_FILE` environment variable at a session file. It answers questions such as "is `WEAPON` animated separately from the right hand in this export".

```
"SSE Animation Previewer.exe" --playback-probe <clip path suffix> [seconds]
```

Loads your last session, plays one clip for a few seconds (4 by default) the way the window does, and writes `playback-probe\playback-probe.txt` beside the exe: the race and skeleton the clip bound to, frames per second, the gaps between presented frames, scene draw time and the cost of each part of a playback tick. A capture of the last frame goes to `after.png`. If no clip path ends with the suffix, `clips.txt` lists every clip path. Use it when playback feels slow or a clip binds to the wrong skeleton.

## View captures

```
"SSE Animation Previewer.exe" --view-capture <data folder> <out.png> <nif or manifest.json> [eye=x,y,z] [yaw=deg] [pitch=deg] [fov=deg] [size=WxH] [colors=<shapes.png>] [timeout=s]
```

Renders a NIF, or several placed NIFs, from a camera standing at a point, in Skyrim's units and axes, so materials can be checked from where a player would stand. It mounts the data folder, turns on True PBR materials and the Skyrim shader, and writes the PNG only after every texture has loaded.

- `eye` is the camera position in game units, Z up. Default `0,0,0`.
- `yaw` is the game's angle z in degrees: 0 looks north (+Y), 90 east (+X). Default 0.
- `pitch` is the game's angle x in degrees: positive looks down. Default 0, limited to ±89.
- `fov` is the horizontal field of view in degrees. Default 75.
- `size` is the image size. Default `1600x900`.
- `colors=<path>` also writes a second PNG with every shape in a flat colour of its own, for finding seams and which shape a surface belongs to. Alpha-tested cut-outs stay cut out.
- `timeout` is how long to wait for textures, in seconds. Default 300.
- `maxtex` is the largest texture size uploaded, in pixels; larger textures use their first mip level that fits. Default 1024, `0` for full size. The graphics driver keeps a copy of every texture in system memory, and a level's textures add up to tens of GB at full size: one grasslands tile with 1,535 textures used 13.4 GB at 2048 and 9.1 GB at 1024.
- `maxmem` is how many GB of memory the run may use. Past it, the run stops with FAIL instead of exhausting the machine. Default 16, `0` for no limit.
- `static=0` turns off static geometry mode, below. Default 1.
- `time=<seconds>` is the moment the still is taken, for looping controllers such as waterfalls and for the water's wave scroll. Default 0.
- `weather=<WTHR EditorID>` draws that weather's sky, fog and light in place of the viewer's background and light rig. See the weather section below.
- `hour=<0-24>` is the time of day for `weather=`. Default 12.
- `climate=<CLMT EditorID>` supplies the sunrise and sunset times for `hour=`. Default: SkyrimClimate's, sunrise 5:30 to 10:00 and sunset 16:00 to 20:30.
- `fog=0`, `skystatics=0` and `weatherlight=0` leave out the weather's fog, its sky statics, or its light (the viewer's rig is used).
- `base=<Skyrim Data folder>` reads the `.bsa` archives in that folder underneath the mounted data folder, so NIFs that use vanilla textures (effect shaders such as `textures\effects\FXfluidTile01.dds`) draw as they do in game. A file in the mounted folder always wins over the same path in an archive. The folder needs the `Skyrim - Textures*.bsa` archives; some modlists' Stock Game folders leave them out.

Water surfaces (shapes with a `BSWaterShaderProperty`, such as `meshes\Water\Water2048.nif`) draw with Skyrim's water shader, rebuilt from the vanilla paths of Community Shaders' `Water.hlsl`. The look comes from the water type (WATR record):

- three noise normal layers scrolling with the type's wind direction and speed, at its UV scales and amplitudes
- fresnel, and a reflection traced against the drawn scene in screen space over the type's reflection colour
- refraction of what lies under the surface, tinted from the shallow to the deep colour with depth and fogged over the type's fog distances, so shallow water shows its bed
- the sun highlight, from the previewer's key light
- river flowmaps, when the type enables them and the manifest names a flowmap folder
- the NIF's own flags: depth, reflections, refractions, mesh UVs for the normals, and vertex alpha as depth (puddles and stream edges)

What differs from the game: the reflection only shows what is on screen (the game renders a mirrored sky and world), the sun is the previewer's key light rather than the weather's, and there is no distance fog, underwater view, rain or wading ripples. Wave and flow speeds use estimated time scales.

Water types are read from the plugins in the `base=` folder (Skyrim.esm, Update.esm and the DLC masters), then from the mounted folder; a later plugin replaces an earlier one's record with the same EditorID. A manifest placement names its type with `"water": "<EditorID>"`. Without one, or when the name is not found, `DefaultWater` is used and a scene line says so.

Waterfalls are lit meshes and effect meshes (the `FXWaterfall` kit) and draw through the normal paths. The water pass runs after opaque geometry and before blended effects, so foam and spray stay on top of the water.

`Water2048.nif` is 2048 units square and centred on its origin, so cell water at height `z` is a placement at the cell's centre with `"scale": 2`.

Shapes with no shader property are left out, as Skyrim draws none (a waterfall's `CurrentPlane` shapes, for example), and so are shapes named `EditorMarker`, the Creation Kit's placement box. The `.txt` file counts both.

With `weather=`, the capture reads the weather (WTHR) and climate (CLMT) records from the `base=` folder's plugins, then the mounted folder's, the same way as water types: a later plugin's record with the same EditorID wins. At `hour=` it blends the weather's four colour sets: night until sunrise begins, then night into sunrise and sunrise into day across the sunrise window, day until sunset begins, then day into sunset and sunset into night. It then draws:

- the sky: a gradient from the weather's lower-sky colour below the horizon, through its horizon colour, to its upper-sky colour overhead
- the sky statics the weather lists (TNAM), resolved through each plugin's master list to their STAT models, centred on the eye and tinted by the weather's sky-statics colour. Single-sided shapes lose their back faces as in game (front is counter-clockwise; SLSF2 Double_Sided draws both), and the `.txt` file gives each shape's count of triangles facing the eye. A single-sided static with none facing fails the capture, since the game would show nothing. Nothing writes over geometry: every mesh draws in front of them whatever the dome's size, and their textures load at full size, not capped by `maxtex`.
- distance fog over the opaque scene, with the game's formula: factor = min(max, pow(saturate((distance - near) / (far - near)), power)), from the fog-near colour to the fog-far colour by that factor, using the day or night fog distances as the hour blends them. Sky pixels are not fogged.
- the weather's light in place of the viewer's rig: its sunlight colour as the key light, from the sun's position by day and the moon's by night, with no fill or back light, and its directional ambient (DALC) up and down colours as the sky and ground ambient. The rig is put back when the capture ends.

What differs from the game:

- The gradient's shape is an estimate: the game draws a sky dome mesh with these colours.
- The sun's path (east, through south at up to 65 degrees, to west) and the moon's are estimates. The light never drops below 15 degrees, so it still casts shadows.
- There are no cloud layers, sun or moon discs, stars, aurora or precipitation.
- Water and blended meshes are not fogged.
- The weather's imagespaces are not applied. Weathers that rely on them, as Obsidian Weathers does, read darker and flatter than in game. `weatherlight=0` keeps the viewer's rig for checking materials under the weather's sky.
- Vanilla weathers list distant cloud meshes as sky statics. Their in-game placement is not reproduced: centred on the eye they surround the camera, so pass `skystatics=0` for those.

A weather or climate that is not found stops the capture. A sky static that does not resolve to a model, or whose model or texture is missing, fails it after the PNG is written, with each one listed under "sky failures".

The run fails when an effect shader's base or palette texture did not load. Without them an effect shader draws its base colour as an opaque sheet. The `.txt` file lists each missing texture under "effect-shader textures that did not load", and the FAIL line names `base=` when it was not given.

Static geometry mode is how a view capture fits a large level in memory. Unskinned meshes are loaded 256 shapes at a time: each chunk is decoded, uploaded to the GPU with packed normals and colours, and then its CPU-side copies (the engine's vertex arrays and the NIF's own vertex list) are dropped before the next chunk. Textures load in batches of 32, and the memory they leave behind is collected as they go. On the grasslands level, 26 baked tiles (57 million vertices, 1,535 textures each) went from over 60 GB to a 20 GB peak at `maxtex=1024`. The image matches a normal load except for packed normals, which moved some pixels by up to 3 levels of 255.

The third argument is a NIF path under the data folder, drawn at the origin, or a JSON manifest that places several:

```json
{ "meshes": [ { "nif": "meshes\\level\\rock.nif", "position": [x, y, z], "angles": [x, y, z], "scale": 1 } ] }
```

For water, a placement may add `"water": "RiverWaterFlow"`, and the manifest may add `"flowmaps": "skyrim.esm"`: the folder under `textures\water` with the worldspace's per-cell flowmaps (`flow.<x>.<y>.dds`).

`angles` are a reference's rotation angles in radians, applied as NiMatrix3::SetEulerAnglesXYZ does (world = Rx Ry Rz local), the same values a placed reference stores. `<out>.txt` beside the PNG records the camera, the shape count, the texture cap and peak memory, any shape whose diffuse texture did not load, the water drawn (surface count, the types used and the plugins they came from, NIF water flags, flowmaps and the surfaces' extent), with `weather=` the weather, climate, hour and colour-set mix, the sky and fog colours (0-255), the fog distances, the light and each sky static with its model, and the scene's diagnostics. The exit code is 0 only for a complete render, 1 when anything failed, and 2 for too few arguments. A failed run writes no PNG, except when the failure is a missing effect-shader texture or sky static: that PNG is written so the gap can be seen.

## Documentation screenshots

```
"SSE Animation Previewer.exe" --background --docs-capture <output folder> <Skyrim Data folder> <clip.hkx> [more clips]
```

Renders most of the pictures used in these pages: a Nord with an iron battleaxe, the overlays, the queue and a vanilla effect. The clips you pass decide the poses.

## Capturing an effect NIF

```
"SSE Animation Previewer.exe" --background --effect-capture <output folder> <nif> <nif> <Data folder>
```

Renders two effect NIFs (the same file twice is fine) with the textures in the Data folder, and writes `original_sample.png`, `original_wrapped.png`, `corrected_sample.png`, `corrected_wrapped.png` and `validation.txt` to the output folder. It exits with code 1 when an effect shows nothing or its clock does not loop, and with code 3 when the NIF or its textures were still loading when a frame was due (nothing is saved for that frame, so the caller can retry). Before each image it waits until the engine has uploaded every texture, up to `SSE_EFFECT_CAPTURE_READY_TIMEOUT` seconds (default 180), so several captures can run at once without saving the "Loading..." card. Other tools use it to build preview sheets. For a run of frames, use `--effect-frames` (below).

Environment variables set the shot: `SSE_EFFECT_CAPTURE_TIME` (seconds into the effect, default 0.934), `SSE_EFFECT_CAPTURE_DISTANCE` (camera distance, default 350), `SSE_EFFECT_CAPTURE_FOCUS_ORIGIN=1` (aim at the point 100 units above the origin) and `SSE_EFFECT_CAPTURE_AUTOMOUNT=1` (mount the folder above the NIF's `meshes` folder in place of the Data argument). More settings, all `SSE_EFFECT_CAPTURE_` plus:

- `PBR=1` draws True PBR shapes with the Community Shaders response (below). Without it they draw as the vanilla shader reads their slots.
- `BUDGET=<particles>` sets the preview particle budget (the capture does not read the session file, so it is otherwise the default).
- `BASE=<Skyrim Data folder>` reads that folder's `.bsa` archives underneath the mounted folder, as `base=` does for view captures.
- `ENVMAP=<cubemap game path>` is what True PBR surfaces reflect, for example `textures\cubemaps\quicksky_e.dds` (a vanilla cubemap needs `BASE`). `ENV_INTENSITY` scales it. Without it they reflect the ambient sky and ground colours.
- `BACKGROUND=<colour name>` paints a flat background, for example `MidnightBlue`. Any .NET colour name works.
- `YAW` and `PITCH` turn the orbit camera, in degrees (default 45 and 65).
- `EXTRA=<nif>@<seconds>;<nif>@<seconds>` spawns more NIFs with the first, each at its own start offset, the way a spell spawns its impact, extra explosion and tell.
- `BLOOM=<strength>` adds a bloom to the saved images (0.5 to 1 is a useful range). It works on the 8-bit image, so light that clipped to white carries less energy than the game's HDR bloom gives it.

`validation.txt` lists each True PBR shape with the features it asks for (two-layer, interlayer parallax, coat values, subsurface colour and opacity) and its feature slots. A clock outside 0.1 to 60 s fails the capture. The live-loop check fails when a single NIF of 1 to 4 s stops at its end instead of coming round to the start, and otherwise logs a warning.

True PBR shapes draw what Community Shaders' True PBR draws, read from its source (`TruePBR.cpp`, `Lighting.hlsl`, `PBR.hlsli`):

- the base layer: GGX specular, roughness, metal, AO and specular level from the RMAOS map, the slot-2 emissive, and the indirect lobes
- two-layer coats (flags 2 bit 24): the coat lobe with its roughness and F0 over the inner layer, which it attenuates by its Fresnel; a coloured coat (bit 30) and a coat normal (bit 27, slot 6)
- interlayer parallax (bits 24 and 25): the view and the lights refract through the coat normal, and the inner layer's UVs move along the refracted ray over the slot-3 height map (CS parallax with its fixed scale and offset; a 32-step march in place of CS's adaptive step count)
- subsurface (bit 26, single layer): forward and back scatter from the subsurface colour and opacity, and its share of the ambient
- colour space as CS uses it with Linear Lighting off: CS uses PBR textures as stored and lights those gamma values, which this linear renderer reproduces by decoding the albedo, emissive and feature colours from sRGB. Textures that store linear values in a UNORM format therefore show dark here, as they do in that game.

Not drawn: fuzz, glint, hair, thin surfaces, dynamic cubemaps of the scene (`ENVMAP` stands in), screen-space reflections, and CS's wetness.

## Effect frame sequences and GIFs

```
"SSE Animation Previewer.exe" --effect-frames <jobs file>
```

Renders effect NIFs as numbered frames (`f0000.png` and up), and optionally a looping GIF, from a JSON jobs file, in one process. The folder is mounted once for every job, without plugin records or the animation library. Frames are taken in time order, so the simulation steps forward from one frame to the next instead of starting over. Before the first frame of each job it waits until the NIF and all its textures are on the GPU; if a later frame finds a texture still loading, it waits and takes that frame again.

```json
{
  "Data": "C:/mods/My Spell Mod",
  "Background": "Black", "Pbr": true, "Budget": 8000,
  "Floor": false, "FarPlane": 40000,
  "Jobs": [
    {"Nif": "C:/mods/My Spell Mod/meshes/effects/MeteorStop.nif", "Out": "C:/shots/stop",
     "Start": 0, "Fps": 30, "Count": 90, "Distance": 3000, "FocusOrigin": true,
     "Width": 640, "Height": 360, "Gif": "C:/shots/stop.gif"}
  ]
}
```

Top-level settings apply to every job:

- `Data` is the folder to mount (default: the folder above the first NIF's `meshes` folder). `Base` adds a Skyrim Data folder's archives underneath it.
- `Pbr`, `Budget`, `Envmap` and `EnvIntensity` work as `PBR`, `BUDGET`, `ENVMAP` and `ENV_INTENSITY` do for `--effect-capture`.
- `Background` is a flat .NET colour name. Without it the viewer's pale cyan gradient shows, and additive effects add their colour onto it.
- `Floor` (default false) draws the grid floor, `FloorSize` sets its width in units (default 6144), and `FarPlane` keeps the far clip plane at least that far, for effects thousands of units across.
- `Window` sets the window size (`"1500x950"` by default); the viewport is part of it.
- `ReadyTimeout` (seconds, default 180) is how long a job waits for its NIF and textures.
- `Ffmpeg` is the ffmpeg to use for GIFs (else `SSE_FFMPEG`, else `ffmpeg` on the path).

Each job:

- `Nif` (a file path) and `Out` (a folder; its old `f????.png` files are removed).
- `Times` (a list of clip times), or `Start`, `Fps` and `Count`. Frame files are numbered in the order given.
- `MaxClock` (default 60): the longest effect clock, in seconds, the job accepts. A clock outside 0.1 s to `MaxClock` fails the job, because it usually means a NIF's duration was misread. Raise it for a long sequence whose Extras run past 60 s.
- `Distance` (default 350), `Yaw` and `Pitch` (degrees), `Focus` (`[x, y, z]`) or `FocusOrigin` (the point 100 units above the origin).
- `Extra`: more NIFs spawned with the first, as `[{"Nif": "...", "Offset": 0.5}]`.
- `Bloom`, as for `--effect-capture`.
- `Width` and `Height`: the output size. The frame is cropped around its centre to that shape, then scaled. One alone keeps the viewport's shape.
- `Gif` and `GifFps` (default `Fps`, else 30): a looping GIF of the frames, encoded with palettegen `reserve_transparent=0`, paletteuse `diff_mode=none`, `-gifflags -transdiff`, and byte 12 of the file set to 0.

Each job writes `frames.txt` to its folder (its time, visible meshes and particles per frame, and any NIF diagnostics). The jobs file gets a `.log.txt` beside it with one line per job. The exit code is 0 when every job worked, 3 when a job timed out waiting for its NIF or textures (worth a retry), and 1 for any other failure.

## Recording frames for an animated picture

```
"SSE Animation Previewer.exe" --background --frames-capture <scene file> <output folder> <seconds> [options]
```

Loads a saved scene, plays it in real time and writes `frame_0000.png` and up, with the real frame rate on the first line of `frames.txt`. Sounds are muted. Options include `fps=15`, `clip=<part of a clip path>`, `queue=<part>|<part>`, `smp`, `colliders`, `precision`, `skeleton`, `follow`, `window` (the whole window, not the viewport alone), `rotate=<dx>,<dy>`, `zoom=<steps>`, `size=<width>x<height>` (the window keeps its 1150x750 minimum), `camera=game|look` and `cameraevents` (the animated camera, below), `notrails` (no Precision blade trails, below), `sky=day` (a clear daylight gradient behind the scene: blue overhead, pale horizon, sand below, no fog) and `nogrid` (no grid floor, for this capture only). `restart` replays the clip from 0 once the warm-up is over, which a GIF of a whole clip wants. `frames.txt` notes the clip time at the first and last frame and warns when it never changed. Turn the frames into a GIF with a tool such as ffmpeg.

### Animated camera

The camera list under CAMERA in the window, and `camera=` for frame captures, plays a clip's camera bone `Camera3rd [Cam3]`. It is off by default and is not saved with the session.

- **game** does what Skyrim 1.6.1170 does with the bone once a behavior sends StartAnimatedCamera with that payload: the bone's local transform is an offset on the normal camera, here the orbit or follow camera. The translation is added in the camera's own axes (x right, y forward, z up), and the rotation turns the camera. `Camera Control` local translate z becomes the FOV, read as the horizontal angle in degrees, when it lies between 5 and 170.
- **look** views along the bone's world transform (+Y forward, +Z up). That is where the FF7 Maya library bakes it, so it matches the Maya shot. The two modes show the same keys as the game plays them and as Maya placed them.
- **Camera annotations** (`cameraevents`) gates the camera by the clip's StartAnimatedCamera, StartAnimatedCameraDelta and EndAnimatedCamera annotations (the handler names AnimatedCameraStart, AnimatedCameraDeltaStart and AnimatedCameraEnd also count). Start applies at once, Delta ramps in over 1 s (an estimate), End stops it. Without it, the camera runs for the whole clip.

In game these events come from a behavior with the payload "Camera3rd [Cam3]". A clip annotation has no payload, and the game does nothing with it, so the annotation gate shows the timing a behavior would give. The game's transition tween into its animated camera state is not modelled. `frames.txt` ends with a line on what the camera did.

### Precision blade trails

Precision 2.0.4 draws a trail behind every `Collision_Add` capsule on WEAPON or SHIELD unless the capsule says `NoTrail`. The previewer draws the same trail in the window ("Draw Precision blade trails" under PRECISION, on by default) and in frame captures. It picks the trail the way Precision does:

1. The capsule's own `TrailMeshOverride`, `TrailBaseColorOverride`, `TrailBaseColorScaleMult` and `TrailLifetimeMult`. A capsule with any of them uses Precision's defaults for the rest: the grey base colour (0.53, 0.53, 0.53, 1), the mesh's own colour scale and lifetime multiplier 1.
2. Else the first `[[TrailDefinition]]` in the mounted `SKSE\Plugins\Precision\*.toml` files whose conditions match, by priority. Only `WeaponNames` is supported; it is matched case-sensitively against the selected weapon's FULL name (from the Strings files for a localized plugin). A definition with any other condition never matches.
3. Else `Effects/WeaponTrails/AttackTrail.nif`.

Segment lifetime, fade time, segments per second, the colour scale multiplier and the default colour come from Precision's MCM ini in the mounted data (`MCM\Settings\Precision.ini`, else Precision's defaults). The strip is as long as the weapon mesh's reach, or the capsule when it says `TrailUseTrueLength` or nothing is in hand.

Precision steps its trail once per game frame. The previewer replays that at a fixed 60 fps from the capsule's start, so a scrubbed frame shows what the game shows at that clip time on a 60 fps machine. `frames.txt` gets one line per drawn trail per frame with the mesh, where it came from, rows alive, fade and colour. The line also says which way the texture runs (Precision's template has u 0 at the oldest row and 1 at the blade, v 0 at the tip and 1 at the base) and how the strip's vertices sit on TrailRoot's children, with a WARNING when the texture runs the other way or a bone carries no vertices.

Precision moves TrailRoot's children by index, so each vertex follows the bones it is skinned to, blended by weight. A mesh without usable skin data falls back to rows by height, and the line says so.

Not modelled: bone roll and any vertex offset off the blade axis (the strip is flat along the blade), the greyscale palette, falloff and soft depth of the trail shader, and TOML conditions other than `WeaponNames`.

## Built-in checks

These run a feature against fixtures and write a pass/fail log beside the exe. They expect the source tree's folder layout. `--ci-test` runs everything that needs only the files in the repository. Many of the others need a local `test-data` folder of vanilla assets or a folder of mods, and report `SKIPPED` when those are missing. See [Building](building.md).

| Switch | Covers |
|---|---|
| `--ci-test` | The three suites below in one run, with a summary in `ci-test.txt`. Exit code 0 only if all pass. |
| `--particle-test` | Particle simulation, effect resolution, speed maps and annotation activity (unit tests, no window) |
| `--catalog-test` | OAR and DAR library reading and the override lookup |
| `--smp-test` | HDT-SMP XML reading and solver setup |
| `--pbr-glow-smoke <data or mod folder> <output folder> <nif> [more nifs]` | Loads each True PBR NIF with the True PBR box on, checks that a shape with a coloured emissive colour has its slot-2 emissive map loaded, and captures it with emission on, emission off and the box off. Fails if emission blows the mesh out to white or changes nothing. NIF paths are relative to the folder. Writes PNGs and `pbr-glow-smoke.txt` to the output folder; a missing folder is reported as skipped. |
| `--queue-smoke <clips>` | Queue, drag feedback, root motion arrow, follow camera |
| `--precision-smoke <clips>` | Precision timeline rules and capsule drawing |
| `--annotation-smoke <clip>` | Active annotation highlight |
| `--bfco-smoke <three BFCO clips>` | BFCO notes, speed, highlight and queue hand-over |
| `--skeleton-smoke <clip>` | Bone overlay |
| `--override-smoke` | Library row colours and hover text. Run `--catalog-test` first, which writes its fixture. |
| `--flipbook-smoke <mod folder> <nifs>` | Sub-texture sheet sampling |
| `--camera-smoke`, `--floor-smoke` | Camera following and zoom, grid floor |

A run that fails exits with code 1 and names the failed check in its log. A switch given too few arguments exits with code 2 and says how many it needs. A switch the program does not know also exits with code 2, with a line on standard error and in `error.log`, so a script never mistakes a run that did nothing for one that worked.
