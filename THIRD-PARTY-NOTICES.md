# Third-party source and notices

This utility uses FO4_Base_Library and its renderer/asset readers, adapted from the ecosystem of MANOLOV02/FO4_NPC_Manager.

Sources retained in this workspace:
- https://github.com/MANOLOV02/FO4_NPC_Manager
- https://github.com/MANOLOV02/FO4_Base_Library
- https://github.com/MANOLOV02/NiflySharpFork
- https://github.com/MANOLOV02/BSA_BA2_Library_DLL
- https://github.com/ousnius/Material-Editor
- https://github.com/Microsoft/DirectXTex
- https://github.com/MANOLOV02/DirectXTexWrapper

Additional managed dependencies: OpenTK and OpenTK.GLControl, SharpZipLib, K4os.Compression.LZ4, and miniball-devel0. The portable runtime includes Microsoft's .NET Windows Desktop runtime and Visual C++ runtime redistributable libraries.

HDT-SMP simulation uses BulletSharp 0.12.0 (BulletSharp.x64 package, copyright (c) 2013-2019 Andres Traks) and the Bullet Physics library it bundles as libbulletc.dll, both under the zlib license. The text is in licenses/BulletSharp-LICENSE.txt. The physics XML reader and the skinned-mesh collision code (SmpPhysics.cs, SmpCollision.cs) are a C# port of logic from Faster HDT-SMP, https://github.com/DaymareOn/hdtSMP64, which is licensed under GPL-3.0 with a modding exception. The FSMP plugin itself is not included or loaded.

Copies of upstream license files are in licenses. Source patches against the checked-out revisions are in source-patches. The local source checkouts contain the corresponding full source; the portable runtime folder alone is not a complete source distribution.

AMR, PayloadInterpreter, DynamicAnimationCasting and DtryKeyUtil source repositories are present under PluginTemplate for format/behavior reference. Their SKSE plugin DLLs are not required or loaded by this standalone application.

The application source is provided under GPL-3.0-or-later, subject to third-party components' own licenses. Game and mod assets are not included, except for the user-supplied floor textures noted below in this local build.

The refraction geometry and composition shaders adapt equations from Community Shaders Utility.hlsl and ISRefraction.hlsl, copyright (c) 2022 Ilya Perapechka, under the MIT license. The full notice is included as licenses/CommunityShaders-Shaders-LICENSE.txt. Native material and permutation mapping was independently checked against the local AE 1.6.1170 executable without executing it.

This local build also includes the three Test Room floor textures explicitly supplied by the user (testgrid_d.dds, testgrid_n.dds, testgrid_s.dds). They remain third-party mod assets and are not covered by the application source license.
