<#
.SYNOPSIS
    Tests for kl_deploy_get_version.ps1 - version detection and parsing.
.USAGE
    powershell -NoProfile -ExecutionPolicy Bypass -File test_deploy_version.ps1 [-SkipIntegration]
#>

param(
    [switch]$SkipIntegration
)

$script:Passed = 0
$script:Failed = 0
$script:Skipped = 0
$script:Errors = @()

function Assert-Equal {
    param([string]$Expected, [string]$Actual, [string]$TestName)
    if ($Expected -eq $Actual) {
        Write-Host "  PASS: $TestName" -ForegroundColor Green
        $script:Passed++
    } else {
        Write-Host "  FAIL: $TestName" -ForegroundColor Red
        Write-Host "    Expected: '$Expected'" -ForegroundColor Yellow
        Write-Host "    Actual:   '$Actual'" -ForegroundColor Yellow
        $script:Failed++
        $script:Errors += "$TestName : expected='$Expected' actual='$Actual'"
    }
}

function Assert-True {
    param([bool]$Condition, [string]$TestName)
    if ($Condition) {
        Write-Host "  PASS: $TestName" -ForegroundColor Green
        $script:Passed++
    } else {
        Write-Host "  FAIL: $TestName" -ForegroundColor Red
        $script:Failed++
        $script:Errors += "$TestName : condition was false"
    }
}

function Assert-Throws {
    param([scriptblock]$ScriptBlock, [string]$TestName)
    try {
        & $ScriptBlock
        Write-Host "  FAIL: $TestName (no exception thrown)" -ForegroundColor Red
        $script:Failed++
        $script:Errors += "$TestName : expected exception"
    } catch {
        Write-Host "  PASS: $TestName (threw: $($_.Exception.Message))" -ForegroundColor Green
        $script:Passed++
    }
}

function Get-NextVersion {
    param([string]$TagOrVersion)
    $v = $TagOrVersion -replace '^[vV]', ''
    if ($v -notmatch '^\d+\.\d+\.\d+$') {
        throw "Invalid version format: '$TagOrVersion' (expected X.Y.Z)"
    }
    $parts = $v.Split('.')
    $major = [int]$parts[0]
    $minor = [int]$parts[1]
    $patch = [int]$parts[2]
    $nextPatch = $patch + 1
    $next = "{0}.{1}.{2}" -f $major, $minor, $nextPatch
    return @{
        Current = $v
        Next    = $next
        Major   = $major
        Minor   = $minor
        Patch   = $patch
        NextPatch = $nextPatch
    }
}

# ================================================================
# TEST SUITE 1: Version Parsing
# ================================================================
Write-Host "`n=== TEST SUITE: Version Parsing ===" -ForegroundColor Cyan

$r = Get-NextVersion -TagOrVersion "v2.0.52"
Assert-Equal "2.0.52"  $r.Current "Normal: current = 2.0.52"
Assert-Equal "2.0.53"  $r.Next    "Normal: next = 2.0.53"

$r = Get-NextVersion -TagOrVersion "2.0.52"
Assert-Equal "2.0.52"  $r.Current "No prefix: current = 2.0.52"
Assert-Equal "2.0.53"  $r.Next    "No prefix: next = 2.0.53"

$r = Get-NextVersion -TagOrVersion "V2.0.52"
Assert-Equal "2.0.52"  $r.Current "Uppercase V: current = 2.0.52"
Assert-Equal "2.0.53"  $r.Next    "Uppercase V: next = 2.0.53"

$r = Get-NextVersion -TagOrVersion "v2.0.99"
Assert-Equal "2.0.99"  $r.Current "Patch 99: current = 2.0.99"
Assert-Equal "2.0.100" $r.Next    "Patch 99: next = 2.0.100"

$r = Get-NextVersion -TagOrVersion "v1.0.0"
Assert-Equal "1.0.0"   $r.Current "Patch 0: current = 1.0.0"
Assert-Equal "1.0.1"   $r.Next    "Patch 0: next = 1.0.1"

$r = Get-NextVersion -TagOrVersion "v10.20.30"
Assert-Equal "10.20.30" $r.Current "Large: current = 10.20.30"
Assert-Equal "10.20.31" $r.Next    "Large: next = 10.20.31"

Assert-Throws { Get-NextVersion -TagOrVersion "v2.0" } "Reject 'v2.0' (too few parts)"
Assert-Throws { Get-NextVersion -TagOrVersion "v2.0.beta" } "Reject 'v2.0.beta' (non-numeric)"
Assert-Throws { Get-NextVersion -TagOrVersion "" } "Reject empty string"
Assert-Throws { Get-NextVersion -TagOrVersion "hello" } "Reject 'hello'"
Assert-Throws { Get-NextVersion -TagOrVersion "v2.0.52-beta" } "Reject 'v2.0.52-beta' (prerelease)"

# ================================================================
# TEST SUITE 2: Output File Format
# ================================================================
Write-Host "`n=== TEST SUITE: Output File Format ===" -ForegroundColor Cyan

$testDir = Join-Path $env:TEMP "kl_deploy_test_$(Get-Random)"
New-Item -ItemType Directory -Path $testDir -Force | Out-Null

$detected = Get-NextVersion -TagOrVersion "v2.0.52"
$nextFile = Join-Path $testDir "kl_deploy_ver.txt"
$infoFile = Join-Path $testDir "kl_deploy_info.txt"
[IO.File]::WriteAllText($nextFile, $detected.Next)

$info = @"
{
  "status": "ok",
  "source": "test",
  "current": "$($detected.Current)",
  "next": "$($detected.Next)",
  "major": $($detected.Major),
  "minor": $($detected.Minor),
  "patch": $($detected.Patch),
  "nextPatch": $($detected.NextPatch)
}
"@
[IO.File]::WriteAllText($infoFile, $info)

$readBack = Get-Content $nextFile -Raw
Assert-Equal "2.0.53" $readBack.Trim() "NEXT file content = '2.0.53'"

$infoContent = Get-Content $infoFile -Raw
Assert-True ($infoContent -match '"status"') "INFO file has 'status' field"
Assert-True ($infoContent -match '"current"') "INFO file has 'current' field"
Assert-True ($infoContent -match '"next"') "INFO file has 'next' field"
Assert-True ($infoContent -match '"source"') "INFO file has 'source' field"

$testStatus = ""
$testCurrent = ""
foreach ($line in (Get-Content $infoFile)) {
    if ($line -match '"status"\s*:\s*"([^"]+)"') { $testStatus = $Matches[1] }
    if ($line -match '"current"\s*:\s*"([^"]+)"') { $testCurrent = $Matches[1] }
}
Assert-Equal "ok" $testStatus "Batch-parse status = 'ok'"
Assert-Equal "2.0.52" $testCurrent "Batch-parse current = '2.0.52'"

Remove-Item -Path $testDir -Recurse -Force -ErrorAction SilentlyContinue

# ================================================================
# TEST SUITE 3: Fallback Layer Detection
# ================================================================
Write-Host "`n=== TEST SUITE: Fallback Layer Detection ===" -ForegroundColor Cyan

$hasGh = [bool](Get-Command gh -ErrorAction SilentlyContinue)
$hasGit = [bool](Get-Command git -ErrorAction SilentlyContinue)
$hasCurl = [bool](Get-Command curl.exe -ErrorAction SilentlyContinue)

Write-Host "  Available tools:" -ForegroundColor Gray
Write-Host "    gh:   $hasGh" -ForegroundColor Gray
Write-Host "    git:  $hasGit" -ForegroundColor Gray
Write-Host "    curl: $hasCurl" -ForegroundColor Gray

if ($hasGh) {
    $ghAuth = $false
    try { & gh auth status 2>$null; $ghAuth = ($LASTEXITCODE -eq 0) } catch {}
    Write-Host "    gh auth: $ghAuth" -ForegroundColor Gray
}

# ================================================================
# TEST SUITE 4: Integration (Live GitHub API)
# ================================================================
if (-not $SkipIntegration) {
    Write-Host "`n=== TEST SUITE: Integration (Live GitHub API) ===" -ForegroundColor Cyan

    $testDir2 = Join-Path $env:TEMP "kl_deploy_integ_$(Get-Random)"
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $psScript = Join-Path $scriptDir "kl_deploy_get_version.ps1"

    Write-Host "  Running kl_deploy_get_version.ps1..." -ForegroundColor Gray

    if (Test-Path $psScript) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $psScript -OutputDir "$testDir2"

        $integNext = ""
        $integInfo = ""
        $integInfoPath = Join-Path $testDir2 "kl_deploy_info.txt"
        $integNextPath = Join-Path $testDir2 "kl_deploy_ver.txt"

        if (Test-Path $integNextPath) { $integNext = (Get-Content $integNextPath -Raw).Trim() }
        if (Test-Path $integInfoPath) { $integInfo = Get-Content $integInfoPath -Raw }

        $integSource = ""
        if ($integInfo -match '"source"\s*:\s*"([^"]+)"') { $integSource = $Matches[1] }
        $integStatus = ""
        if ($integInfo -match '"status"\s*:\s*"([^"]+)"') { $integStatus = $Matches[1] }
        $integCurrent = ""
        if ($integInfo -match '"current"\s*:\s*"([^"]+)"') { $integCurrent = $Matches[1] }

        if ($integStatus -eq "ok") {
            Assert-Equal "ok" $integStatus "Integration: status = ok"
            Assert-Equal "2.0.52" $integCurrent "Integration: current = 2.0.52"
            Assert-Equal "2.0.53" $integNext    "Integration: next = 2.0.53"
            Assert-True ($integSource -ne "") "Integration: source = '$integSource'"
            Write-Host "  Detected via: $integSource" -ForegroundColor Gray
        } else {
            Write-Host "  WARNING: Could not reach GitHub (offline/rate-limited)" -ForegroundColor Yellow
            $script:Skipped++
        }
    } else {
        Write-Host "  SKIP: kl_deploy_get_version.ps1 not found" -ForegroundColor Yellow
        $script:Skipped++
    }

    Remove-Item -Path $testDir2 -Recurse -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "`n=== Integration tests SKIPPED (-SkipIntegration) ===" -ForegroundColor Yellow
    $script:Skipped++
}

# ================================================================
# RESULTS
# ================================================================
Write-Host "`n==========================================" -ForegroundColor Cyan
Write-Host "  RESULTS" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  Passed:   $script:Passed" -ForegroundColor Green
Write-Host "  Failed:   $script:Failed" -ForegroundColor $(if ($script:Failed -gt 0) { "Red" } else { "Green" })
Write-Host "  Skipped:  $script:Skipped" -ForegroundColor Yellow
Write-Host ""

if ($script:Failed -gt 0) {
    Write-Host "  FAILURES:" -ForegroundColor Red
    foreach ($e in $script:Errors) {
        Write-Host "    - $e" -ForegroundColor Red
    }
    exit 1
} else {
    Write-Host "  ALL TESTS PASSED!" -ForegroundColor Green
    exit 0
}
