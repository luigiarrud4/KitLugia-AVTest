# patch_setupcompat.ps1 - aplica o patch de downgrade na setupcompat.dll
# Uso: powershell -NoProfile -ExecutionPolicy Bypass -File patch_setupcompat.ps1 -Dll <caminho>
#
# Localiza o epilogo unico "B8 01 00 00 00 C3 33 C0 C3" (MOV eax,1; RET / XOR eax,eax; RET)
# da funcao CWindowsVersion::IsLaterThan e troca o byte "01" por "00" (offset+1),
# fazendo o setup tratar downgrade como upgrade normal ("Keep personal files and apps").
# Padrao confirmado na midia 25H2 26200.8973 (VA 0x180002DFC / FILE 0x2DFC, 31/07/2026).
#
# Exit codes:
#   0 - PATCHED / ALREADY_PATCHED / NAO_APLICAVEL (ok)
#   1 - NOT_FOUND (padrao nao existe nesta DLL - build mudou? reportar)
#   2 - AMBIGUOUS (varias ocorrencias - nao patchear, reportar offsets)
#   3 - erro inesperado (verificacao falhou)
#   4 - arquivo nao existe

param(
  [Parameter(Mandatory = $true)][string]$Dll
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Dll)) {
  Write-Output "ERRO: arquivo nao existe: $Dll"
  exit 4
}

$data = [System.IO.File]::ReadAllBytes($Dll)

# padroes (epilogo fundido do MSVC: todos os "return 1" convergem no mov eax,1)
$patRet  = [byte[]](0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3, 0x33, 0xC0, 0xC3)  # nao patcheado (9B, unico)
$patDone = [byte[]](0xB8, 0x00, 0x00, 0x00, 0x00, 0xC3, 0x33, 0xC0, 0xC3)  # ja patcheado (9B, unico)
$patShort= [byte[]](0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3)                    # fallback: epilogo "return 1" curto

function Find-Patterns([byte[]]$hay, [byte[]]$needle) {
  $hits = @()
  $n = $needle.Length
  for ($i = 0; $i -le $hay.Length - $n; $i++) {
    $m = $true
    for ($j = 0; $j -lt $n; $j++) {
      if ($hay[$i + $j] -ne $needle[$j]) { $m = $false; break }
    }
    if ($m) { $hits += $i }
  }
  return $hits
}

function Write-Hits($hits) {
  return ($hits | ForEach-Object { "0x{0:X}" -f $_ }) -join ", "
}

function Do-Patch([int]$off, [byte[]]$verifyPattern) {
  if (-not (Test-Path -LiteralPath "$Dll.orig")) {
    Copy-Item -LiteralPath $Dll -Destination "$Dll.orig" -Force
    Write-Output "backup criado: $Dll.orig"
  } else {
    Write-Output "backup ja existe: $Dll.orig (mantido)"
  }
  $data[$off + 1] = 0x00
  [System.IO.File]::WriteAllBytes($Dll, $data)
  $check = [System.IO.File]::ReadAllBytes($Dll)
  $ok = $true
  for ($j = 0; $j -lt $verifyPattern.Length; $j++) {
    if ($check[$off + $j] -ne $verifyPattern[$j]) { $ok = $false }
  }
  if (-not $ok) {
    Write-Output "AVISO: verificacao apos o patch FALHOU em 0x{0:X}" -f $off
    exit 3
  }
  $bytes = ($check[$off..($off + 8)] | ForEach-Object { $_.ToString("X2") }) -join " "
  Write-Output ("PATCHED offset=0x{0:X} (byte {0:X} -> 00) bytes={1}" -f ($off + 1), $bytes)
  exit 0
}

$hits = @(Find-Patterns $data $patRet)
if ($hits.Count -eq 1) {
  Do-Patch $hits[0] $patDone
  return
}
if ($hits.Count -gt 1) {
  Write-Output ("AMBIGUOUS: " + (Write-Hits $hits))
  exit 2
}

$hits = @(Find-Patterns $data $patDone)
if ($hits.Count -eq 1) {
  Write-Output ("ALREADY_PATCHED em 0x{0:X} - nada a fazer" -f $hits[0])
  exit 0
}
if ($hits.Count -gt 1) {
  Write-Output ("AMBIGUOUS(patched): " + (Write-Hits $hits))
  exit 2
}

$hits = @(Find-Patterns $data $patShort)
if ($hits.Count -eq 1) {
  $verify = [byte[]](0xB8, 0x00, 0x00, 0x00, 0x00, 0xC3)
  Do-Patch $hits[0] $verify
  return
}
if ($hits.Count -gt 1) {
  Write-Output ("AMBIGUOUS(short): " + (Write-Hits $hits))
  exit 2
}

Write-Output "NOT_FOUND: nenhum padrao de epilogo 'return 1' encontrado (build mudou?)"
exit 1
