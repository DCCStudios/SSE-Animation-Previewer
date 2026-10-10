# DirectXTexWrapper

C++/CLI wrapper that exposes Microsoft's **DirectXTex** to .NET, plus the DXGI ↔ OpenGL format
mapping used to upload those textures to the GPU. Built as a mixed-mode assembly
(`DirectXTexWrapper.dll`), it is consumed by FO4 Base Library and by the BSA/BA2 Library, and ships
inside FO4 NPC Manager, Wardrobe Manager, Nif Explorer and BA2/BSA Manager.

## Licence

Copyright (C) 2025 ManoloV02 — https://github.com/MANOLOV02

This program is free software: you can redistribute it and/or modify it under the terms of the
**GNU General Public License version 3**, as published by the Free Software Foundation, either
version 3 of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
General Public License for more details.

You should have received a copy of the GNU General Public License along with this program. If not,
see <https://www.gnu.org/licenses/>.

The full text is in [`LICENSE`](LICENSE).

> Previously this file *was* the GPL text pasted in whole. That is not a licence grant: the text
> opens with the Free Software Foundation's copyright over the licence **document**, not with a
> statement placing this code under it. The grant is the paragraph above, and the licence text now
> lives in `LICENSE`, where tooling and users look for it.

## Third-party

| Component | Licence | Project |
|---|---|---|
| DirectXTex | MIT | https://github.com/microsoft/DirectXTex |

DirectXTex is © Microsoft Corporation and is used under the MIT License. Its notice must travel with
any binary distribution that includes it; the applications that ship this wrapper carry it in their
`THIRD-PARTY-NOTICES.md` and `licenses/MIT.txt`.

Building the mixed-mode assembly also pulls in `Ijwhost.dll` (© Microsoft Corporation, MIT), the
C++/CLI host shim from the .NET runtime.

## Build

```
msbuild DirectXTexWrapper.vcxproj -p:Configuration=Release -p:Platform=x64
msbuild DirectXTexWrapper.vcxproj -p:Configuration=Release -p:Platform=Win32
```

Only `Debug` and `Release` exist here. The consuming projects declare a `Publish` configuration and
map this one to `Release` on purpose — asking this project for `Publish` fails. See
`DirectXTexWrapper.targets`, which also pins the native platform so an x86 wrapper cannot end up
inside an x64 process.
