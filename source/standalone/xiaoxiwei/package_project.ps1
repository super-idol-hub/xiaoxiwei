param(
    [string]$ProjectDirectory = '小曦薇'
)

$ErrorActionPreference = 'Stop'

$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = [IO.Path]::GetFullPath((Join-Path $workspace $ProjectDirectory))
$workspacePrefix = $workspace.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $project.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Project directory escaped the workspace.'
}
$projectPrefix = $project.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

function Ensure-Directory([string]$Path) {
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Reset-ProjectDirectory([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a directory outside the project: $fullPath"
    }
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    Ensure-Directory $fullPath
}

function Copy-DirectoryContents([string]$Source, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Missing source directory: $Source"
    }
    Ensure-Directory $Destination
    Get-ChildItem -Force -LiteralPath $Source | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
    }
}

$release = Join-Path $project 'releases\最新版'
$releaseSkins = Join-Path $release 'skins'
$source = Join-Path $project 'source'
$qa = Join-Path $project 'qa'
$history = Join-Path $project 'releases\历史版本'
Ensure-Directory $history
$previousV3 = Join-Path $release '小曦薇桌宠-v3.0-增强版.exe'
if (Test-Path -LiteralPath $previousV3 -PathType Leaf) {
    Copy-Item -LiteralPath $previousV3 -Destination (Join-Path $history '小曦薇桌宠-v3.0-增强版.exe') -Force
}
$previousV301 = Join-Path $release '小曦薇桌宠-v3.0.1-增强版.exe'
if (Test-Path -LiteralPath $previousV301 -PathType Leaf) {
    Copy-Item -LiteralPath $previousV301 -Destination (Join-Path $history '小曦薇桌宠-v3.0.1-增强版.exe') -Force
}
$previousV302 = Join-Path $release '小曦薇桌宠-v3.0.2-增强版.exe'
if (Test-Path -LiteralPath $previousV302 -PathType Leaf) {
    Copy-Item -LiteralPath $previousV302 -Destination (Join-Path $history '小曦薇桌宠-v3.0.2-增强版.exe') -Force
}
$previousV303 = Join-Path $release '小曦薇桌宠-v3.0.3-增强版.exe'
if (Test-Path -LiteralPath $previousV303 -PathType Leaf) {
    Copy-Item -LiteralPath $previousV303 -Destination (Join-Path $history '小曦薇桌宠-v3.0.3-增强版.exe') -Force
}
$previousV304NamedAsPet = Join-Path $release '小曦薇桌宠-v3.0.4-增强版.exe'
if (Test-Path -LiteralPath $previousV304NamedAsPet -PathType Leaf) {
    Copy-Item -LiteralPath $previousV304NamedAsPet -Destination (Join-Path $history '小曦薇桌宠-v3.0.4-增强版.exe') -Force
}
Reset-ProjectDirectory $release
Reset-ProjectDirectory $source
Reset-ProjectDirectory $qa
Ensure-Directory $releaseSkins

$builtExe = Join-Path $workspace 'outputs\xiaoxiwei-standalone-4k-v3\小曦薇.exe'
$releaseExe = Join-Path $release '小曦薇.exe'
Copy-Item -LiteralPath $builtExe -Destination $releaseExe -Force

foreach ($skinId in @('linan-princess', 'huang-chengzi')) {
    $skinSource = Join-Path $workspace ("outputs\xiaoxiwei-skins\{0}" -f $skinId)
    $skinTarget = Join-Path $releaseSkins $skinId
    Ensure-Directory $skinTarget
    Copy-Item -LiteralPath (Join-Path $skinSource 'skin.xml') -Destination $skinTarget -Force
    Copy-Item -LiteralPath (Join-Path $skinSource 'frames.zip') -Destination $skinTarget -Force

    $skinQaTarget = Join-Path $qa ("skins\{0}" -f $skinId)
    Copy-DirectoryContents (Join-Path $skinSource 'qa') $skinQaTarget
}

Copy-Item -LiteralPath (Join-Path $project 'README.md') -Destination (Join-Path $release 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $project 'docs\使用说明.md') -Destination (Join-Path $release '使用说明.md') -Force
Copy-Item -LiteralPath (Join-Path $project 'docs\皮肤包接口.md') -Destination (Join-Path $release '皮肤包接口.md') -Force

Copy-DirectoryContents (Join-Path $workspace 'standalone\xiaoxiwei') (Join-Path $source 'standalone\xiaoxiwei')
Copy-DirectoryContents (Join-Path $workspace 'work\xiaoxiwei') (Join-Path $source 'work\xiaoxiwei')
Copy-DirectoryContents (Join-Path $workspace 'outputs\xiaoxiwei-standalone-4k-v3') (Join-Path $qa 'built-in-and-runtime')

$materialNames = @(
    'codex-clipboard-7d242c4b-798f-4580-b3c1-b113f3afad52.png',
    'codex-clipboard-c2513740-38af-42dc-aeac-a9a09d4e77d6.png',
    'codex-clipboard-840005dd-8157-47f3-aeeb-294248aac64b.png',
    'codex-clipboard-668f3b85-a0e8-4ca1-86d2-7fd53fef47eb.png',
    'codex-clipboard-d106676d-00f7-463e-9c1b-d4d1b8729938.png',
    'codex-clipboard-04bd146a-57bd-4145-b2bc-e5fbfe87bb67.png',
    'codex-clipboard-404f294a-937c-4b4c-85f1-5fa4768b8d0e.png',
    'codex-clipboard-c00a5126-bbf7-4998-a7af-7d6bae4aa176.png',
    'codex-clipboard-412428d3-8df4-48bf-ba53-4efdef991d61.png',
    'codex-clipboard-3656347c-a015-4620-9593-bd4806ed23b4.png',
    'codex-clipboard-6fbc6282-3f05-4aef-8fef-dfbd2a87a7e1.png',
    'codex-clipboard-8c4e4e11-c3a4-4038-904a-7d629db7996d.png',
    'codex-clipboard-79774f5e-3a6b-4548-9d59-ea3a6746fb8d.png',
    'codex-clipboard-79588ca4-033c-4913-ab6d-a7915c8bf633.png',
    'codex-clipboard-1e97eb59-c3a3-48ba-954a-f35641d33960.png',
    'codex-clipboard-69bd8589-96fa-4d2f-8640-428a1efce452.png',
    'codex-clipboard-d6bd4ca0-eaab-4ac0-b24a-21e2fc230842.png',
    'codex-clipboard-86c815d9-ede8-41ff-aae1-72c0cb881e3f.png',
    'codex-clipboard-a3f27748-cfb1-4525-a2a4-6df0641e081e.png',
    'codex-clipboard-84d6a2ba-2099-44dc-b98e-f1aab57bc8c1.png',
    'codex-clipboard-01722016-8e36-4071-8efd-805391df90e1.png',
    'codex-clipboard-8f716835-36e2-49e1-8544-6e15f218c344.png',
    'codex-clipboard-eefe3ec7-ae45-4615-95e4-93e0e9837a60.png',
    'codex-clipboard-f24d16c9-af96-4672-a637-2eff55289332.png'
)
$materialSource = Join-Path $env:LOCALAPPDATA 'Temp'
$materialTarget = Join-Path $source 'user-materials'
Ensure-Directory $materialTarget
$missingMaterials = @()
foreach ($name in $materialNames) {
    $path = Join-Path $materialSource $name
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        Copy-Item -LiteralPath $path -Destination $materialTarget -Force
    } else {
        $missingMaterials += $name
    }
}

$hashPath = Join-Path $release 'SHA256SUMS.txt'
$releaseFiles = Get-ChildItem -LiteralPath $release -Recurse -File |
    Where-Object { -not $_.FullName.Equals($hashPath, [StringComparison]::OrdinalIgnoreCase) } |
    Sort-Object FullName
$hashLines = foreach ($file in $releaseFiles) {
    $relative = $file.FullName.Substring($release.Length).TrimStart('\')
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    "{0}  {1}" -f $hash, $relative
}
[IO.File]::WriteAllLines($hashPath, $hashLines, [Text.UTF8Encoding]::new($false))

$releaseArchive = Join-Path (Split-Path -Parent $release) '小曦薇-完整包.zip'
$releaseArchive = [IO.Path]::GetFullPath($releaseArchive)
if (-not $releaseArchive.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release archive escaped the project: $releaseArchive"
}
if (Test-Path -LiteralPath $releaseArchive) {
    Remove-Item -LiteralPath $releaseArchive -Force
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory(
    $release,
    $releaseArchive,
    [IO.Compression.CompressionLevel]::Optimal,
    $false
)

$historicalRelativePaths = @(
    'releases\历史版本\小曦薇桌宠-v1.0-标准版.exe',
    'releases\历史版本\小曦薇桌宠-v2.0-4K版.exe',
    'releases\历史版本\小曦薇桌宠-v3.0-增强版.exe',
    'releases\历史版本\小曦薇桌宠-v3.0.1-增强版.exe',
    'releases\历史版本\小曦薇桌宠-v3.0.2-增强版.exe',
    'releases\历史版本\小曦薇桌宠-v3.0.3-增强版.exe',
    'releases\历史版本\小曦薇桌宠-v3.0.4-增强版.exe'
)
$historicalExecutables = foreach ($relativePath in $historicalRelativePaths) {
    $historicalPath = Join-Path $project $relativePath
    if (-not (Test-Path -LiteralPath $historicalPath -PathType Leaf)) {
        throw "Missing historical executable: $historicalPath"
    }
    [ordered]@{
        path = $relativePath
        bytes = (Get-Item -LiteralPath $historicalPath).Length
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $historicalPath).Hash.ToLowerInvariant()
    }
}

$summary = [ordered]@{
    schemaVersion = 1
    packagedAt = [DateTimeOffset]::Now.ToString('o')
    developer = 'Anbunengsi'
    executable = [ordered]@{
        path = 'releases\最新版\小曦薇.exe'
        bytes = (Get-Item -LiteralPath $releaseExe).Length
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $releaseExe).Hash.ToLowerInvariant()
    }
    releaseArchive = [ordered]@{
        path = 'releases\小曦薇-完整包.zip'
        bytes = (Get-Item -LiteralPath $releaseArchive).Length
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $releaseArchive).Hash.ToLowerInvariant()
    }
    skins = @('linan-princess', 'huang-chengzi')
    historicalExecutables = @($historicalExecutables)
    copiedUserMaterials = $materialNames.Count - $missingMaterials.Count
    missingUserMaterials = $missingMaterials
}
$summaryPath = Join-Path $project 'PROJECT-MANIFEST.json'
[IO.File]::WriteAllText(
    $summaryPath,
    (($summary | ConvertTo-Json -Depth 6) + "`n"),
    [Text.UTF8Encoding]::new($false)
)

[pscustomobject]@{
    Project = $project
    ReleaseExecutable = $releaseExe
    ReleaseArchive = $releaseArchive
    ReleaseFiles = $releaseFiles.Count + 1
    CopiedUserMaterials = $materialNames.Count - $missingMaterials.Count
    MissingUserMaterials = $missingMaterials.Count
}
