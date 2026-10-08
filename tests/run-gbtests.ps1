# ===========================================================================
# run-gbtests.ps1 - executa a bateria de testes do GameBoost de uma vez
# ===========================================================================
# Os harnesses viviam em %TEMP% e morriam com a sessao. Aqui ficam no
# repositorio e este runner constroi e corre todos, devolvendo um resumo.
#
# USO:
#   powershell -ExecutionPolicy Bypass -File tests\run-gbtests.ps1
#   powershell -ExecutionPolicy Bypass -File tests\run-gbtests.ps1 -Rapido
#   powershell -ExecutionPolicy Bypass -File tests\run-gbtests.ps1 -So apenas gbidem,gbsave
#
# PARAMETROS:
#   -Rapido     corre o soak com 50 ciclos em vez de 1000
#   -SoakN N    numero de ciclos do soak (por omissao 1000, ou 50 com -Rapido)
#   -So lista   so' estes projetos (separados por virgula). Util para repetir um.
#   -SemSoak    atalho para -So sem o gbsoak (bateria rapida de correcao)
#
# NOTAS:
#   - O OutDir e' ABSOLUTO de proposito. Com caminho relativo, a DLL do
#     KitLugia.GUI ia parar DENTRO do repositorio e as passagens seguintes
#     compilavam contra uma copia obsoleta (custou horas a diagnosticar).
#   - Alguns testes precisam de elevacao (escrevem no HKLM). O gblimited
#     precisa do CONTRARIO: e' lancado por uma Scheduled Task com -RunLevel
#     Limited, senao a linha "nao elevado" da matriz nao fica provada.
#   - gbcrash e' de duas fases: --crash mata-se de proposito (TerminateProcess)
#     e --rescue corre num processo NOVO que limpa o estado deixado.

param(
    [switch]$Rapido,
    [int]$SoakN = 0,
    [string]$So = '',
    [switch]$SemSoak
)

$ErrorActionPreference = 'Continue'
$raiz = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $env:TEMP 'gbtests-bin'
if ($SoakN -le 0) { $SoakN = if ($Rapido) { 50 } else { 1000 } }
if ($SemSoak -and $So -eq '') { $So = 'gbauto,gbv4,gbreliab,gbprobe,gbengine,gbrescue,gbidem,gbsave,gbcrash,gblimited' }

Write-Host "=== bateria de testes do GameBoost ===" -ForegroundColor Cyan
Write-Host "repositorio : $raiz"
Write-Host "binarios    : $outDir"
Write-Host "soak        : $SoakN ciclos"

$elevado = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host "elevado     : $elevado"
if (-not $elevado) {
    Write-Host "AVISO: sem elevacao, os testes que escrevem no HKLM vao falhar." -ForegroundColor Yellow
}
Write-Host ""

# ---------------------------------------------------------------- utilitarios
$script:ScheduledTaskName = 'KitLugia_GbTest_Limited'

# Corre um exe e devolve { Codigo, Texto }.
#
# NAO usar o operador `&` do PowerShell: o output vai para um PIPE, e qualquer
# processo FILHO que o harness crie herda esse pipe. Se o filho viver mais que o
# pai (as vitimas --idle do gbcrash vivem minutos), o `&` so' recebe EOF quando
# o ultimo filho morrer - o runner ficava pendurado 5 minutos. Com
# Start-Process + ficheiros, cada processo tem o seu proprio destino.
function Invoke-ComCaptura {
    param([string]$Exe, [string[]]$Argumentos, [string]$NomeLog, [int]$TimeoutMs = 600000)

    # NAO usar o operador `&` nem `Start-Process -Wait`:
    #   - `&` captura para um PIPE e qualquer FILHO que o harness crie herda esse
    #     pipe -> a leitura so' acaba quando o ultimo filho morrer (as vitimas
    #     --idle do gbcrash dormem 120 s e penduravam o runner).
    #   - `Start-Process -Wait` espera pelo processo E PELOS DESCENDENTES: a fase
    #     --rescue do gbcrash arrancava quando as vitimas ja tinham saido e falhava
    #     com prio=Normal / mem=-1 (tres falhas falsas).
    # Aqui o processo e' criado pela .NET directamente: espera-se SO' por ele, com
    # teto, e os streams sao lidos em paralelo (sem deadlock) tambem com teto.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    # ArgumentList NAO existe no Windows PowerShell 5.1 - usa-se a string com aspas.
    # Sem isto, ArgumentList era $null e o exe arrancava SEM argumentos (o gbcrash
    # caia no modo errado e acusava tres falhas falsas).
    $quoted = @($Argumentos | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { "$_" }
    })
    $psi.Arguments = ($quoted -join ' ')

    $p = [System.Diagnostics.Process]::Start($psi)
    $so = $p.StandardOutput.ReadToEndAsync()
    $se = $p.StandardError.ReadToEndAsync()

    if (-not $p.WaitForExit($TimeoutMs)) {
        try { $p.Kill() } catch { }
        throw "$NomeLog excedeu ${TimeoutMs}ms"
    }

    # As linhas podem ficar abertas quando algum FILHO do harness herda o pipe
    # (o charmap do gbreliab/gbidem/gbsoak vive depois do harness). Nao bloqueamos:
    # matamos os filhos de teste e damos um instante curto para o pipe fechar.
    Start-Sleep -Milliseconds 300
    if (-not $so.IsCompleted -or -not $se.IsCompleted) {
        Get-Process charmap -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
    }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ((-not $so.IsCompleted -or -not $se.IsCompleted) -and $sw.ElapsedMilliseconds -lt 5000) {
        Start-Sleep -Milliseconds 50
    }

    $texto = @()
    if ($so.IsCompleted -and $so.Result) { $texto += ($so.Result -split "`r?`n") }
    if ($se.IsCompleted -and $se.Result) { $texto += ($se.Result -split "`r?`n") }
    if (-not $so.IsCompleted) { $texto += "(saida nao capturada: os streams nao fecharam em 5s)" }

    return @{ Codigo = $p.ExitCode; Texto = $texto }
}

# Corre um exe com o token NORMAL do utilizador (RunLevel Limited) e devolve o
# exit code. Necessario para o gblimited: o processo-pai e' elevado e um filho
# herdava a elevacao, provando a linha errada da matriz.
function Invoke-SemElevacao {
    param([string]$Exe, [string[]]$Argumentos, [int]$TimeoutMin = 5)

    $nome = $script:ScheduledTaskName
    try { Unregister-ScheduledTask -TaskName $nome -Confirm:$false -ErrorAction SilentlyContinue } catch { }

    $action = New-ScheduledTaskAction -Execute $Exe -Argument ($Argumentos -join ' ') `
                                      -WorkingDirectory (Split-Path -Parent $Exe)
    $principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) `
                                            -LogonType Interactive -RunLevel Limited
    # -Priority 4 = Normal. Por omissao o Task Scheduler usa 7 (BelowNormal) e os
    # filhos herdam essa classe, o que fazia o charmap nascer a BelowNormal.
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                                             -ExecutionTimeLimit (New-TimeSpan -Minutes $TimeoutMin) -Priority 4

    Register-ScheduledTask -TaskName $nome -Action $action -Principal $principal `
                           -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName $nome

    $limite = (Get-Date).AddMinutes($TimeoutMin)
    $rc = 267009   # SCHED_S_TASK_RUNNING
    while ($rc -eq 267009 -and (Get-Date) -lt $limite) {
        Start-Sleep -Milliseconds 500
        $rc = (Get-ScheduledTaskInfo -TaskName $nome).LastTaskResult
    }
    Unregister-ScheduledTask -TaskName $nome -Confirm:$false -ErrorAction SilentlyContinue
    return $rc
}

# ---------------------------------------------------------------- a bateria
# tipo: 'simples' | 'crash' | 'limited'
$testes = @(
    @{ proj = 'gbauto';   exe = 'GbAuto';    nome = 'Auto v2 (25 verificacoes)';            args = @();                     tipo = 'simples' } ,
    @{ proj = 'gbv4';     exe = 'GbV4';      nome = 'V4 legado (7 verificacoes)';           args = @();                     tipo = 'simples' } ,
    @{ proj = 'gbreliab'; exe = 'GbReliab';  nome = 'Fiabilidade (13 verificacoes)';        args = @('--src', $raiz);        tipo = 'simples' } ,
    @{ proj = 'gbprobe';  exe = 'GbProbe';   nome = 'Sondagem de capacidade';               args = @();                     tipo = 'simples' } ,
    @{ proj = 'gbengine'; exe = 'GbEngine';  nome = 'Por knob: V1/V2/V3/V4/Auto';           args = @();                     tipo = 'simples' } ,
    @{ proj = 'gbrescue'; exe = 'GbRescue';  nome = 'Resgate do tweak de rede (3 cenarios)';args = @();                     tipo = 'simples' } ,
    @{ proj = 'gbidem';   exe = 'GbIdem';    nome = 'Idempotencia + handles (200x)';        args = @('--n', '200');         tipo = 'simples' } ,
    @{ proj = 'gbsave';   exe = 'GbSave';    nome = 'Morte a meio da escrita (25 rondas)';  args = @('--rounds', '25');     tipo = 'simples' } ,
    @{ proj = 'gbcrash';  exe = 'GbCrash';   nome = 'Crash real + resgate (2 fases)';       args = @();                     tipo = 'crash'   } ,
    @{ proj = 'gblimited';exe = 'GbLimited'; nome = 'Sem elevacao (matriz)';                args = @();                     tipo = 'limited' } ,
    @{ proj = 'gbsoak';   exe = 'GbSoak';    nome = "Soak ($SoakN ciclos)";                 args = @('--ciclos', "$SoakN"); tipo = 'simples' }
)

if ($So -ne '') {
    $queridos = $So.Split(',') | ForEach-Object { $_.Trim() }
    $testes = $testes | Where-Object { $queridos -contains $_.proj }
}

$resultados = @()
foreach ($t in $testes) {
    $proj = Join-Path $raiz "tests\$($t.proj)\$($t.proj).csproj"
    Write-Host "--- $($t.nome) ---" -ForegroundColor White

    if (-not (Test-Path $proj)) {
        Write-Host "    PULADO: projeto nao existe ($proj)" -ForegroundColor Yellow
        $resultados += [pscustomobject]@{ Teste = $t.nome; Build = 'ausente'; Run = 'pulado' }
        continue
    }

    $buildOut = & dotnet build $proj -v q --nologo "-p:OutDir=$outDir/" 2>&1
    $buildOk = ($LASTEXITCODE -eq 0)
    if (-not $buildOk) {
        Write-Host "    BUILD FALHOU" -ForegroundColor Red
        $buildOut | Where-Object { $_ -match 'error' } | Select-Object -First 5 | ForEach-Object { Write-Host "      $_" }
        $resultados += [pscustomobject]@{ Teste = $t.nome; Build = 'falhou'; Run = 'nao correu' }
        continue
    }

    $exe = Join-Path $outDir "$($t.exe).exe"
    $estado = 'falhou (?)'

    switch ($t.tipo) {
        'crash' {
            # Fase 1: aplica o boost pelo caminho real e MORRE (TerminateProcess).
            $r1 = Invoke-ComCaptura -Exe $exe -Argumentos @('--crash') -NomeLog 'gbcrash1'
            $rc1 = $r1.Codigo
            $falhas1 = @($r1.Texto | Where-Object { $_ -match '\[FALHA\]' }).Count
            Write-Host "    fase 1 (--crash): exit=$rc1  falhas=$falhas1"

            # Fase 2: processo NOVO faz o resgate do estado deixado.
            $r2 = Invoke-ComCaptura -Exe $exe -Argumentos @('--rescue') -NomeLog 'gbcrash2'
            $rc2 = $r2.Codigo
            $r2.Texto | Select-Object -Last 3 | ForEach-Object { Write-Host "    $_" }

            if ($rc2 -eq 0 -and $falhas1 -eq 0) { $estado = 'passou' }
            else { $estado = "falhou (crash=$rc1/$falhas1, rescue=$rc2)" }
        }
        'limited' {
            # Corrido com token normal: o exe ABORTA se arrancar elevado.
            $log = Join-Path $outDir 'gblimited-out.txt'
            try { if (Test-Path $log) { Remove-Item $log -Force } } catch { }
            $rc = Invoke-SemElevacao -Exe $exe -Argumentos @('--out', $log) -TimeoutMin 5
            if (Test-Path $log) {
                Get-Content $log | Where-Object { $_ -match '\[OK|\[FALHA|elevado=|ABORTADO|TODOS|FALHARAM' } |
                    Select-Object -Last 12 | ForEach-Object { Write-Host "    $_" }
            } else {
                Write-Host "    sem log do gblimited ($log)" -ForegroundColor Yellow
            }
            if ($rc -eq 0) { $estado = 'passou' } else { $estado = "falhou (exit $rc)" }
        }
        default {
            $r = Invoke-ComCaptura -Exe $exe -Argumentos $t.args -NomeLog $t.proj
            $r.Texto | Select-Object -Last 4 | ForEach-Object { Write-Host "    $_" }
            if ($r.Codigo -eq 0) { $estado = 'passou' } else { $estado = "falhou ($($r.Codigo))" }
        }
    }

    if ($estado -eq 'passou') { Write-Host "    => PASSOU" -ForegroundColor Green }
    else { Write-Host "    => FALHOU: $estado" -ForegroundColor Red }

    $resultados += [pscustomobject]@{ Teste = $t.nome; Build = 'ok'; Run = $estado }
}

# limpa processos de teste que possam ter ficado
foreach ($n in @('charmap', 'GbSave', 'GbCrash', 'GbSoak')) {
    Get-Process $n -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
try { Unregister-ScheduledTask -TaskName $script:ScheduledTaskName -Confirm:$false -ErrorAction SilentlyContinue } catch { }

Write-Host ""
Write-Host "=== RESUMO ===" -ForegroundColor Cyan
$resultados | Format-Table -AutoSize

$falhas = @($resultados | Where-Object { $_.Run -ne 'passou' }).Count
if ($falhas -eq 0) {
    Write-Host "TODA A BATERIA PASSOU  ($($resultados.Count) testes)" -ForegroundColor Green
    exit 0
} else {
    Write-Host "$falhas de $($resultados.Count) teste(s) nao passaram" -ForegroundColor Red
    exit 1
}
