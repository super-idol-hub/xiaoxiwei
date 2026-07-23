param(
    [Parameter(Mandatory = $true)][string]$SkinId,
    [Parameter(Mandatory = $true)][string]$SkinName,
    [Parameter(Mandatory = $true)][string]$DecodedDir,
    [Parameter(Mandatory = $true)][string]$ExclusiveAction,
    [string]$Developer = 'Anbunensi',
    [string]$PythonPath = '',
    [string]$SkillDirectory = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($PythonPath)) {
    $PythonPath = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
}
if ([string]::IsNullOrWhiteSpace($SkillDirectory)) {
    $codexHome = if ([string]::IsNullOrWhiteSpace($env:CODEX_HOME)) {
        Join-Path $env:USERPROFILE '.codex'
    } else {
        $env:CODEX_HOME
    }
    $SkillDirectory = Join-Path $codexHome 'skills\hatch-pet'
}
$python = $PythonPath
$skill = $SkillDirectory
$project = $PSScriptRoot
$workspace = (Resolve-Path (Join-Path $project '..\..')).Path
$run = Join-Path $workspace 'work\xiaoxiwei\realistic-run'
$decoded = (Resolve-Path $DecodedDir).Path
$skinWork = Join-Path $workspace ("work\xiaoxiwei\skins\{0}\rebuilt" -f $SkinId)
$skinQa = Join-Path $workspace ("outputs\xiaoxiwei-skins\{0}\qa" -f $SkinId)
$skinOutput = Join-Path $workspace 'outputs\xiaoxiwei-skins'
$temporaryArchive = Join-Path $skinWork 'embedded-layout.zip'
$frameReport = Join-Path $skinQa 'frame-build-report.json'
$comparison = Join-Path $skinQa 'quality-comparison.png'
$packReport = Join-Path $skinQa 'skin-pack-report.json'
$frameBuilder = Join-Path $project 'build_hd_frames.py'
$motionBuilder = Join-Path $project 'build_motion_fields.py'
$skinPacker = Join-Path $project 'pack_external_skin.py'
$motionRoot = Join-Path $skinWork 'motion'
$motionReport = Join-Path $skinQa 'motion-build-report.json'
$motionContact = Join-Path $skinQa 'motion-ghost-free-contact.png'
$isLinanSwingSkin = $SkinId.Equals('linan-princess', [StringComparison]::OrdinalIgnoreCase)
$frameActionProfile = if ($isLinanSwingSkin) { 'external-v306-linan' } else { 'external-v306' }

foreach ($required in @($python, $frameBuilder, $motionBuilder, $skinPacker, $decoded)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required path not found: $required" }
}

New-Item -ItemType Directory -Force -Path $skinWork, $skinQa, $skinOutput | Out-Null

& $python $frameBuilder `
  --run-dir $run `
  --decoded-dir $decoded `
  --external-skin `
  --legacy-from-walk `
  --action-profile $frameActionProfile `
  --skill-dir $skill `
  --output-dir $skinWork `
  --archive $temporaryArchive `
  --report $frameReport `
  --comparison $comparison

if ($LASTEXITCODE -ne 0) { throw "Skin frame recovery failed with exit code $LASTEXITCODE" }

$motionArguments = @(
  $motionBuilder,
  '--frames-root', (Join-Path $skinWork 'frames'),
  '--motion-root', $motionRoot,
  '--report', $motionReport,
  '--qa-contact', $motionContact
)
if ($isLinanSwingSkin) { $motionArguments += '--include-linan-swing-exit' }
& $python @motionArguments

if ($LASTEXITCODE -ne 0) { throw "Skin motion mesh build failed with exit code $LASTEXITCODE" }

$packArguments = @(
  $skinPacker,
  '--frames-root', (Join-Path $skinWork 'frames'),
  '--motion-root', $motionRoot,
  '--output-root', $skinOutput,
  '--id', $SkinId,
  '--name', $SkinName,
  '--developer', $Developer,
  '--exclusive-action', $ExclusiveAction,
  '--report', $packReport
)
if ($isLinanSwingSkin) { $packArguments += '--include-linan-swing-exit' }
& $python @packArguments

if ($LASTEXITCODE -ne 0) { throw "Skin packaging failed with exit code $LASTEXITCODE" }

Get-Item -LiteralPath (Join-Path $skinOutput "$SkinId\skin.xml"), (Join-Path $skinOutput "$SkinId\frames.zip")
