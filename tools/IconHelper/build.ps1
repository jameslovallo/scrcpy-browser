# Builds the on-device icon helper (Main.java -> dex jar) used by Scrcpy Launcher.
# Requirements: a JDK (e.g. `winget install Microsoft.OpenJDK.21`) and r8.jar from Google Maven:
#   https://dl.google.com/android/maven2/com/android/tools/r8/<version>/r8-<version>.jar
param(
    [string]$R8Jar = "$env:LOCALAPPDATA\r8\r8.jar",
    [string]$Output = "$PSScriptRoot\..\..\src\ScrcpyLauncher\Assets\iconhelper.jar"
)
$ErrorActionPreference = 'Stop'

$javaBin = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME 'bin' } else {
    Get-ChildItem "$env:ProgramFiles\Microsoft" -Filter 'jdk-*' -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | Select-Object -First 1 | ForEach-Object { Join-Path $_.FullName 'bin' }
}
$javac = if ($javaBin -and (Test-Path "$javaBin\javac.exe")) { "$javaBin\javac.exe" } else { 'javac' }
$java = if ($javaBin -and (Test-Path "$javaBin\java.exe")) { "$javaBin\java.exe" } else { 'java' }

$work = Join-Path $env:TEMP "iconhelper-build"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$work\classes" | Out-Null

& $javac --release 8 -nowarn -d "$work\classes" "$PSScriptRoot\Main.java"
if ($LASTEXITCODE) { throw "javac failed" }

$classes = (Get-ChildItem "$work\classes" -Filter *.class).FullName
& $java -cp $R8Jar com.android.tools.r8.D8 --release --min-api 21 --output "$work\iconhelper.jar" $classes
if ($LASTEXITCODE) { throw "d8 failed" }

Copy-Item "$work\iconhelper.jar" $Output -Force
Remove-Item $work -Recurse -Force
Write-Host "Built $((Resolve-Path $Output).Path)"
