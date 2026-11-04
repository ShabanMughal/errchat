# Removes ErrChat Keyboard. Pass -RemoveData to also delete settings and learned words.
param([switch]$RemoveData)
$name = 'ErrChat Keyboard'
$target = Join-Path $env:LOCALAPPDATA 'Programs\ErrChatKeyboard'

Get-Process ErrChatKeyboard -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
foreach ($folder in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) {
  Remove-Item (Join-Path $folder "$name.lnk") -Force -ErrorAction SilentlyContinue
}
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' ErrChatKeyboard -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ErrChatKeyboard' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $target 'ErrChatKeyboard.exe') -Force -ErrorAction SilentlyContinue
if ($RemoveData) { Remove-Item (Join-Path $env:APPDATA 'ErrChatKeyboard') -Recurse -Force -ErrorAction SilentlyContinue }
# PowerShell has already read this script, so it can delete its own folder.
Set-Location $env:TEMP
Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
"Uninstalled $name"
