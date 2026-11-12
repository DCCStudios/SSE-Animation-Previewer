# Changelog

Each release has a section headed by its version number. The release workflow copies the section into the GitHub
release notes and adds every bullet as its own changelog entry on the Nexus Mods page, with the bold, code and link
markup removed. Write the section before pushing the version tag. A bullet may wrap onto lines indented by two
spaces.

## 0.6.0

- **Precision blade trails.** The trail Precision draws behind a `Collision_Add` capsule on a weapon or shield shows
  in the window, with the mesh, colour and lifetime taken from the capsule, the Precision trail TOML files and the
  Precision MCM settings. The tick box is under PRECISION.
- **Effect frame sequences.** `--effect-frames <jobs file>` renders many effect NIFs as numbered frames and looping
  GIFs in one run, with the data folder mounted once.
- **Effect captures wait for textures.** `--effect-capture` waits until the NIF and its textures have loaded before
  each image, and exits with code 3 when they do not load in time.
- **Weapon names from localized plugins.** A weapon's name is read from the plugin's strings file when the plugin
  is localized.

## 0.5.0

- **Animated camera.** The camera list under CAMERA plays a clip's `Camera3rd [Cam3]` bone, either as the offset
  Skyrim applies to its own camera or looking through the bone. It is off by default.
- **Camera annotations.** The tick box beside it turns the animated camera on and off at the clip's
  StartAnimatedCamera and EndAnimatedCamera annotations.
- **Frame captures.** `--frames-capture` takes `camera=game`, `camera=look` and `cameraevents`.

## 0.4.0

- **View captures.** `--view-capture` renders a NIF, or a JSON manifest of placed NIFs, from a camera standing at a
  point in game units, and writes a PNG with a pass or fail log beside it. `maxtex=` and `maxmem=` limit texture size
  and memory for large levels, and `base=` reads a Skyrim Data folder's archives underneath the mounted folder.
- **Water.** Water surfaces draw with a rebuild of Skyrim's water shader: wave normals, fresnel, reflection,
  refraction with depth fog and river flowmaps, from the water types in the loaded plugins.
- **Weather.** `weather=` on a view capture draws a weather's sky colours, sky statics, fog and light at a chosen
  hour.
- **True PBR.** Two-layer coats, interlayer parallax, subsurface scattering, emissive maps and an environment
  cubemap draw as Community Shaders reads them.
- **Effect captures.** `--effect-capture` takes settings for True PBR shading, the particle budget, a background
  colour, camera angles, extra NIFs at their own start times and bloom. It accepts effects from 0.1 to 60 s long; the
  limit was 1 to 4 s.
- **Effect previews.** An effect NIF opened on its own plays for as long as its controllers run; it stopped after
  10 s. Blended shapes at the same depth no longer swap order between frames. Captured images are opaque.
- **Background runs.** A command-line run never takes the keyboard focus and never shows a dialog; a scene that
  references a missing file skips it and says so.
- **Frame captures.** `--frames-capture` gains `sky=day` and `nogrid`, `restart` shows the clip from its first frame,
  and playback recovers when a frame tick is lost.

## 0.3.0

- **Smoother playback.** Animations play at 60 FPS. They were held near 32 by timer rounding and by the toolbar
  being laid out again on every frame.
- **Creature animations.** Picking a werewolf, bear, wolf or other creature clip switches to a race that uses that
  skeleton. Vampire lord clips play on their own skeleton and body when Dawnguard.esm is not loaded. A clip that does
  not fit the race you pick leaves the character in its rest pose instead of twisting it.
- **Race skeletons.** A race loads the skeleton its record names, so follower races with custom skeletons get theirs.
- **True PBR preview.** Shapes patched for Community Shaders True PBR render with its PBR lighting: metal, roughness
  and ambient occlusion from the RMAOS map. The tick box is under the camera options and turns itself on when the
  scene has such shapes.
- **Start without focus.** Command-line tool runs work off-screen, and `--no-activate` opens the window without
  taking the keyboard focus.

## 0.2.1

- **Effect capture is back.** `--effect-capture` renders an effect NIF to image files again, and the older
  `--cloud-effect-alpha-smoke` name still works.
- **Unknown switches are reported.** A switch the program does not know exits with code 2 and says so.

## 0.2.0

- **BFCO clips.** Clips made for the BFCO attack framework play as BFCO plays them: combo hand-over at its window
  annotations, its attack speed, notes for each clip, and highlighting until `BFCO_DIY_EndLoop`.
- **Missing particle textures** draw a placeholder and name the missing file.
- **DAR folders** keyed by plugin and actor count as overriders at priority 0, as OAR loads them.
- **`--frames-capture`** records frames from a saved scene for animated pictures.
- **Smaller download.** No debug symbols; license texts for everything it ships are included.

## 0.1.0

- First public build: timeline and root motion, animation queue with MCO windows, annotation list, OAR and DAR
  library, character and equipment, Precision collisions, HDT-SMP physics, effect preview and annotation sounds.
