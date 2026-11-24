# Builds a portable, self-contained copy of Scrcpy Launcher into .\dist\<rid>.
# No installer or runtime prerequisites; copy the folder anywhere and run ScrcpyLauncher.exe.
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\ScrcpyLauncher\ScrcpyLauncher.csproj'
$output = Join-Path $PSScriptRoot "dist\$Runtime"
$platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }

if (Test-Path $output) { Remove-Item $output -Recurse -Force }

dotnet publish $project -c Release -r $Runtime -p:Platform=$platform -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

if ($Zip) {
    $zipPath = Join-Path $PSScriptRoot "dist\ScrcpyLauncher-$Runtime.zip"
    Compress-Archive -Path "$output\*" -DestinationPath $zipPath -Force
    Write-Host "Created $zipPath"
}

Write-Host "Portable build: $output\ScrcpyLauncher.exe"
