<#
.SYNOPSIS
    Detects the latest KitLugia release version from GitHub and computes the next version.
.DESCRIPTION
    5 fallback layers:
      1. gh CLI (GitHub CLI) - fastest, most reliable
      2. PowerShell Invoke-RestMethod - native, no external deps
      3. curl + regex parse - works when PS web is blocked
      4. Local git tags - works offline
      5. Failure (caller handles manual input)

    Outputs:
      - NEXT_VERSION file: just the next version string (e.g. 2.0.53)
      - VERSION_INFO file:  JSON-like structured info for rich UI display
#>

param(
    [string]$RepoOwner = "luigiarrud4",
    [string]$RepoName = "KitLugia-AVTest",
    [string]$OutputDir = "$env:TEMP"
)

$ErrorActionPreference = 'Stop'

# --- Helper: parse "vX.Y.Z" or "X.Y.Z" into components and compute next ---
function Get-NextVersion {
    param([string]$TagOrVersion)

    # Strip leading v/V
    $v = $TagOrVersion -replace '^[vV]', ''

    # Validate format X.Y.Z (digits only)
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

# --- Output paths ---
$nextFile  = Join-Path $OutputDir "kl_deploy_ver.txt"
$infoFile  = Join-Path $OutputDir "kl_deploy_info.txt"

# Ensure output directory exists
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

# Clean previous outputs
Remove-Item -Path $nextFile -Force -ErrorAction SilentlyContinue
Remove-Item -Path $infoFile -Force -ErrorAction SilentlyContinue

$detected = $null
$source   = ""

# ============================================================
# FALLBACK 1: GitHub CLI (gh)
# ============================================================
try {
    $ghPath = Get-Command gh -ErrorAction SilentlyContinue
    if ($ghPath) {
        $ghOut = & gh release view --repo "$RepoOwner/$RepoName" --json tagName --jq '.tagName' 2>&1
        if ($LASTEXITCODE -eq 0 -and $ghOut -match 'v?\d+\.\d+\.\d+') {
            $detected = Get-NextVersion -TagOrVersion $ghOut
            $source = "gh-cli"
        }
    }
} catch {
    # Fall through to next fallback
}

# ============================================================
# FALLBACK 2: PowerShell Invoke-RestMethod
# ============================================================
if (-not $detected) {
    try {
        $headers = @{ "Accept" = "application/vnd.github+json" }
        $uri = "https://api.github.com/repos/$RepoOwner/$RepoName/releases/latest"
        $resp = Invoke-RestMethod -Uri $uri -UseBasicParsing -TimeoutSec 15 -Headers $headers
        if ($resp.tag_name) {
            $detected = Get-NextVersion -TagOrVersion $resp.tag_name
            $source = "invoke-restmethod"
        }
    } catch {
        # Fall through
    }
}

# ============================================================
# FALLBACK 3: curl (when PS web is restricted)
# ============================================================
if (-not $detected) {
    try {
        $curlPath = Get-Command curl.exe -ErrorAction SilentlyContinue
        if (-not $curlPath) { $curlPath = Get-Command curl -ErrorAction SilentlyContinue }
        if ($curlPath) {
            $uri = "https://api.github.com/repos/$RepoOwner/$RepoName/releases/latest"
            $curlOut = & curl.exe -s -L --max-time 15 "$uri" 2>&1
            if ($LASTEXITCODE -eq 0) {
                # Parse tag_name from JSON without ConvertFrom-Json
                if ($curlOut -match '"tag_name"\s*:\s*"([^"]+)"') {
                    $tagName = $Matches[1]
                    $detected = Get-NextVersion -TagOrVersion $tagName
                    $source = "curl"
                }
            }
        }
    } catch {
        # Fall through
    }
}

# ============================================================
# FALLBACK 4: Local git tags (offline)
# ============================================================
if (-not $detected) {
    try {
        $gitPath = Get-Command git -ErrorAction SilentlyContinue
        if ($gitPath) {
            $tags = & git tag --sort=-v:refname 2>&1
            if ($LASTEXITCODE -eq 0 -and $tags) {
                # Find first tag matching vX.Y.Z
                foreach ($tag in $tags) {
                    if ($tag -match '^v?\d+\.\d+\.\d+$') {
                        $detected = Get-NextVersion -TagOrVersion $tag
                        $source = "git-tag-offline"
                        break
                    }
                }
            }
        }
    } catch {
        # Fall through
    }
}

# ============================================================
# FALLBACK 5: Total failure - write error info
# ============================================================
if (-not $detected) {
    $info = @"
{
  "status": "error",
  "source": "none",
  "current": null,
  "next": null,
  "error": "All 4 fallbacks failed. Check: internet, gh auth, repo visibility, git tags."
}
"@
    [IO.File]::WriteAllText($infoFile, $info)
    exit 1
}

# ============================================================
# SUCCESS: write output files
# ============================================================
# File 1: just the next version (backward compatible with deploy.bat)
[IO.File]::WriteAllText($nextFile, $detected.Next)

# File 2: rich info for display (plain key=value for easy batch parsing)
$publishedDate = ""
try {
    $uri2 = "https://api.github.com/repos/$RepoOwner/$RepoName/releases/latest"
    if ($source -ne "none") {
        $resp2 = Invoke-RestMethod -Uri $uri2 -UseBasicParsing -TimeoutSec 10 -Headers @{ "Accept" = "application/vnd.github+json" } -ErrorAction SilentlyContinue
        if ($resp2.published_at) { $publishedDate = $resp2.published_at }
    }
} catch {}

$infoLines = @(
    "status=ok",
    "source=$source",
    "current=$($detected.Current)",
    "next=$($detected.Next)",
    "major=$($detected.Major)",
    "minor=$($detected.Minor)",
    "patch=$($detected.Patch)",
    "nextPatch=$($detected.NextPatch)",
    "published=$publishedDate",
    "repo=$RepoOwner/$RepoName"
)
[IO.File]::WriteAllLines($infoFile, $infoLines)

# Console output for non-interactive use
Write-Host "SOURCE=$source"
Write-Host "CURRENT=$($detected.Current)"
Write-Host "NEXT=$($detected.Next)"

exit 0
