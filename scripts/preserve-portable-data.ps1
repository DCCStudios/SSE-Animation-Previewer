param([Parameter(Mandatory)][string]$Previous,[Parameter(Mandatory)][string]$Staging)
$ErrorActionPreference='Stop'
if (!(Test-Path -LiteralPath $Previous)) { return }
$sourceRoot=[IO.Path]::GetFullPath($Previous).TrimEnd('\')+'\'
$targetRoot=[IO.Path]::GetFullPath($Staging).TrimEnd('\')+'\'
# These locations belong to the user. Existing files win over bundled examples.
$items=@()
foreach($name in 'Scenes','Presets') {
    $folder=Join-Path $sourceRoot $name
    if(Test-Path -LiteralPath $folder) {
        $nodes=@(Get-Item -LiteralPath $folder)+@(Get-ChildItem -LiteralPath $folder -Recurse -Force)
        foreach($node in $nodes) {
            if($node.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Cannot safely preserve linked user data: $($node.FullName)"}
        }
        $items+=$nodes
    }
}
foreach($name in 'session.json','recent-scenes.json','viewer-settings.json') {
    $file=Join-Path $sourceRoot $name
    if(Test-Path -LiteralPath $file -PathType Leaf){$items+=Get-Item -LiteralPath $file}
}
foreach($item in $items) {
    $relative=$item.FullName.Substring($sourceRoot.Length)
    $target=[IO.Path]::GetFullPath((Join-Path $targetRoot $relative))
    if(!$target.StartsWith($targetRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'User data target escaped staging directory.'}
    if($item.PSIsContainer){New-Item -ItemType Directory -Path $target -Force | Out-Null;continue}
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $item.FullName -Destination $target -Force
    if((Get-FileHash -LiteralPath $item.FullName).Hash -ne (Get-FileHash -LiteralPath $target).Hash){throw "User data verification failed: $relative"}
}
Write-Host 'Preserved user scenes, presets, and settings; copied files verified by hash.'
