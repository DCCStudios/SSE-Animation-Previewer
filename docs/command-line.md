# Command line

[Back to the README](../README.md)

The exe takes switches for scripted runs. Most exist to test the program itself. The first few below are useful to anyone.

## Background mode

`--background` opens the previewer off-screen, out of the taskbar and never as the foreground window. It renders with the same code as a normal run. The environment variable `SSE_PREVIEW_BACKGROUND=1` does the same.

`--data <folder>` mounts a Data folder at start.

Combine it with any of the modes below.

## Reading clips from a script

```
"SSE Animation Previewer.exe" --background --dump-annotations <clip.hkx> [more clips]
```

Writes each clip's duration and annotations, with times and track numbers, to `annotations.txt` beside the exe.

```
"SSE Animation Previewer.exe" --background --track-bone <bone> <reference bone> <clip.hkx> [more clips]
```

Writes `track-bone.txt` beside the exe, a log of how a bone moves relative to another across each clip. It takes the skeleton from your last session, so run the previewer once and mount your data first, or point the `SSE_SESSION_FILE` environment variable at a session file. It answers questions such as "is `WEAPON` animated separately from the right hand in this export".

## Documentation screenshots

```
"SSE Animation Previewer.exe" --background --docs-capture <output folder> <Skyrim Data folder> <clip.hkx> [more clips]
```

Renders most of the pictures used in these pages: a Nord with an iron battleaxe, the overlays, the queue and a vanilla effect. The clips you pass decide the poses.

## Capturing an effect NIF

```
"SSE Animation Previewer.exe" --background --effect-capture <output folder> <nif> <nif> <Data folder>
```

Renders two effect NIFs (the same file twice is fine) with the textures in the Data folder, and writes `original_sample.png`, `original_wrapped.png`, `corrected_sample.png`, `corrected_wrapped.png` and `validation.txt` to the output folder. It exits with code 1 when an effect shows nothing or its clock does not loop. Other tools use it to build preview sheets.

Environment variables set the shot: `SSE_EFFECT_CAPTURE_TIME` (seconds into the effect, default 0.934), `SSE_EFFECT_CAPTURE_DISTANCE` (camera distance, default 350), `SSE_EFFECT_CAPTURE_FOCUS_ORIGIN=1` (aim at the point 100 units above the origin) and `SSE_EFFECT_CAPTURE_AUTOMOUNT=1` (mount the folder above the NIF's `meshes` folder in place of the Data argument).

## Recording frames for an animated picture

```
"SSE Animation Previewer.exe" --background --frames-capture <scene file> <output folder> <seconds> [options]
```

Loads a saved scene, plays it in real time and writes `frame_0000.png` and up, with the real frame rate on the first line of `frames.txt`. Sounds are muted. Options include `fps=15`, `clip=<part of a clip path>`, `queue=<part>|<part>`, `smp`, `colliders`, `precision`, `skeleton`, `follow`, `window` (the whole window, not the viewport alone), `rotate=<dx>,<dy>`, `zoom=<steps>` and `size=<width>x<height>`. Turn the frames into a GIF with a tool such as ffmpeg.

## Built-in checks

These run a feature against fixtures and write a pass/fail log beside the exe. They expect the source tree's folder layout. `--ci-test` runs everything that needs only the files in the repository. Many of the others need a local `test-data` folder of vanilla assets or a folder of mods, and report `SKIPPED` when those are missing. See [Building](building.md).

| Switch | Covers |
|---|---|
| `--ci-test` | The three suites below in one run, with a summary in `ci-test.txt`. Exit code 0 only if all pass. |
| `--particle-test` | Particle simulation, effect resolution, speed maps and annotation activity (unit tests, no window) |
| `--catalog-test` | OAR and DAR library reading and the override lookup |
| `--smp-test` | HDT-SMP XML reading and solver setup |
| `--queue-smoke <clips>` | Queue, drag feedback, root motion arrow, follow camera |
| `--precision-smoke <clips>` | Precision timeline rules and capsule drawing |
| `--annotation-smoke <clip>` | Active annotation highlight |
| `--bfco-smoke <three BFCO clips>` | BFCO notes, speed, highlight and queue hand-over |
| `--skeleton-smoke <clip>` | Bone overlay |
| `--override-smoke` | Library row colours and hover text. Run `--catalog-test` first, which writes its fixture. |
| `--flipbook-smoke <mod folder> <nifs>` | Sub-texture sheet sampling |
| `--camera-smoke`, `--floor-smoke` | Camera following and zoom, grid floor |

A run that fails exits with code 1 and names the failed check in its log. A switch given too few arguments exits with code 2 and says how many it needs. A switch the program does not know also exits with code 2, with a line on standard error and in `error.log`, so a script never mistakes a run that did nothing for one that worked.
