# Install-HandleTool.ps1
# Script para baixar e instalar o Handle tool do Microsoft Sysinternals

# Verifica se está rodando como administrador
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERRO: Este script precisa ser executado como Administrador." -ForegroundColor Red
    Write-Host "Clique com botão direito e selecione 'Executar como administrador'" -ForegroundColor Yellow
    exit 1
}

Write-Host "=== Instalação do Handle Tool (Sysinternals) ===" -ForegroundColor Cyan
Write-Host ""

# URL de download do Handle tool
$handleUrl = "https://download.sysinternals.com/files/Handle.zip"
$downloadPath = "$env:TEMP\Handle.zip"
$extractPath = "$PSScriptRoot"

Write-Host "Baixando Handle tool de: $handleUrl" -ForegroundColor Yellow

try {
    # Baixa o arquivo
    Invoke-WebRequest -Uri $handleUrl -OutFile $downloadPath -UseBasicParsing
    Write-Host "Download concluído." -ForegroundColor Green
} catch {
    Write-Host "ERRO ao baixar Handle tool: $_" -ForegroundColor Red
    Write-Host "Tentando instalar via winget..." -ForegroundColor Yellow
    
    try {
        winget install Microsoft.Sysinternals.Handle --accept-source-agreements --accept-package-agreements
        Write-Host "Handle tool instalado via winget." -ForegroundColor Green
        
        # Copia handle64.exe para a pasta do script
        $wingetPath = "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\Microsoft.Sysinternals.Handle_*\handle64.exe"
        if (Test-Path $wingetPath) {
            Copy-Item $wingetPath -Destination "$PSScriptRoot\handle64.exe" -Force
            Write-Host "handle64.exe copiado para: $PSScriptRoot" -ForegroundColor Green
        } else {
            Write-Host "AVISO: Não foi possível encontrar handle64.exe instalado pelo winget." -ForegroundColor Yellow
            Write-Host "Você pode precisar copiar manualmente handle64.exe para esta pasta." -ForegroundColor Yellow
        }
        
        exit 0
    } catch {
        Write-Host "ERRO ao instalar via winget: $_" -ForegroundColor Red
        exit 1
    }
}

Write-Host "Extraindo arquivos..." -ForegroundColor Yellow

try {
    # Extrai o arquivo ZIP
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($downloadPath, $extractPath)
    Write-Host "Extração concluída." -ForegroundColor Green
} catch {
    Write-Host "ERRO ao extrair: $_" -ForegroundColor Red
    exit 1
}

# Limpa arquivo temporário
Remove-Item $downloadPath -Force -ErrorAction SilentlyContinue

# Verifica se handle64.exe foi extraído
if (Test-Path "$PSScriptRoot\handle64.exe") {
    Write-Host "✓ handle64.exe instalado com sucesso em: $PSScriptRoot" -ForegroundColor Green
} elseif (Test-Path "$PSScriptRoot\handle.exe") {
    Write-Host "✓ handle.exe instalado com sucesso em: $PSScriptRoot" -ForegroundColor Green
    # Renomeia para handle64.exe se necessário
    Rename-Item "$PSScriptRoot\handle.exe" -Destination "$PSScriptRoot\handle64.exe" -Force
} else {
    Write-Host "ERRO: Não foi possível encontrar handle64.exe após extração." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "=== Instalação concluída com sucesso! ===" -ForegroundColor Cyan
Write-Host "Agora você pode executar AddContextMenu.reg para adicionar a opção ao menu de contexto." -ForegroundColor Green
