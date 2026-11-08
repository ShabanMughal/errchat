# Installs ErrChat Keyboard for this Windows user. No admin rights needed.
$ErrorActionPreference = 'Stop'
$target = Join-Path $env:LOCALAPPDATA 'Programs\ErrChatKeyboard'
$exe = Join-Path $target 'ErrChatKeyboard.exe'
$name = 'ErrChat Keyboard'

Get-Process ErrChatKeyboard -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'dist\ErrChatKeyboard.exe') $exe -Force
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') (Join-Path $target 'uninstall.ps1') -Force

$shell = New-Object -ComObject WScript.Shell
foreach ($folder in [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Desktop')) {
  $link = $shell.CreateShortcut((Join-Path $folder "$name.lnk"))
  $link.TargetPath = $exe
  $link.WorkingDirectory = $target
  $link.Description = 'Grammar fixes, word suggestions and quick replies in any app'
  $link.Save()
}

# Start with Windows (turn off in Settings > Start with Windows).
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
New-Item -Path $run -Force | Out-Null
Set-ItemProperty $run ErrChatKeyboard "`"$exe`""

$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ErrChatKeyboard'
New-Item -Path $key -Force | Out-Null
Set-ItemProperty $key DisplayName $name
Set-ItemProperty $key DisplayVersion '0.1.0'
Set-ItemProperty $key Publisher 'errchat'
Set-ItemProperty $key DisplayIcon $exe
Set-ItemProperty $key InstallLocation $target
Set-ItemProperty $key NoModify 1 -Type DWord
Set-ItemProperty $key NoRepair 1 -Type DWord
Set-ItemProperty $key UninstallString "powershell -NoProfile -ExecutionPolicy Bypass -File `"$target\uninstall.ps1`""
"Installed to $target"
