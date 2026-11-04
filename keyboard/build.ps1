# Builds dist\ErrChatKeyboard.exe with the C# compiler that ships with Windows.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$fw = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319"
New-Item -ItemType Directory -Force dist | Out-Null
$refs = 'System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll',
  'WPF\UIAutomationClient.dll','WPF\UIAutomationTypes.dll','WPF\WindowsBase.dll' | ForEach-Object { "/r:$fw\$_" }
$out = & "$fw\csc.exe" /nologo /target:winexe /optimize+ /platform:x64 /out:dist\ErrChatKeyboard.exe `
  /win32manifest:app.manifest /win32icon:icon.ico /res:data\words.txt,words.txt /res:data\next.txt,next.txt `
  $refs (Get-ChildItem src\*.cs | ForEach-Object FullName) 2>&1
$code = $LASTEXITCODE
$out | Where-Object { $_ -notmatch 'This compiler is provided|C# 5|csc.exe|no longer the latest|go.microsoft.com' }
if ($code -ne 0) { exit $code }
"built dist\ErrChatKeyboard.exe ($([math]::Round((Get-Item dist\ErrChatKeyboard.exe).Length/1KB)) KB)"
