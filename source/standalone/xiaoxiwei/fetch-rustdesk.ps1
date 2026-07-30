param(
    [string]$DestinationDirectory = ''
)

$ErrorActionPreference = 'Stop'

$version = '1.4.9'
$expectedSha256 =
    'EAEDEB0088E687BF46F7C46A9C6EA5493CE51F3134DFD6ACBEDB47B5B9136274'
$project = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($DestinationDirectory)) {
    $DestinationDirectory = Join-Path $project 'vendor\rustdesk'
}
$DestinationDirectory = [IO.Path]::GetFullPath($DestinationDirectory)
$executable = Join-Path $DestinationDirectory "rustdesk-$version-x86_64.exe"
$license = Join-Path $DestinationDirectory 'LICENCE-RustDesk.txt'

New-Item -ItemType Directory -Force -Path $DestinationDirectory | Out-Null

Invoke-WebRequest `
    -Uri "https://github.com/rustdesk/rustdesk/releases/download/$version/rustdesk-$version-x86_64.exe" `
    -OutFile $executable
Invoke-WebRequest `
    -Uri "https://raw.githubusercontent.com/rustdesk/rustdesk/$version/LICENCE" `
    -OutFile $license

$actualSha256 = (Get-FileHash -Algorithm SHA256 $executable).Hash
if ($actualSha256 -ne $expectedSha256) {
    Remove-Item -LiteralPath $executable
    throw "RustDesk SHA-256 mismatch. Expected $expectedSha256, got $actualSha256."
}

$signature = Get-AuthenticodeSignature $executable
if (
    $null -eq $signature.SignerCertificate -or
    $signature.Status -eq [System.Management.Automation.SignatureStatus]::HashMismatch -or
    $signature.Status -eq [System.Management.Automation.SignatureStatus]::NotSigned
) {
    Remove-Item -LiteralPath $executable
    throw "RustDesk Authenticode signature is not valid: $($signature.Status)"
}
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    Write-Warning (
        "RustDesk signature chain could not be fully validated ($($signature.Status)); "
        + "the pinned SHA-256 and embedded signer certificate are valid."
    )
}

Get-Item -LiteralPath $executable, $license
