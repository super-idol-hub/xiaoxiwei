param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Destination
)

$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$destinationPath = [IO.Path]::GetFullPath((Join-Path $workspace $Destination))
$workspacePrefix = $workspace.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

if (-not $destinationPath.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Destination escaped the workspace.'
}
if (Test-Path -LiteralPath $destinationPath) {
    throw "Destination already exists: $destinationPath"
}

$destinationDirectory = Split-Path -Parent $destinationPath
New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
Copy-Item -LiteralPath $sourcePath -Destination $destinationPath
$sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash
$destinationHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $destinationPath).Hash
if ($sourceHash -ne $destinationHash) {
    Remove-Item -LiteralPath $destinationPath
    throw 'Hash mismatch after importing the generated asset.'
}

Remove-Item -LiteralPath $sourcePath
$generatedDirectory = Split-Path -Parent $sourcePath
if ((Get-ChildItem -Force -LiteralPath $generatedDirectory | Measure-Object).Count -eq 0) {
    Remove-Item -LiteralPath $generatedDirectory
}

[pscustomobject]@{
    Path = $destinationPath
    Bytes = (Get-Item -LiteralPath $destinationPath).Length
    SHA256 = $destinationHash
}
