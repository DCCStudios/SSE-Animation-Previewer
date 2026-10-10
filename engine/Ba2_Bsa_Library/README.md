# BSA/BA2 Library

## Licence

This project is licensed under the **GNU General Public License version 3**.
The full text is in `LICENSE`. Credits are in `LICENSE_CREDITS.txt`, and the
per-component copyright lines, licence texts and source-code offer are in
`THIRD-PARTY-NOTICES.md` and the `licenses/` folder.

## Requires the following libraries/packages

 - ManoloV02: DirectXTexWrapper - Licensed under the GPL-3.0 License (https://github.com/MANOLOV02/DirectXTexWrapper)
 - Microsoft: DirectXTex - Licensed under the MIT License (https://github.com/microsoft/DirectXTex)
     wrapped by DirectXTexWrapper
 - SharpZipLib Contributors: SharpZipLib 1.4.2 - Licensed under the MIT License (https://github.com/icsharpcode/SharpZipLib)
 - Milosz Krajewski: K4os.Compression.LZ4 (+ .Streams) - Licensed under the MIT License (https://github.com/MiloszKrajewski/K4os.Compression.LZ4)
     Copyright (c) 2017 Milosz Krajewski
 - Milosz Krajewski: K4os.Hash.xxHash - Licensed under the MIT License (https://github.com/MiloszKrajewski/K4os.Hash.xxHash)
     Copyright (c) 2017 Milosz Krajewski
 - Microsoft: System.IO.Pipelines - Licensed under the MIT License (https://github.com/dotnet/runtime)
 - Microsoft: Ijwhost (Ijwhost.dll) - Licensed under the MIT License (https://github.com/dotnet/runtime)
     C++/CLI host shim required by DirectXTexWrapper

## Build

Build with MSBuild, configuration `Publish`:

```
msbuild BSA_BA2_Library_DLL.vbproj -t:Restore,Build -p:Configuration=Publish -p:Platform=x64
```
