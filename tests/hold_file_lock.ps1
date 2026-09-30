# hold_file_lock.ps1 — utilitario de teste do Force Stop Unlock.
#
# Mantem um arquivo aberto com FileShare.None (lock exclusivo, igual a um
# app/driver que trava o arquivo) por N segundos, para validar que o pipeline
# (FileOpGuarantee) detecta e libera o bloqueador.
#
# Uso:
#   powershell -NoProfile -File tests/hold_file_lock.ps1 -Path C:\temp\a.txt -Seconds 30
#
# O arquivo precisa existir. O script imprime "LOCKED" quando o lock estiver
# ativo (para o chamador esperar) e fica bloqueado ate o fim dos segundos.

param(
    [Parameter(Mandatory = $true)][string]$Path,
    [int]$Seconds = 30
)

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Error "Arquivo nao encontrado: $Path"
    exit 2
}

$stream = [System.IO.File]::Open(
    $Path,
    [System.IO.FileMode]::Open,
    [System.IO.FileAccess]::ReadWrite,
    [System.IO.FileShare]::None)

try {
    Write-Output "LOCKED"
    [Console]::Out.Flush()
    Start-Sleep -Seconds $Seconds
}
finally {
    $stream.Dispose()
    Write-Output "RELEASED"
}
