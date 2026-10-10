# Command line

[Back to the README](../README.md)

The exe takes switches for scripted runs. They exist mainly to test the program itself, but two are useful to anyone.

## Background mode

`--background` opens the previewer off-screen, out of the taskbar and never as the foreground window. Rendering is unchanged. A capture taken this way is pixel-identical to one from a normal run. The environment variable `SSE_PREVIEW_BACKGROUND=1` does the same.

Combine it with any of the modes below.

## Reading clips without the window

```
"SSE Animation Previewer.exe" --dump-annotations <clip.hkx> [more clips]
```

Writes each clip's duration and annotations, with times and track numbers, to `annotations.txt` beside the exe.

```
"SSE Animation Previewer.exe" --background --track-bone <bone> <reference bone> <clip.hkx> [more clips]
```

Writes a log beside the exe of how a bone moves relative to another across each clip. The test data must be present, since it needs a skeleton. It answers questions such as "is `WEAPON` animated separately from the right hand in this export".

## Documentation screenshots

```
"SSE Animation Previewer.exe" --background --docs-capture <output folder> <Skyrim Data folder> <clip.hkx> [more clips]
```

Renders the pictures used in these pages: a Nord in iron armour with an iron battleaxe, the overlays, the queue and a vanilla effect.

## Built-in checks

These run a feature against fixtures and write a pass/fail log beside the exe. Most expect the source tree's folder layout, and several expect a local `test-data` folder of vanilla assets, which is not in the repository. See [Building](building.md).

| Switch | Covers |
|---|---|
| `--particle-test` | Particle simulation, effect resolution, speed maps and annotation activity (unit tests, no window) |
| `--catalog-test` | OAR and DAR library reading and the override lookup |
| `--smp-test` | HDT-SMP XML reading and solver setup |
| `--queue-smoke <clips>` | Queue, drag feedback, root motion arrow, follow camera |
| `--precision-smoke <clips>` | Precision timeline rules and capsule drawing |
| `--annotation-smoke <clip>` | Active annotation highlight |
| `--skeleton-smoke <clip>` | Bone overlay |
| `--override-smoke` | Library row colours and hover text |
| `--flipbook-smoke <mod folder> <nifs>` | Sub-texture sheet sampling |
| `--camera-smoke`, `--floor-smoke` | Camera following and zoom, grid floor |

A run that fails exits with code 1 and names the failed check in its log.
