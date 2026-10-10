# Getting started

[Back to the README](../README.md)

## Install

Unpack the release zip and run `SSE Animation Previewer.exe`. The folder holds the .NET desktop runtime, the NIF, HKX and archive readers, the DDS decoder and the C++ runtime. Nothing is written outside that folder.

## Load your game data

The toolbar across the top does the loading.

| Button | What it does |
|---|---|
| **Open Data…** | Mounts a Skyrim `Data` folder as the base. Loose files and its BSA archives are both read. |
| **Add mod folder…** | Mounts one more folder on top. Its loose files beat everything below. Later folders win. |
| **Load order…** | Selects a `plugins.txt`, so plugins and their paired BSAs load in your real order. |
| **Add HKX…** | Opens single animation files from anywhere. |
| **Restore session** | Brings back the last session: folders, load order, character, clip and settings. |
| **Scenes...** | Save scene, Load scene and a list of recent scenes. |
| **Reset** | Unloads everything, including the mounted folders, and replaces the saved session with an empty one. |
| **Lighting…** | Opens the light rig and render settings. |
| **Frame F** | Fits the camera to the scene. |

Base game meshes and textures are needed even when you are testing one small mod, because the character's body, head and skeleton come from them. A mod folder on its own can list its animations but will have no body to play them on.

The **Loaded sources** list under the animation library shows every mounted folder in precedence order with its loose file and BSA counts. Double-click a folder to open it in Explorer. Right-click to copy its path, or to remove it and reload the rest.

### Mod Organizer 2

This route is untested. The program reads files in the ordinary way, so it should see MO2's virtual file system when started from MO2: add the exe as an executable, start it from there, use **Open Data…** on the game's `Data` folder and **Load order…** on the profile's `plugins.txt`.

The route used during development skips MO2: add individual folders from your `mods` directory with **Add mod folder…**. That is also faster when you only care about one moveset.

## First clip

1. Pick a race and sex under **CHARACTER**. Nord male is the default.
2. Pick armor by slot under **EQUIPMENT** and press **Equip armor**.
3. Pick a weapon under **WEAPON**.
4. Click a clip in the library on the left.

The clip plays on a loop. The tabs under the viewport show where it came from, what its annotations are and anything the previewer could not load.

| Tab | Contents |
|---|---|
| **Configuration & source** | The clip's file path, source folder, and its OAR or DAR configuration with conditions written out |
| **Annotations & effects** | Every annotation with its time, Precision collisions, sounds and resolved effects |
| **Diagnostics** | Missing files, unsupported NIF blocks, physics messages |
| **Warnings** | Annotation sounds that cannot play, and which plugin would supply them |
| **Queue** | The [animation queue](animation-queue.md) |

## Drop-down lists

The record drop-downs (race, armor, weapon and the like) open with a filter box. Type a few words and the list narrows to entries that contain all of them. Arrow keys or the mouse pick an entry, Enter confirms. F4 or Alt+Down opens a list from the keyboard.

## Sessions and scenes

The previewer saves your session as you work: mounted folders, load order, character, equipment, the selected clip, the queue and the tick boxes. After a restart, press **Restore session** before anything else. Mounting a folder or pressing Reset first saves a new session over the old one.

Lighting, render options and the Require Alt setting are kept separately in `viewer-settings.json` and apply to every session.

A scene is the same information saved to a file you name. **Scenes...** has **Save scene...**, **Load scene...** and **Recent**, which lists the last twelve. Scene files are plain JSON (`*.ssescene.json`) and go wherever you save them, so you can keep one per character or per moveset. If a scene points at a folder that has moved, the previewer asks where it went and repairs the paths.

## The grid floor

**Grid floor at origin** draws a disc at height zero, the level the skeleton root stands on. With the floor textures that ship with the program, cells are 64 game units. A character is about 128 tall. The floor receives the character's shadow, which makes foot contact and travel distance easy to read. It is a preview aid and takes no part in particle collision.
