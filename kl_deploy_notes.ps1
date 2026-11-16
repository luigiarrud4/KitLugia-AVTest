<#
.SYNOPSIS
    Gera um resumo de commits (markdown) para as notas da release do KitLugia.
.DESCRIPTION
    O gerador nativo do GitHub (Release Notes API, usado por --generate-notes)
    so' lista PRs mergeados. Este repo faz push direto no main, sem PRs - sem
    este resumo o corpo da release ficaria com o link de comparacao apenas.

    O script le' git log entre a release anterior e o HEAD, agrupa por tipo
    (Conventional Commits: feat/fix/docs/...) e escreve markdown em
    %TEMP%\kl_deploy_notes.md (mesmo caminho lido pelo deploy.bat).
    Tambem' escreve %TEMP%\kl_deploy_base.txt com a tag usada como base
    (vazio se nao' houve base), para o deploy.bat montar o link de
    comparacao no fallback.

    Fonte da tag base, na ordem:
      1. -BaseTag (explicito, para testes)
      2. tag da ultima release publicada (gh release view)
      3. maior tag local vX.Y.Z menor que a nova versao' (offline)
      4. nada -> exit 1 (sem resumo; o deploy.bat usa so' o gerador do GitHub)

    Exit 0 = arquivo escrito com >= 1 commit.
    Exit 1 = nada para listar (deploy.bat entao' usa so' o gerador do GitHub).
#>
param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$RepoOwner = "luigiarrud4",
    [string]$RepoName  = "KitLugia-AVTest",
    [string]$BaseTag   = "",
    [int]$MaxCommits   = 40
)

$ErrorActionPreference = 'Stop'

# git log emite bytes UTF-8 crudos; o PS 5.1 decodifica o stdout de comando
# nativo com a encoding da consola (cp1252) e corrompe acentos. Forcar UTF-8:
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$notesFile = Join-Path $env:TEMP "kl_deploy_notes.md"
$baseFile  = Join-Path $env:TEMP "kl_deploy_base.txt"
foreach ($f in @($notesFile, $baseFile)) {
    if (Test-Path $f) { Remove-Item $f -Force }
}

$shortRepo = "$RepoOwner/$RepoName"
$base = ""

# --- 1. Qual e' a tag de base? -------------------------------------------
if ($BaseTag) {
    $base = $BaseTag
} else {
    # Comando nativo com EAP=Stop: QUALQUER linha em stderr vira erro
    # terminante (mesmo com 2>$null). Padrao: 2>&1 + catch + so' linhas de texto.
    try {
        $raw = & gh release view --repo $shortRepo --json tagName --jq '.tagName' 2>&1
        $text = ($raw | Where-Object { $_ -is [string] }) -join "`n"
        if ($text -match '^v(\d+\.\d+\.\d+)$') { $base = "v$($Matches[1])" }
    } catch { $base = "" }
}
if (-not $base) {
    # maior tag local vX.Y.Z menor que a nova versao'
    $target = [version]$Version
    $best = $null
    $allTags = $null
    try { $allTags = & git tag --sort=-v:refname 2>&1 } catch { $allTags = $null }
    foreach ($t in ($allTags | Where-Object { $_ -is [string] })) {
        if ($t -match '^v(\d+\.\d+\.\d+)$') {
            $v = [version]$Matches[1]
            if ($v -lt $target -and ($null -eq $best -or $v -gt $best)) { $best = $v }
        }
    }
    if ($best) { $base = "v$($best.ToString())" }
}
[IO.File]::WriteAllText($baseFile, $base)
if (-not $base) { exit 1 }

# A tag precisa existir localmente para o git log; se nao', busca so' ela.
if (-not (git rev-parse --verify -q "refs/tags/$base" 2>&1)) {
    try { & git fetch origin "refs/tags/$base" 2>&1 | Out-Null } catch { }
}
if (-not (git rev-parse --verify -q "refs/tags/$base" 2>&1)) { exit 1 }

# --- 2. Log de commits ----------------------------------------------------
$logArgs = @("--no-merges", "--pretty=format:%h|%s", "--max-count", [string]$MaxCommits, "$base", "HEAD")
$lines = $null
try { $lines = & git log @logArgs 2>&1 } catch { $lines = $null }
$lines = @($lines | Where-Object { $_ -is [string] })
if ($lines.Count -eq 0) { exit 1 }

# --- 3. Filtrar + agrupar --------------------------------------------------
$sectionOrder = @('feat','fix','perf','refactor','test','docs','style','build','ci','chore','revert')
$sectionNames = @{
    'feat'='Features'; 'fix'='Bug fixes'; 'perf'='Performance'
    'refactor'='Refactoring'; 'test'='Tests'; 'docs'='Documentation'
    'style'='Styles'; 'build'='Build'; 'ci'='CI'
    'chore'='Chores'; 'revert'='Reverts'
}
$groups = @{}
foreach ($s in $sectionOrder) { $groups[$s] = New-Object System.Collections.Generic.List[string] }
$other  = New-Object System.Collections.Generic.List[string]
$kept   = 0

foreach ($line in $lines) {
    if (-not $line) { continue }
    $parts = [string]$line -split '\|', 2
    if ($parts.Count -lt 2) { continue }
    $hash = $parts[0].Trim()
    $subj = $parts[1].Trim()
    # O commit generico do proprio deploy nao' acrescenta informacao.
    if ($subj -match '^Deploy v\d' -or $subj -match '^chore\(deploy\)') { continue }
    if ($subj -match '^(feat|fix|perf|refactor|test|docs|style|build|ci|chore|revert)(\([^)]*\))?:\s*(.+)$') {
        $type = $Matches[1]
        # Grupo opcional: commit sem scope deixa $Matches[2] = $null.
        $scope = ""
        if ($Matches[2]) { $scope = $Matches[2].Trim() -replace '^\(','' -replace '\)$','' }
        $text = $Matches[3]
        if ($scope) { $entry = "- ${scope}: ${text} (${hash})" }
        else        { $entry = "- ${text} (${hash})" }
        $groups[$type].Add($entry)
        $kept++
    } else {
        $other.Add("- $subj ($hash)")
        $kept++
    }
}
if ($kept -eq 0) { exit 1 }

# --- 4. Montar o markdown ---------------------------------------------------
$out = New-Object System.Collections.Generic.List[string]
$out.Add("## Destaques - v$Version")
$out.Add("")
if ($base) {
    $out.Add("_$kept commit(s) desde a $base, agrupados pelo tipo do commit._")
} else {
    $out.Add("_Ultimos $kept commits do repositorio._")
}
if ($kept -ge $MaxCommits) { $out.Add("_Limitado a $MaxCommits commits._") }
$out.Add("")

foreach ($s in $sectionOrder) {
    if ($groups[$s].Count -eq 0) { continue }
    $out.Add("### $($sectionNames[$s])")
    $out.Add("")
    foreach ($e in $groups[$s]) { $out.Add($e) }
    $out.Add("")
}
if ($other.Count -gt 0) {
    $out.Add("### Other")
    $out.Add("")
    foreach ($e in $other) { $out.Add($e) }
    $out.Add("")
}

[IO.File]::WriteAllLines($notesFile, $out)
Write-Host "NOTES_FILE=$notesFile"
Write-Host "BASE_TAG=$base"
Write-Host "COMMITS=$kept"
exit 0
