# Builds a portable, self-contained copy of Scrcpy Launcher into .\dist\<rid>.
# No installer or runtime prerequisites; copy the folder anywhere and run ScrcpyLauncher.exe.
# Optionally Authenticode-signs the launcher binaries after publishing (see the signing parameters).
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$Zip,

    # Signing: pass a certificate thumbprint (CurrentUser\My) or a PFX file. Omit both to skip signing.
    [string]$CertificateThumbprint,
    [string]$PfxPath,
    [string]$PfxPassword,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\ScrcpyLauncher\ScrcpyLauncher.csproj'
$output = Join-Path $PSScriptRoot "dist\$Runtime"
$platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }

function Get-SignToolPath {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    # signtool ships with the Windows SDK BuildTools package restored by the .NET build.
    $packageRoot = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools'
    $found = Get-ChildItem $packageRoot -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $found) { throw 'signtool.exe not found. Install the Windows SDK or restore Microsoft.Windows.SDK.BuildTools.' }
    return $found.FullName
}

if (Test-Path $output) { Remove-Item $output -Recurse -Force }

dotnet publish $project -c Release -r $Runtime -p:Platform=$platform -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

if ($CertificateThumbprint -or $PfxPath) {
    $signtool = Get-SignToolPath
    # These are the only unsigned binaries in the output; the bundled .NET/Windows App SDK files are Microsoft-signed.
    foreach ($name in @('ScrcpyLauncher.exe', 'ScrcpyLauncher.dll')) {
        $file = Join-Path $output $name
        if (-not (Test-Path $file)) { continue }

        $signArgs = @('sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', $TimestampUrl)
        if ($PfxPath) {
            $signArgs += @('/f', (Resolve-Path $PfxPath).Path)
            if ($PfxPassword) { $signArgs += @('/p', $PfxPassword) }
        }
        else {
            $signArgs += @('/sha1', $CertificateThumbprint)
        }
        $signArgs += $file

        & $signtool @signArgs
        if ($LASTEXITCODE -ne 0) { throw "signtool failed for $name ($LASTEXITCODE)" }
    }
    Write-Host "Signed: ScrcpyLauncher.exe, ScrcpyLauncher.dll"
}

if ($Zip) {
    $zipPath = Join-Path $PSScriptRoot "dist\ScrcpyLauncher-$Runtime.zip"
    Compress-Archive -Path "$output\*" -DestinationPath $zipPath -Force
    Write-Host "Created $zipPath"
}

Write-Host "Portable build: $output\ScrcpyLauncher.exe"
