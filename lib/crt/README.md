# Visual C++ runtime DLLs

These are the Microsoft Visual C++ 2015-2022 x64 runtime DLLs (file version 14.44.35211.0).
`build-portable.ps1` copies them into the portable package so the app runs on a PC without the runtime installed.
They come unchanged from the Visual Studio 2022 folder `VC\Redist\MSVC\14.44.35112\x64\Microsoft.VC143.CRT`.
Microsoft allows their redistribution under the Visual C++ Redistributable terms in the Visual Studio license.
To update them, replace the DLLs with the ones from a newer `Microsoft.VC143.CRT` folder and change the versions above.
