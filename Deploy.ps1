param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "Publish",
    [switch]$SkipBuild,
    [switch]$SkipHash,
    [switch]$CreateRelease,
    [string]$ReleaseTag,
    [string]$ReleaseName
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$OutputPath = Join-Path $RepoRoot $OutputDir
$ZipName = "KITLUGIA2.zip"
$ZipPath = Join-Path $OutputPath $ZipName
$HashPath = "$ZipPath.sha256"

Write-Host "=== KitLugia Deploy Script ===" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration"
Write-Host "Output: $OutputPath"

Write-Host "`n[1/5] Preparing output directory..." -ForegroundColor Yellow
if (Test-Path $OutputPath) {
    Remove-Item -Path $OutputPath -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null

if (-not $SkipBuild) {
    Write-Host "`n[2/5] Building solution..." -ForegroundColor Yellow
    & dotnet build "$RepoRoot\KitLugia.sln" -c $Configuration -nologo
    if (-not $?) { throw "Build failed" }

    Write-Host "`n[3/5] Publishing Updater (single-file)..." -ForegroundColor Yellow
    $updaterOut = Join-Path (Join-Path $RepoRoot $OutputDir) "Updater"
    & dotnet publish "$RepoRoot\KitLugia.Updater\KitLugia.Updater.csproj" `
        -c $Configuration `
        -o $updaterOut `
        --nologo `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none `
        -p:DebugSymbols=false
    if (-not $?) { throw "Updater publish failed" }
}
else {
    Write-Host "`n[2-3/5] Skipped (SkipBuild)" -ForegroundColor Magenta
}

Write-Host "`n[4/5] Assembling release package..." -ForegroundColor Yellow

$updaterExe = Join-Path (Join-Path $OutputPath "Updater") "KitLugia.Updater.exe"
if (Test-Path $updaterExe) {
    Copy-Item $updaterExe (Join-Path $OutputPath "KitLugia.Updater.exe")
    Write-Host "  Copied KitLugia.Updater.exe"
}
else {
    Write-Warning "KitLugia.Updater.exe not found - publishing fallback..."
    & dotnet publish "$RepoRoot\KitLugia.Updater\KitLugia.Updater.csproj" `
        -c $Configuration `
        -o $OutputPath `
        --nologo `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none `
        -p:DebugSymbols=false
}

$guiOut = Join-Path $RepoRoot "KitLugia.GUI\bin\$Configuration\net10.0-windows10.0.26100.0"
if (Test-Path $guiOut) {
    Get-ChildItem -Path $guiOut -File | Where-Object {
        $_.Name -ne "KitLugia.Updater.exe"
    } | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $OutputPath $_.Name)
    }
    Write-Host "  Copied GUI files from $guiOut"
}
else {
    throw "GUI output not found at $guiOut. Build the solution first."
}

$coreOut = Join-Path $RepoRoot "KitLugia.Core\bin\$Configuration\net10.0-windows10.0.26100.0"
if (Test-Path $coreOut) {
    Get-ChildItem -Path $coreOut -File | ForEach-Object {
        Copy-Item $_.FullName (Join-Path $OutputPath $_.Name)
    }
    Write-Host "  Copied Core files from $coreOut"
}

Write-Host "`nCreating ZIP: $ZipPath..." -ForegroundColor Yellow
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($OutputPath, $ZipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

if (-not $SkipHash) {
    Write-Host "`n[5/5] Computing SHA256..." -ForegroundColor Yellow
    $hash = (Get-FileHash -Path $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path $HashPath -Value $hash -NoNewline
    Write-Host "  SHA256: $hash"
    Write-Host "  Saved to: $HashPath"
}

Write-Host "`n=== Done ===" -ForegroundColor Green
Write-Host "Package: $ZipPath"
Write-Host "Size: $([math]::Round((Get-Item $ZipPath).Length / 1MB, 2)) MB"

if ($CreateRelease) {
    if (-not $ReleaseTag) { $ReleaseTag = "v$(Get-Date -Format 'yyyy.MM.dd')" }
    if (-not $ReleaseName) { $ReleaseName = "KitLugia $ReleaseTag" }
    Write-Host "`nCreating GitHub release..." -ForegroundColor Yellow
    $assets = @("""$ZipPath""")
    if (Test-Path $HashPath) { $assets += """$HashPath""" }
    & gh release create $ReleaseTag --title "$ReleaseName" --notes "Automated release" $assets
    if ($?) {
        Write-Host "  Release $ReleaseTag created!" -ForegroundColor Green
    }
    else {
        Write-Warning "gh release create failed. Ensure GitHub CLI is authenticated."
    }
}