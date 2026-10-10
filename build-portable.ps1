# -Public builds the copy that is published: generated floor textures, never the local set in resources\local.
param([string]$Output = (Join-Path $PSScriptRoot 'Portable'), [switch]$Public)
$ErrorActionPreference = 'Stop'
# Build in a fresh workspace; never mix previous test output into the delivery.
$destination = [IO.Path]::GetFullPath($Output)
$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
if ($destination.TrimEnd('\') -eq $projectRoot -or $projectRoot.StartsWith($destination.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output cannot be the project directory or one of its ancestors.'
}
$buildRoot = Join-Path $PSScriptRoot ('verification/package-builds/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
$Output = Join-Path $buildRoot 'staging'
# Development-only build. The resulting Portable folder requires no SDK or converter.
# A property passed on the command line overrides the project file, so it is only passed for the public build.
$floor = if ($Public) { @('-p:FloorTextures=resources') } else { @() }
dotnet publish (Join-Path $PSScriptRoot 'SSEAnimationPreviewer.csproj') -c Release -r win-x64 --self-contained true -o $Output @floor
if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
$vswhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -property installationPath
$crt = Get-ChildItem (Join-Path $vs 'VC/Redist/MSVC') -Directory |
    Where-Object Name -Match '^\d+\.' | Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64/Microsoft.VC143.CRT' } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (!$crt) { throw 'C++ runtime redistributables were not found in the build environment.' }
Copy-Item -LiteralPath (Get-ChildItem $crt -Filter '*.dll').FullName -Destination $Output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $Output
$sample = Join-Path $Output 'Samples/ParticleFixtures'
New-Item -ItemType Directory -Path $sample -Force | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot 'particle-fixtures/*') -Destination $sample -Recurse -Force
foreach ($file in 'LICENSE','THIRD-PARTY-NOTICES.md') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $Output -Force
}
foreach ($directory in 'licenses','source-patches','docs') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $directory) -Destination $Output -Recurse -Force
}
if (!(Test-Path (Join-Path $Output 'Resources/testgrid_d.dds'))) { throw 'Missing floor textures in the package.' }
$needed = 'coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Windows.Forms.dll','DirectXTexWrapper.dll','Ijwhost.dll','vcruntime140.dll','msvcp140.dll'
foreach ($file in $needed) { if (!(Test-Path (Join-Path $Output $file))) { throw "Missing portable dependency: $file" } }
# Paths are relative to the package root, including nested samples and licenses.
$prefix = [IO.Path]::GetFullPath($Output).TrimEnd('\') + '\'
Get-ChildItem -LiteralPath $Output -Recurse -File | Where-Object { $_.Name -ne 'manifest.sha256.csv' -and !$_.FullName.Substring($prefix.Length).StartsWith('Scenes\',[StringComparison]::OrdinalIgnoreCase) -and !$_.FullName.Substring($prefix.Length).StartsWith('Presets\',[StringComparison]::OrdinalIgnoreCase) } | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{File=$_.FullName.Substring($prefix.Length).Replace('\','/'); Hash=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
} | Export-Csv (Join-Path $Output 'manifest.sha256.csv') -NoTypeInformation
& (Join-Path $PSScriptRoot 'verify-portable.ps1') -Package $Output

# Validate full paths before moving an existing directory. Keep its contents as a
# recovery copy, including user-added files and previous verification evidence.
$backup = [IO.Path]::GetFullPath((Join-Path $buildRoot 'previous-portable'))
if (!$backup.StartsWith($projectRoot + '\verification\package-builds\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe backup path.' }
if (Test-Path -LiteralPath $destination) {
    if (!(Test-Path -LiteralPath $destination -PathType Container)) { throw 'Output exists as a file.' }
    if ((Get-Item -LiteralPath $destination).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Output must not be a directory link.' }
    Move-Item -LiteralPath $destination -Destination $backup
}
try {
    # Copy from the actual backup after the rename, so edits made during publish are retained.
    & (Join-Path $PSScriptRoot 'preserve-portable-data.ps1') -Previous $backup -Staging $Output
    & (Join-Path $PSScriptRoot 'verify-portable.ps1') -Package $Output
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Move-Item -LiteralPath $Output -Destination $destination
} catch {
    if (!(Test-Path -LiteralPath $destination) -and (Test-Path -LiteralPath $backup)) { Move-Item -LiteralPath $backup -Destination $destination }
    throw
}
Write-Host "Portable build ready: $destination"
if (Test-Path -LiteralPath $backup) { Write-Host "Previous folder preserved at: $backup" }

