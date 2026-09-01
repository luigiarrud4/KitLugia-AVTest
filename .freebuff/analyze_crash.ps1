# System events - critical/error
Get-WinEvent -LogName System -MaxEvents 500 | Where-Object { $_.Level -le 3 } | Select-Object -First 30 | Format-List TimeCreated, Id, LevelDisplayName, Message

Write-Host "`n=== WER APPLICATION CRASHES ===" -ForegroundColor Yellow
Get-WinEvent -FilterHashtable @{LogName='Application'; Id=1000,1001,1002} -MaxEvents 30 2>$null | Select-Object -First 20 | Format-List TimeCreated, Id, Message

Write-Host "`n=== MEMORY DIAGNOSTIC ===" -ForegroundColor Yellow
Get-WinEvent -FilterHashtable @{LogName='System'; Id=1201,1202} -MaxEvents 10 2>$null | Format-List TimeCreated, Id, Message

Write-Host "`n=== BUGCHECK ===" -ForegroundColor Yellow
Get-WinEvent -FilterHashtable @{LogName='System'; Id=1001} -MaxEvents 10 2>$null | Select-Object -First 5 | Format-List TimeCreated, Id, Message
