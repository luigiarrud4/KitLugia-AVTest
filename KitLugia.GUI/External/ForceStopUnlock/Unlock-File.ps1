# Unlock-File.ps1
# Script para liberar arquivos bloqueados usando Handle tool do Sysinternals
# Requer: Handle.exe (Microsoft Sysinternals)

param(
    [Parameter(Mandatory=$true)]
    [string]$FilePath
)

# Auto-elevação: se não for Admin, reinicia o script elevado
if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Normal -File `"$PSCommandPath`" `"$FilePath`""
    Start-Process powershell -ArgumentList $arguments -Verb RunAs -Wait
    exit
}

# Caminho do Handle tool
$handlePath = "$PSScriptRoot\handle64.exe"

# Verifica se Handle.exe existe
if (-not (Test-Path $handlePath)) {
    Write-Host "ERRO: handle64.exe não encontrado em: $handlePath" -ForegroundColor Red
    Write-Host "Execute Install-HandleTool.ps1 para instalar o Handle tool." -ForegroundColor Yellow
    exit 1
}

# Verifica se o arquivo existe
if (-not (Test-Path $FilePath)) {
    Write-Host "ERRO: Arquivo não encontrado: $FilePath" -ForegroundColor Red
    exit 1
}

Write-Host "=== Force Stop Unlock Tool ===" -ForegroundColor Cyan
Write-Host "Arquivo alvo: $FilePath" -ForegroundColor White
Write-Host ""

# Converte para caminho absoluto
$FilePath = (Resolve-Path $FilePath).Path

# Executa Handle para encontrar o arquivo (com -accepteula para pular licença)
Write-Host "Buscando processos que estão bloqueando o arquivo..." -ForegroundColor Yellow
$output = & $handlePath -accepteula -nobanner $FilePath 2>&1 | Out-String

if ($output -match "No matching handles found") {
    Write-Host "Nenhum processo encontrado bloqueando este arquivo." -ForegroundColor Green
    Write-Host "O arquivo já pode estar liberado." -ForegroundColor Green
    exit 0
}

# Extrai informações dos handles
$lines = $output -split "`n"
$handlesFound = @()

foreach ($line in $lines) {
    if ($line -match $FilePath) {
        $handlesFound += $line
    }
}

if ($handlesFound.Count -eq 0) {
    Write-Host "Nenhum handle encontrado para este arquivo." -ForegroundColor Green
    exit 0
}

Write-Host "Encontrados $($handlesFound.Count) handles bloqueando o arquivo:" -ForegroundColor Yellow
Write-Host ""

# Processa cada handle encontrado
foreach ($handleInfo in $handlesFound) {
    Write-Host $handleInfo -ForegroundColor White
    
    # Extrai PID e Handle ID usando regex
    if ($handleInfo -match "(\w+)\s+pid:\s+(\d+)\s+([0-9A-Fa-f]+)") {
        $processName = $Matches[1]
        $pid = $Matches[2]
        $handleId = $Matches[3]
        
        Write-Host "  Processo: $processName (PID: $pid)" -ForegroundColor Cyan
        Write-Host "  Handle ID: $handleId" -ForegroundColor Cyan
        
        # Tenta fechar o handle
        Write-Host "  Tentando liberar handle..." -ForegroundColor Yellow
        
        try {
            $result = & $handlePath -accepteula -c $handleId -p $pid -y 2>&1
            
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  ✓ Handle liberado com sucesso!" -ForegroundColor Green
            } else {
                Write-Host "  ✗ Falha ao liberar handle. Código: $LASTEXITCODE" -ForegroundColor Red
                Write-Host "  Tentando encerrar processo..." -ForegroundColor Yellow
                
                try {
                    Stop-Process -Id $pid -Force -ErrorAction Stop
                    Write-Host "  ✓ Processo encerrado com sucesso!" -ForegroundColor Green
                } catch {
                    Write-Host "  ✗ Falha ao encerrar processo: $_" -ForegroundColor Red
                }
            }
        } catch {
            Write-Host "  ✗ Erro ao tentar liberar handle: $_" -ForegroundColor Red
        }
    }
    Write-Host ""
}

Write-Host "=== Processo concluído ===" -ForegroundColor Cyan
Write-Host "Tente novamente a operação (deletar/mover/renomear) o arquivo." -ForegroundColor Green
