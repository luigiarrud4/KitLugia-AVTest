<#
.SYNOPSIS
    Verifica se handlers WPF estao chamando o Core (KitLugia.Core) sem Task.Run.

.DESCRIPTION
    POR QUE (03/10/2026): o usuario報表ou DUAS vezes que o Kit "travou" ao estender
    particao. A causa foi a MESMA nas duas: `PartitionManager.ExtendPartition` e `async`,
    mas roda WMI de forma SINCRONA antes do primeiro `await` — chamado direto do handler,
    executava inteiro na thread de UI (UI-FREEZE de ~5,5 s medido pelo UiFreezeWatchdog).

    Nao ha nada no compilador que reclame disso. `async` nao significa "nao bloqueia".
    Este script e a regra que falta.

    COMO FUNCIONA:
      1. Extrai as classes estaticas publicas de KitLugia.Core (automatico: nao envelhece
         quando um Manager novo e criado).
      2. Varre os .xaml.cs da GUI procurando `await <ClasseDoCore>.` .
      3. Se a mesma expressao NAO estiver envolvida em Task.Run => VIOLACAO.
      4. Compara com Scripts/ui-threading-baseline.txt. Violacoes JA registradas sao
         ignoradas (legado). Qualquer violacao NOVA faz o script sair com codigo 1.

    USO:
      powershell -ExecutionPolicy Bypass -File Scripts\CheckUiThreading.ps1
      Saida 0 = nenhum padrao novo. Saida 1 = REGRESSAO (falha no CI).

    OBSERVACAO DE ESCOPO (importante): nao e "todo await do Core precisa de Task.Run".
    Operacoes RAPIDAS (GetAllDisks, IsSystemDisk, leitura de registro) sao seguras na UI
    thread e involve-las em Task.Run só cria thread-pool churn. O alvo sao as operacoes
    POTENCIALMENTE BLOQUEANTES. Por isso o script reporta em vez de bloquear: a lista e
    revisada a mao, e so o que E novo quebra o build.

    BASEADO NA RESPOSTA DO CHATGPT (03/10/2026): ele sugeriu "nenhum await do Core sem
    Task.Run" como regra global. Isso foi RECUSADO de proposito (gera Task.Run demais).
    Aqui a regra e "nenhum padrao NOVO sem Task.Run", com baseline.
#>
[CmdletBinding()]
param(
    [switch]$UpdateBaseline
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$coreDir = Join-Path $repoRoot 'KitLugia.Core'
$guiDir  = Join-Path $repoRoot 'KitLugia.GUI'
$baselineFile = Join-Path $repoRoot 'Scripts\ui-threading-baseline.txt'

if (-not (Test-Path $coreDir) -or -not (Test-Path $guiDir)) {
    Write-Error "Rode a partir da raiz do repositorio (Core/GUI nao encontrados em '$repoRoot')."
    exit 2
}

# --- 1. Classes estaticas publicas do Core -------------------------------------
# Extraidas do fonte para nao depender de nenhuma lista hardcoded.
$coreClasses = @{}
Get-ChildItem -Path $coreDir -Filter '*.cs' -Recurse |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin|backups)[\\/]' } |
    ForEach-Object {
        Select-String -Path $_.FullName -Pattern 'public\s+static\s+(?:partial\s+|sealed\s+|abstract\s+)?class\s+(\w+)' -AllMatches |
            ForEach-Object { foreach ($m in $_.Matches) { $coreClasses[$m.Groups[1].Value] = $true } }
    }

if ($coreClasses.Count -eq 0) {
    Write-Error 'Nenhuma classe estatica publica encontrada em KitLugia.Core (padrao mudou?).'
    exit 2
}

# --- 2. Varredura da GUI --------------------------------------------------------
# Captura `await Task.Run(...) => Core.X` (grupo 1 presente = ja protegido) e
# `await Core.X` (grupo 1 ausente = candidato).
$awaitRx = [regex]::new('await\s+(Task\.Run\s*\(\s*\(\s*\)\s*=>\s*)?([A-Za-z_]\w*)\s*\.\s*(\w+)')

$found = [System.Collections.Generic.List[string]]::new()

Get-ChildItem -Path $guiDir -Filter '*.xaml.cs' -Recurse |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin|backups)[\\/]' } |
    ForEach-Object {
        $file = $_
        $rel = $file.FullName.Substring($repoRoot.Length + 1)
        $lines = [System.IO.File]::ReadAllLines($file.FullName)
        for ($i = 0; $i -lt $lines.Length; $i++) {
            $line = $lines[$i]
            if ($line -match '^\s*//' -or $line -match '^\s*///') { continue }
            foreach ($m in $awaitRx.Matches($line)) {
                $protectedByRun = $m.Groups[1].Success
                $class  = $m.Groups[2].Value
                $method = $m.Groups[3].Value
                if (-not $coreClasses.ContainsKey($class)) { continue }   # nao e Core
                if ($protectedByRun) { continue }                         # ja esta em Task.Run

                # Chave SEM numero de linha: mover o codigo nao deve quebrar o baseline.
                $found.Add("$rel :: await $class.$method")
            }
        }
    }

$unique = $found | Sort-Object -Unique

# --- 3. Comparacao com o baseline ----------------------------------------------
$baseline = @()
if (Test-Path $baselineFile) {
    $baseline = @(Get-Content $baselineFile | Where-Object { $_ -and -not $_.StartsWith('#') })
}

if ($UpdateBaseline) {
    $unique | Set-Content -Path $baselineFile -Encoding UTF8
    Write-Host "Baseline atualizado: $($unique.Count) padrao(s) legado(s) registrado(s)." -ForegroundColor Green
    exit 0
}

$new = @($unique | Where-Object { $baseline -notcontains $_ })
$legacy = $unique.Count - $new.Count

Write-Host ''
Write-Host '=== Verificacao de travessia de thread (UI -> Core) ===' -ForegroundColor Cyan
Write-Host "Classes do Core detectadas : $($coreClasses.Count)"
Write-Host "Padroes legados (baseline) : $legacy"
Write-Host "Padroes NOVOS              : $($new.Count)"
Write-Host ''

if ($new.Count -eq 0) {
    Write-Host 'OK - nenhum padrao novo de await do Core sem Task.Run.' -ForegroundColor Green
    exit 0
}

Write-Host 'FALHA - padrao novo de operacao potencialmente bloqueante na thread de UI:' -ForegroundColor Red
foreach ($p in $new) {
    Write-Host "  $p" -ForegroundColor Red
}
Write-Host ''
Write-Host 'Envolva em Task.Run (o callback UpdateProgress ja e thread-safe) OU, se a' -ForegroundColor Yellow
Write-Host 'operacao for rapida demais para justificar thread, grave a decisao com:' -ForegroundColor Yellow
Write-Host '  powershell -ExecutionPolicy Bypass -File Scripts\CheckUiThreading.ps1 -UpdateBaseline' -ForegroundColor Yellow
exit 1
