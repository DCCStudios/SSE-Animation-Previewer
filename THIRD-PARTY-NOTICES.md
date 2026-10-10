# Third-party source and notices

This utility uses FO4_Base_Library and its renderer/asset readers, adapted from the ecosystem of MANOLOV02/FO4_NPC_Manager.

Their source is included in this repository under engine and native, at the upstream commits listed in engine/README.md and native/README.md, with the previewer's changes applied. Upstream projects:
- https://github.com/MANOLOV02/FO4_NPC_Manager
- https://github.com/MANOLOV02/FO4_Base_Library
- https://github.com/MANOLOV02/NiflySharpFork
- https://github.com/MANOLOV02/BSA_BA2_Library_DLL
- https://github.com/ousnius/Material-Editor
- https://github.com/Microsoft/DirectXTex
- https://github.com/MANOLOV02/DirectXTexWrapper

Additional managed dependencies: OpenTK and OpenTK.GLControl (MIT), SharpZipLib (MIT), K4os.Compression.LZ4 (MIT), miniball-devel0 (Apache-2.0) and NAudio 2.2.1 (MIT, copyright 2020 Mark Heath). OpenTK brings GLFW (zlib/libpng). licenses/FO4_Base_Library-THIRD-PARTY-NOTICES.md is the engine's own notice file and lists each of these with its copyright line, along with the other components the engine itself builds on: xEdit, whose format declarations are compiled into the engine under MPL-2.0, HavokLib (GPL-3.0), and niftools' nifxml (GPL-3.0). The license texts are in licenses: MIT.txt, Apache-2.0.txt, MPL-2.0.txt, GLFW-zlib.txt, NAudio-LICENSE.txt and nifxml-LICENSE. The portable runtime includes Microsoft's .NET runtime and Windows Desktop runtime (MIT, copyright .NET Foundation and Contributors; the package carries their license and third-party notices as licenses/dotnet-runtime-LICENSE.txt and licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt) and Visual C++ runtime redistributable libraries.

HDT-SMP simulation uses BulletSharp 0.12.0 (BulletSharp.x64 package, copyright (c) 2013-2019 Andres Traks) and the Bullet Physics library it bundles as libbulletc.dll, both under the zlib license. The text is in licenses/BulletSharp-LICENSE.txt. The physics XML reader and the skinned-mesh collision code (SmpPhysics.cs, SmpCollision.cs) are a C# port of logic from Faster HDT-SMP, https://github.com/DaymareOn/hdtSMP64, which is licensed under GPL-3.0 with a modding exception. The FSMP plugin itself is not included or loaded.

Copies of upstream license files are in licenses. The portable runtime folder alone is not a complete source distribution; the corresponding source is this repository.

The formats and behaviour of AMR, Payload Interpreter, Dynamic Animation Casting, Dtry's Key Utils, Precision and FSMP were taken from their published source. None of their SKSE plugin DLLs is included, required or loaded by this standalone application. Two parts of the previewer follow published source closely: the FSMP-derived physics reader noted above, and the Precision collision rules in PrecisionCollisions.cs, which follow PrecisionHandler.cpp, AttackCollision.cpp and Settings.h from Precision by Ersh (https://github.com/ersh1/Precision, GPL-3.0).

The application source is provided under GPL-3.0-or-later, subject to third-party components' own licenses. Game and mod assets are not included.

The refraction geometry and composition shaders adapt equations from Community Shaders Utility.hlsl and ISRefraction.hlsl, copyright (c) 2022 Ilya Perapechka, under the MIT license. The full notice is included as licenses/CommunityShaders-Shaders-LICENSE.txt. Native material and permutation mapping was independently checked against the local AE 1.6.1170 executable without executing it.

The grid floor textures in resources (testgrid_d.dds, testgrid_n.dds, testgrid_s.dds) and the particle fixtures in particle-fixtures were generated for this project and are covered by the application license.

The engine's interface icons (engine/FO4_Base_Library/Resources/Icons and the copies embedded in IconFormBase.resx) carry no source note upstream. By their artwork and file names they appear to be from the Crystal icon set by Everaldo Coelho, distributed with KDE under the GNU Lesser General Public License 2.1. They are credited here on that basis, and the license text is in licenses/LGPL-2.1.txt. The previewer's own window does not display them.

Three engine files follow other projects' source closely, by their own comments: RaceMenuJslot.vb (RaceMenu's skee64 preset reader by expired6978), LmHairColorLut.vb (LooksMenu's f4ee by expired6978) and Jsoncpp.vb (a number conversion table from JsonCpp, MIT or public domain, by Baptiste Lepilleur and the JsonCpp authors). The first two upstream repositories state no license. These files come with the engine as published by its author. The previewer does not call them.
