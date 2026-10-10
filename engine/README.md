# Engine source

The previewer's renderer and file readers, vendored so the repository builds on its own. Each folder is a copy of the upstream project at the commit below, with the previewer's changes already applied. Each keeps its own license.

| Folder | Upstream | Commit | License |
|---|---|---|---|
| `FO4_Base_Library` | https://github.com/MANOLOV02/FO4_Base_Library | `1f6a72f188b846901217d9878565106566138128` | GPL-3.0, with xEdit-derived files under MPL-2.0 (see its THIRD-PARTY-NOTICES.md) |
| `Ba2_Bsa_Library` | https://github.com/MANOLOV02/BSA_BA2_Library_DLL | `1bfbeb59ca54219e3f208fbee52bc904ef39f743` | GPL-3.0 |
| `NiflySharp` | https://github.com/MANOLOV02/NiflySharpFork | `8035a250f88eeb0c925e498f11092cb01ae28c89` | GPL-3.0 |
| `NiflySharp/nifxml` | https://github.com/niftools/nifxml | `292bb9403cbf4052c58d66e80906b6bde1700779` | GPL-3.0 |
| `Material-Editor-master` | https://github.com/ousnius/Material-Editor | `21411873f17454a2442d6785d533499e14c63adb` | MIT |

Only what the build needs was copied: test projects, the Material Editor application and all of nifxml except `nif.xml` and its license were left out. Some generated files in FO4_Base_Library name generator scripts under `Tools/`; upstream does not publish those, so they are not here either.

## Changes from upstream

- `FO4_Base_Library` (changed between 2026-09-26 and 2026-10-08): `FO4UnifiedMaterial_Class.vb`, `FilesDictionary_class.vb`, `Havok/Anim/HkxAnimationGraphParser.vb`, `Havok/Anim/HkxPoseImportHelper.vb`, `NifContent_Class.vb` and `Render.vb` carry the previewer's changes: direct HKX decoding, stored-track interpolation, scene hooks, a frame cap, render diagnostics and safe colour conversion.
- `NiflySharp`: `NiflySharp/NiflySharp.csproj` and `NiflySharp.Generator/NiflySharp.Generator.csproj` have small project changes.
- `FO4_Base_Library.vbproj` and `BSA_BA2_Library_DLL.vbproj` reference the prebuilt `lib/DirectXTexWrapper.dll` in place of the C++ project, so the .NET SDK alone can build them.

To see the exact differences, diff a folder against its upstream commit.
