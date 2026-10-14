# Changelog

Each release has a section headed by its version number. The release workflow copies the section into the GitHub
release notes and adds every bullet as its own changelog entry on the Nexus Mods page, with the bold, code and link
markup removed. Write the section before pushing the version tag. A bullet may wrap onto lines indented by two
spaces.

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
