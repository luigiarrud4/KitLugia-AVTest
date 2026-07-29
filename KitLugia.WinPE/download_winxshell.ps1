# Download WinXShell do WimBuilder2 (82MB) e extrai apenas o WinXShell
$url = "https://github.com/slorelee/wimbuilder2/releases/download/v2026.03.03/WimBuilder2-Full.v2026-03-03.7z"
$outDir = "$PSScriptRoot\WinXShell"
$temp7z = "$env:TEMP\wimbuilder2.7z"

Write-Host "Baixando WimBuilder2 (82MB)... isso pode levar alguns minutos..."
Invoke-WebRequest -Uri $url -OutFile $temp7z -UseBasicParsing

Write-Host "Extraindo WinXShell..."
if (Get-Command "7z.exe" -ErrorAction SilentlyContinue) {
    & 7z.exe e "$temp7z" "WimBuilder2\bin\WinXShell\*" -o"$outDir" -y
} else {
    Write-Host "7z.exe não encontrado. Use 7-Zip ou WinRAR para extrair."
    Write-Host "O arquivo está em: $temp7z"
}

Write-Host "WinXShell extraído para: $outDir"
Get-ChildItem "$outDir\*.exe"
