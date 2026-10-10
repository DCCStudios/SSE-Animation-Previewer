# Building

[Back to the README](../README.md)

## What you need

- Windows x64
- .NET 8 SDK
- Visual Studio 2022 with the C++ desktop workload (for the engine's native DDS wrapper, and for the C++ runtime files the portable script copies)

## Engine libraries

The previewer links the renderer and asset readers from MANOLOV02's libraries. They are not in this repository. `SSEAnimationPreviewer.csproj` expects their build output at:

```
..\PluginTemplate\FO4_Base_Library\bin\x64\Release\net8.0-windows\win-x64
```

Change `EngineBin` in the project file if you keep them somewhere else.

1. Clone the repositories listed in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) at the revisions recorded in `source-patches/*.revision.txt`.
2. Apply `source-patches/FO4_Base_Library.patch` and `source-patches/NiflySharp.patch`. They add direct HKX decoding, scene hooks, a presentation cap and a few fixes the previewer relies on.
3. Build FO4_Base_Library in Release for x64. Its output folder then holds `FO4_Base_Library.dll`, `NiflySharp.dll`, `MaterialLib.dll`, `BSA_BA2_Library_DLL.dll`, `DirectXTexWrapper.dll` and `Ijwhost.dll`.

## Build and run

```
dotnet build SSEAnimationPreviewer.csproj -c Release
```

The exe lands in `bin\Release\net8.0-windows\win-x64`.

## Portable folder

```
.\build-portable.ps1
```

This publishes a self-contained build, copies the C++ runtime, licenses, docs and particle fixtures, writes a SHA-256 manifest and verifies it. If a `Portable` folder already exists, your scenes, presets and settings are carried over and the old folder is kept under `verification\package-builds`.

`.\build-portable.ps1 -Public -Output <folder>` builds the copy that gets released. It always uses the generated floor textures in `resources`.

### Floor textures

`resources\testgrid_*.dds` are generated grid textures that belong to this project. To use your own, put files with the same three names in `resources\local`. That folder is ignored by git, and a normal build picks it up automatically.

## Layout

| Path | Contents |
|---|---|
| `MainWindow.cs` | The window, every control, session handling and the smoke tests |
| `Scene*.cs` | Scene assembly, attachment, overlays, physics write-back |
| `Animation.cs` | Clip decoding, speed maps, the queue timeline |
| `Effects.cs`, `Particle*.cs`, `Nif*.cs` | Effect resolution and the particle simulator |
| `Smp*.cs` | HDT-SMP reader, collision and Bullet setup |
| `PrecisionCollisions.cs` | Precision timeline and capsule geometry |
| `ClipCatalog.cs` | OAR and DAR library and the override lookup |
| `Records.cs`, `Assets.cs`, `Archive*.cs` | Plugin records, virtual file system, BSA load order |
| `particle-fixtures` | Original NIF fixtures for the particle tests |
| `source-patches` | Patches and pinned revisions for the engine libraries |

## Tests

The checks are built into the exe. See [Command line](command-line.md).

Some of them need files that cannot be redistributed:

- `test-data`, a small copy of vanilla meshes, textures and `Skyrim.esm` from your own installation
- mod folders named in `SelfTest.cs`, which point at one local mod list

Checks whose files are missing fail or skip. The unit tests on the bundled fixtures (`--particle-test`, `--smp-test`) run anywhere.

## Code style

The source is written dense, with long lines and few blank lines. Match the file you are in.
