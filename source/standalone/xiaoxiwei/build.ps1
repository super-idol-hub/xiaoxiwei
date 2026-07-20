param(
    [string]$PythonPath = '',
    [string]$SkillDirectory = ''
)

$ErrorActionPreference = 'Stop'

$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
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
$frameWork = Join-Path $workspace 'work\xiaoxiwei\standalone-4k'
$archive = Join-Path $project 'frames-4k.zip'
$frameBuilder = Join-Path $project 'build_hd_frames.py'
$motionBuilder = Join-Path $project 'build_motion_fields.py'
$motionRoot = Join-Path $frameWork 'motion'
$icon = Join-Path $project 'xiaoxiwei.ico'
$manifest = Join-Path $project 'app.manifest'
$source = Join-Path $project 'XiaoXiWeiPet.cs'
$outputDirectory = Join-Path $workspace 'outputs\xiaoxiwei-standalone-4k-v3'
$motionReport = Join-Path $outputDirectory 'motion-build-report.json'
$motionContact = Join-Path $outputDirectory 'motion-ghost-free-contact.png'
$frameReport = Join-Path $outputDirectory 'frame-build-report.json'
$comparison = Join-Path $outputDirectory 'quality-comparison.png'
$output = Join-Path $outputDirectory '小曦薇.exe'

if (-not (Test-Path -LiteralPath $compiler)) { throw "C# compiler not found: $compiler" }
if (-not (Test-Path -LiteralPath $python)) { throw "Bundled Python not found: $python" }
if (-not (Test-Path -LiteralPath $frameBuilder)) { throw "Frame builder not found: $frameBuilder" }
if (-not (Test-Path -LiteralPath $motionBuilder)) { throw "Motion builder not found: $motionBuilder" }
if (-not (Test-Path -LiteralPath $icon)) { throw "Icon not found: $icon" }
if (-not (Test-Path -LiteralPath (Join-Path $run 'decoded\angry-stomp.png'))) { throw 'Angry-stomp source row is missing.' }

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

& $python $frameBuilder `
  --run-dir $run `
  --action-profile built-in-v304 `
  --skill-dir $skill `
  --output-dir $frameWork `
  --archive $archive `
  --report $frameReport `
  --comparison $comparison

if ($LASTEXITCODE -ne 0) { throw "4K frame recovery failed with exit code $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $archive)) { throw "4K frame archive not found: $archive" }

& $python $motionBuilder `
  --frames-root (Join-Path $frameWork 'frames') `
  --motion-root $motionRoot `
  --report $motionReport `
  --qa-contact $motionContact `
  --archive $archive

if ($LASTEXITCODE -ne 0) { throw "Motion mesh build failed with exit code $LASTEXITCODE" }

& $compiler `
  /nologo `
  /target:winexe `
  /optimize+ `
  /debug- `
  /platform:anycpu `
  /langversion:5 `
  /codepage:65001 `
  /reference:System.dll `
  /reference:System.Core.dll `
  /reference:System.Drawing.dll `
  /reference:System.IO.Compression.dll `
  /reference:System.Windows.Forms.dll `
  "/resource:$archive,XiaoXiWei.Standalone.Frames.zip" `
  "/win32icon:$icon" `
  "/win32manifest:$manifest" `
  "/out:$output" `
  $source

if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }
Get-Item -LiteralPath $output
