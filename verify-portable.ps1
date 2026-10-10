param([string]$Package = (Join-Path $PSScriptRoot 'Portable'))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Package).TrimEnd('\') + '\'
$manifest = Join-Path $root 'manifest.sha256.csv'
$entries = @(Import-Csv -LiteralPath $manifest)
if (!$entries.Count) { throw 'Empty portable manifest.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $entries) {
    $relative = $entry.File
    if (!$relative -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or ($relative.Split([char[]]'/\') -contains '..')) { throw "Unsafe manifest path: $relative" }
    if (!$seen.Add($relative.Replace('\','/'))) { throw "Duplicate manifest path: $relative" }
    $path = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (!$path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "Manifest path escapes package: $relative" }
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing package file: $relative" }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.Hash) { throw "Package hash mismatch: $relative" }
}
foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
    $relative = $file.FullName.Substring($root.Length).Replace('\','/')
    if ($relative -in @('manifest.sha256.csv','session.json','recent-scenes.json','viewer-settings.json') -or $relative.StartsWith('Scenes/',[StringComparison]::OrdinalIgnoreCase) -or $relative.StartsWith('Presets/',[StringComparison]::OrdinalIgnoreCase)) { continue }
    if (!$seen.Contains($relative)) { throw "Unlisted package file: $relative" }
}
Write-Host "PASS portable manifest: $($entries.Count) files; all relative paths and hashes verified."

