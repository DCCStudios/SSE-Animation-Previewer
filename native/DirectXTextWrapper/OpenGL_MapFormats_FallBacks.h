// DirectXTexWrapper — C++/CLI wrapper over Microsoft DirectXTex.
// Copyright (C) 2025  ManoloV02  <https://github.com/MANOLOV02>
//
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
// even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
// General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with this program.
// If not, see <https://www.gnu.org/licenses/>.
//
// Wraps DirectXTex, (C) Microsoft Corporation, used under the MIT License.
// See README.md and LICENSE.

#pragma once

struct FallbackMapping
{
    const char* originalName;
    int fallbackCode;
    const char* fallbackName;
};

#ifdef __cplusplus
extern "C" {
#endif

    int ResolveFallback(int dxgiCode);

#ifdef __cplusplus
}
#endif
