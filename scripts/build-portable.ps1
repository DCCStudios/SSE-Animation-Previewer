# -Public builds the copy that is published: generated floor textures, never the local set in src\resources\local.
param([string]$Output = (Join-Path (Split-Path $PSScriptRoot -Parent) 'Portable'), [switch]$Public)
# The repository root: this script lives in scripts\.
$root = Split-Path $PSScriptRoot -Parent
$ErrorActionPreference = 'Stop'
# Build in a fresh workspace; never mix previous test output into the delivery.
# Resolve against the PowerShell location, which can differ from the process directory.
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
$projectRoot = [IO.Path]::GetFullPath($root).TrimEnd('\')
if ($destination.TrimEnd('\') -eq $projectRoot -or $projectRoot.StartsWith($destination.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Output cannot be the project directory or one of its ancestors.'
}
# An existing folder is moved aside and replaced, so it must be an earlier package or empty.
if ((Test-Path -LiteralPath $destination -PathType Container) -and @(Get-ChildItem -LiteralPath $destination -Force).Count -gt 0) {
    $isPackage = (Test-Path -LiteralPath (Join-Path $destination 'manifest.sha256.csv')) -and (Test-Path -LiteralPath (Join-Path $destination 'SSE Animation Previewer.exe'))
    if (!$isPackage) { throw "Output exists and is not an earlier package, so it will not be replaced: $destination" }
}
$buildRoot = Join-Path $root ('verification/package-builds/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
$Output = Join-Path $buildRoot 'staging'
# Development-only build. The resulting Portable folder requires no SDK or converter.
# A property passed on the command line overrides the project file, so it is only passed for the public build.
# The public build also leaves out debug symbols, which carry the build machine's folder paths.
$floor = @(if ($Public) { '-p:FloorTextures=resources'; '-p:DebugType=none'; '-p:DebugSymbols=false'; '-p:GenerateDocumentationFile=false' })
dotnet publish (Join-Path $root 'src/SSEAnimationPreviewer.csproj') -c Release -r win-x64 --self-contained true -o $Output @floor
if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
# The C++ runtime DLLs live in the repository, so packaging needs no Visual Studio install.
$crt = Join-Path $root 'lib/crt'
if (!(Test-Path -LiteralPath $crt -PathType Container)) { throw "C++ runtime folder is missing: $crt. Restore lib/crt from the repository." }
$crtFiles = @(Get-ChildItem -LiteralPath $crt -Filter '*.dll' -File)
if (!$crtFiles.Count) { throw "C++ runtime folder holds no DLLs: $crt. Restore lib/crt from the repository." }
Copy-Item -LiteralPath $crtFiles.FullName -Destination $Output
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $Output
$sample = Join-Path $Output 'Samples/ParticleFixtures'
New-Item -ItemType Directory -Path $sample -Force | Out-Null
Copy-Item -Path (Join-Path $root 'particle-fixtures/*') -Destination $sample -Recurse -Force
foreach ($file in 'LICENSE','THIRD-PARTY-NOTICES.md') {
    Copy-Item -LiteralPath (Join-Path $root $file) -Destination $Output -Force
}
foreach ($directory in 'licenses','docs') {
    Copy-Item -LiteralPath (Join-Path $root $directory) -Destination $Output -Recurse -Force
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
    # The public package never carries scenes, presets or settings from an earlier folder.
    if (!$Public) { & (Join-Path $PSScriptRoot 'preserve-portable-data.ps1') -Previous $backup -Staging $Output }
    & (Join-Path $PSScriptRoot 'verify-portable.ps1') -Package $Output
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Move-Item -LiteralPath $Output -Destination $destination
} catch {
    if (!(Test-Path -LiteralPath $destination) -and (Test-Path -LiteralPath $backup)) { Move-Item -LiteralPath $backup -Destination $destination }
    throw
}
Write-Host "Portable build ready: $destination"
if (Test-Path -LiteralPath $backup) { Write-Host "Previous folder preserved at: $backup" }

