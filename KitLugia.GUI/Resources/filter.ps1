$json = Get-Content KitLugia_Commands_Export.json -Raw | ConvertFrom-Json

$userFriendly = $json.commands | Where-Object {
    $_.isStatic -eq $true -and
    $_.parameterCount -eq 0 -and
    $_.visibility -eq 'PUBLIC' -and
    $_.className -notmatch '^<' -and
    $_.className -notmatch 'd__' -and
    $_.className -notmatch 'b__' -and
    $_.className -notmatch 'c__'
} | Select-Object className, methodName, signature, returnType | Sort-Object className, methodName

$result = @{
    totalCommands = $userFriendly.Count
    commands = $userFriendly
}

$result | ConvertTo-Json -Depth 3 | Out-File UserFriendlyCommands.json -Encoding UTF8

Write-Host "Encontrados $($userFriendly.Count) comandos úteis para o usuário comum"
