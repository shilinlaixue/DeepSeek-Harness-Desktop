#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Build a self-contained Windows DeepSeek Desktop package.

.DESCRIPTION
  1. downloads a pinned Node.js win-x64 runtime
  2. installs the pinned @deepseek-ai/dsh bundle (with Windows native deps)
  3. dotnet publish (self-contained, no .NET install required on the user machine)
  4. runs the real bundled runtime through DeepSeek.exe --self-test
  5. writes DeepSeek-<version>-win-x64.zip + SHA256

.PARAMETER SkipRuntime
  Reuse .build/runtime if it is already assembled.

.PARAMETER SkipSelfTest
  Skip the end-to-end smoke test (not recommended).

.PARAMETER SkipZip
  Stop after publish/smoke test.
#>
param(
    [string]$Version = "1.1.0",
    [string]$NodeVersion = "24.21.0",
    [string]$DshVersion = "0.2.1-rc.2",
    [switch]$SkipRuntime,
    [switch]$SkipSelfTest,
    [switch]$SkipZip
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$Root = Split-Path -Parent $PSScriptRoot
$Build = Join-Path $Root ".build"
$Cache = Join-Path $Build "cache"
$Runtime = Join-Path $Build "runtime"
$Bundle = Join-Path $Runtime "bundle"
$Publish = Join-Path $Build "publish"
$NodeDir = Join-Path $Runtime "node"
$NodeExe = Join-Path $NodeDir "node.exe"
$Npm = Join-Path $NodeDir "npm.cmd"
$Dist = Join-Path $Root "dist"

New-Item -ItemType Directory -Force -Path $Build, $Cache, $Runtime, $Dist | Out-Null

function Get-File([string]$Url, [string]$OutFile) {
    if (Test-Path $OutFile) { return }
    Write-Host "==> download $Url"
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        & $curl.Source -L --fail --retry 3 --output $OutFile $Url
        if ($LASTEXITCODE -ne 0) { throw "curl failed: $Url" }
    } else {
        Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing
    }
}

if (-not $SkipRuntime) {
    if (-not (Test-Path $NodeExe)) {
        $zip = Join-Path $Cache "node-v$NodeVersion-win-x64.zip"
        Get-File "https://nodejs.org/dist/v$NodeVersion/node-v$NodeVersion-win-x64.zip" $zip
        $extract = Join-Path $Cache "node-$NodeVersion"
        if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
        Expand-Archive -Path $zip -DestinationPath $extract -Force
        Remove-Item $NodeDir -Recurse -Force -ErrorAction SilentlyContinue
        Move-Item (Join-Path $extract "node-v$NodeVersion-win-x64") $NodeDir
    }

    $dshBin = Join-Path $Bundle "node_modules/@deepseek-ai/dsh/lib/bin.js"
    if (-not (Test-Path $dshBin)) {
        New-Item -ItemType Directory -Force -Path $Bundle | Out-Null
        if (-not (Test-Path (Join-Path $Bundle "package.json"))) {
            Push-Location $Bundle
            try { npm init -y | Out-Null } finally { Pop-Location }
        }
        # --ignore-scripts: all native packages used by dsh ship win-x64 prebuilds;
        # skipping lifecycle scripts also keeps CI hermetic and fast.
        & $Npm install --prefix $Bundle --omit=dev --ignore-scripts --no-audit --no-fund `
            "@deepseek-ai/dsh@$DshVersion"
        if ($LASTEXITCODE -ne 0) { throw "npm install @deepseek-ai/dsh@$DshVersion failed" }
    }
    if (-not (Test-Path (Join-Path $Bundle "node_modules/@deepseek-ai/dsh/lib/bin.js"))) {
        throw "dsh bundle is incomplete"
    }
}

Write-Host "==> dotnet publish (self-contained win-x64)"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
& dotnet publish (Join-Path $Root "windows") -c Release -o $Publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "==> assemble runtime"
$PublishRuntime = Join-Path $Publish "runtime"
New-Item -ItemType Directory -Force -Path $PublishRuntime | Out-Null
Copy-Item $NodeExe (Join-Path $PublishRuntime "node.exe") -Force
if (Test-Path (Join-Path $PublishRuntime "bundle")) {
    Remove-Item (Join-Path $PublishRuntime "bundle") -Recurse -Force
}
Copy-Item $Bundle (Join-Path $PublishRuntime "bundle") -Recurse
Copy-Item (Join-Path $Root "windows/assets/whale.png") (Join-Path $Publish "whale.png") -Force
Get-ChildItem $Publish -Filter *.pdb -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

if (-not $SkipSelfTest) {
    Write-Host "==> self-test (real Node + dsh + token URL)"
    $testData = Join-Path $Build "selftest-data"
    $testOut = Join-Path $Build "selftest.txt"
    Remove-Item $testData -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $testOut -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $testData | Out-Null
    $testHome = Join-Path $testData "home"
    New-Item -ItemType Directory -Force -Path $testHome | Out-Null
    $env:DSH_HOME = $testHome
    $env:DSH_DESKTOP_DATA = $testData
    $env:DSH_DESKTOP_SELFTEST_OUT = $testOut
    $env:DSH_DESKTOP_PORT = "0"
    & (Join-Path $Publish "DeepSeek.exe") --self-test
    $deadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $deadline -and -not (Test-Path $testOut)) {
        Start-Sleep -Milliseconds 500
    }
    if (-not (Test-Path $testOut)) { throw "self-test produced no result (see $testData\server.log)" }
    $result = Get-Content $testOut -Raw
    if (-not $result.StartsWith("OK ")) { throw "self-test failed: $result" }
    Write-Host "    $result"
}

if (-not $SkipZip) {
    Write-Host "==> package"
    $name = "DeepSeek-$Version-win-x64"
    $zip = Join-Path $Dist "$name.zip"
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    if (Get-Command tar.exe -ErrorAction SilentlyContinue) {
        & tar.exe -a -c -f $zip -C $Publish .
        if ($LASTEXITCODE -ne 0) { throw "tar failed" }
    } else {
        Compress-Archive -Path (Join-Path $Publish "*") -DestinationPath $zip -Force
    }
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content (Join-Path $Dist "$name.zip.sha256") $hash -Encoding ascii
    Get-Item $zip | Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }, FullName
}
