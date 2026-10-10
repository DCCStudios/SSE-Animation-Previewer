# Native source

Source for the one part of the engine that is not built by `dotnet build`: the C++/CLI wrapper that decodes DDS textures through Microsoft's DirectXTex. The built files are `lib/DirectXTexWrapper.dll` and `lib/Ijwhost.dll`.

| Folder | Upstream | Commit | License |
|---|---|---|---|
| `DirectXTextWrapper` | https://github.com/MANOLOV02/DirectXTexWrapper | `234b8cda9d8acc1a9cde9bb1a58c0e935a6dd83a` | GPL-3.0 |
| `DirectXTex` | https://github.com/microsoft/DirectXTex | `603bfa32b8dc7272eace963cdd05a03016224b61` | MIT |

Of DirectXTex, only the library and `Common` folders were copied.

## Rebuilding

You only need this if you change the wrapper. It takes Visual Studio 2022 or later with the C++ desktop workload and C++/CLI support.

1. Build `DirectXTex/DirectXTex/DirectXTex_Desktop_2026.vcxproj` for Release x64. If your Visual Studio is older, retarget the project to your toolset first.
2. Build `DirectXTextWrapper/DirectXTexWrapper.vcxproj` for Release x64.
3. Copy the new `DirectXTexWrapper.dll` and `Ijwhost.dll` over the ones in `lib`.
