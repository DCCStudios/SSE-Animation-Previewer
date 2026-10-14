# Building

[Back to the README](../README.md)

Everything needed to build is in this repository. You install one thing, the .NET 8 SDK or a later one. GitHub Actions builds with the .NET 8 SDK on every push, so that minimum stays tested.

```
git clone https://github.com/DCCStudios/SSE-Animation-Previewer
cd SSE-Animation-Previewer
dotnet build -c Release
```

The exe lands in `bin\Release\net8.0-windows\win-x64`. The first build downloads its NuGet packages (OpenTK, BulletSharp, NAudio, SharpZipLib, LZ4, miniball, the Roslyn packages the generator uses, and the .NET runtime packs), so it needs a network connection once.

Windows x64 only. Visual Studio is not required.

## What is in the repository

| Path | Contents |
|---|---|
| `src` | The previewer itself |
| `engine` | The renderer and file readers it is built on, as source. See [engine/README.md](https://github.com/DCCStudios/SSE-Animation-Previewer/blob/main/engine/README.md). |
| `native` | Source of the C++/CLI DDS wrapper. See [native/README.md](https://github.com/DCCStudios/SSE-Animation-Previewer/blob/main/native/README.md). |
| `lib` | The built DDS wrapper and the Microsoft C++ runtime files that ship with the program |
| `particle-fixtures` | Small NIF files written for the particle tests |
| `scripts` | Packaging scripts |
| `docs`, `licenses` | These pages and the third-party license texts |

`dotnet build` compiles `src` and everything under `engine`. The DDS wrapper in `lib` is the only prebuilt part. It is C++/CLI, which the .NET SDK cannot compile, so a built copy is checked in and its source sits beside it in `native`. Rebuild it only if you change it. The Microsoft C++ runtime files it needs are in `lib\crt` and are copied beside the exe by the build.

## Portable folder

```
.\scripts\build-portable.ps1
```

This publishes a self-contained build to `Portable`, adds the C++ runtime, licenses, docs and particle fixtures, writes a SHA-256 manifest and verifies it. If `Portable` already exists, your scenes, presets and settings are carried over and the old folder is kept under `verification\package-builds`.

`.\scripts\build-portable.ps1 -Public -Output <folder>` builds the copy that gets released. It always uses the floor textures in `src\resources`.

GitHub Actions (`.github/workflows/build.yml`) builds every push to `main`, every pull request and every version tag of the form `v1.2.3`, and can be started by hand on any branch. Pushing a tag such as `v0.2.0` also attaches the portable zip to a release and stamps that version into the build. A tag with anything after the three numbers, such as `v0.2.0-rc1`, makes a prerelease.

A release also goes to the Nexus Mods page. `.github/workflows/nexus.yml` uploads the zip as a new version of the main file and archives the version before it, for releases made by a tag push and for releases published by hand. It needs the `NEXUSMODS_API_KEY` secret and the `NEXUSMODS_FILE_ID` variable on the repository. Without the variable it is skipped. With the variable and no key it fails and says so. An optional `NEXUSMODS_MOD_ID` variable, holding the mod's id from the Nexus v3 API, turns on changelog entries. Prereleases are not sent.

Before pushing a version tag, add a section for the version to [docs/changelog.md](changelog.md). The release notes are that section, and each of its bullets becomes a changelog entry on the Nexus page. Without a section, the release uses GitHub's generated notes and Nexus gets one line linking the release. `python scripts/changelog.py 0.3.0 --nexus` shows what Nexus will receive.

### Floor textures

`src\resources\testgrid_*.dds` are grid textures generated for this project. To use your own, put files with the same three names in `src\resources\local`. That folder is ignored by git, and a normal build picks it up automatically.

## Source map

| File | Contents |
|---|---|
| `src\MainWindow.cs` | The window, every control, session handling and the smoke tests |
| `src\Scene*.cs` | Scene assembly, attachment, overlays, physics write-back |
| `src\Animation.cs` | Clip decoding, speed maps, the queue timeline |
| `src\Effects.cs`, `Particle*.cs`, `Nif*.cs` | Effect resolution and the particle simulator |
| `src\Smp*.cs` | HDT-SMP reader, collision and Bullet setup |
| `src\PrecisionCollisions.cs` | Precision timeline and capsule geometry |
| `src\ClipCatalog.cs` | OAR and DAR library and the override lookup |
| `src\Records.cs`, `Assets.cs`, `Archive*.cs` | Plugin records, virtual file system, BSA load order |
| `engine\FO4_Base_Library\Render.vb` | The viewport, shaders, lighting and camera |

## Tests

The checks are built into the exe. See [Command line](command-line.md).

```
cd bin\Release\net8.0-windows\win-x64
"SSE Animation Previewer.exe" --ci-test
```

`--ci-test` runs every check that needs only the files in this repository. It is what GitHub Actions runs.

The other checks need files that cannot be redistributed:

- `test-data` at the repository root, a small copy of vanilla meshes, textures and `Skyrim.esm` from your own installation
- a folder of mods, named by the `SSE_TEST_MODS` environment variable or by the first line of a `test-mods.txt` file at the repository root (git ignores that file)

A check whose files are missing reports that it was skipped.

## Code style

The previewer source is written dense, with long lines and few blank lines. The engine is VB.NET with Spanish comments. Match the file you are in.
