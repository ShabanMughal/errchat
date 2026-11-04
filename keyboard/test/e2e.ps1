# Drives the real helper against a WhatsApp-style box in Chrome and a classic Windows
# text box. Takes over the keyboard for about a minute and uses a little AI quota.
param([string]$Shots = "$env:TEMP\errchat-kb-shots")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class W {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, uint d, UIntPtr e);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  public static string Title(IntPtr h) { var s = new StringBuilder(4096); GetWindowText(h, s, 4096); return s.ToString(); }
  public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(120); mouse_event(2,0,0,0,UIntPtr.Zero); mouse_event(4,0,0,0,UIntPtr.Zero); }
}
'@
[W]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null

New-Item -ItemType Directory -Force $Shots | Out-Null
$root = Split-Path $PSScriptRoot
$data = Join-Path $env:TEMP "errchat-kb-e2e-$PID"
$chromeProfile = Join-Path $env:TEMP "errchat-kb-chrome-$PID"
New-Item -ItemType Directory -Force $data | Out-Null
$env:ERRCHAT_KB_DATA = $data
# Without this it counts as a first run and opens the settings window over the test.
Set-Content (Join-Path $data 'settings.txt') "provider=claude`nautoGrammar=true`nlearnWords=true`nautoReplies=true"
$trace = Join-Path $data 'strip.txt'
$env:ERRCHAT_KB_TRACE = $trace
$script:fail = 0
$script:win = [IntPtr]::Zero
$exe = Join-Path $root 'dist\ErrChatKeyboard.exe'

function Check($name, $ok, $detail = '') { if ($ok) { "PASS  $name" } else { $script:fail++; "FAIL  $name  $detail" } }
function Panel { [W]::FindWindow([NullString]::Value, 'ErrChat Keyboard') }
function PanelShown { $p = Panel; $p -ne [IntPtr]::Zero -and [W]::IsWindowVisible($p) }
function PanelRect { $r = New-Object W+RECT; [W]::GetWindowRect((Panel), [ref]$r) | Out-Null; $r }
function Guard {
  $front = [W]::GetForegroundWindow()
  if ($front -ne $script:win) { throw "Another window took focus ('$([W]::Title($front))'); stopping so nothing is typed into it." }
}
function Keys($s) { Guard; [System.Windows.Forms.SendKeys]::SendWait($s) }
function Text { [W]::Title($script:win) }
function Field { $t = Text; if ($t -match 'KBTEST\[(.*)\]SENT') { $Matches[1] } elseif ($t -match 'KBTEST\[(.*)\]') { $Matches[1] } else { $t } }
function WaitFor($seconds, [scriptblock]$cond) { $end = (Get-Date).AddSeconds($seconds); while ((Get-Date) -lt $end) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 100 }; $false }
function Shot($name) {
  $r = PanelRect; $pad = 300
  $x = [math]::Max(0, $r.L - 40); $y = [math]::Max(0, $r.T - 40); $w = [math]::Min(1100, $r.R - $r.L + $pad + 300); $h = 340
  $b = New-Object Drawing.Bitmap $w, $h; $g = [Drawing.Graphics]::FromImage($b)
  $g.CopyFromScreen($x, $y, 0, 0, $b.Size); $b.Save((Join-Path $Shots "$name.png")); $g.Dispose(); $b.Dispose()
}
# The strip's first chip sits at the same spot however many rows it has.
function Click-FirstChip { $r = PanelRect; $s = [W]::GetDpiForWindow((Panel)) / 96.0; [W]::Click($r.L + [int](30 * $s), $r.T + [int](24 * $s)) }
function Chips { if (Test-Path $trace) { try { Get-Content $trace -Raw -Encoding UTF8 -ErrorAction Stop } catch { '' } } else { '' } }
function Replies { @((Chips) -split ' \| ' | Where-Object { $_ -and $_ -notmatch 'Smart replies|Reading the chat|Improve|Checking|^\S$' }) }
function Clear-Box { Keys '^a'; Start-Sleep -Milliseconds 80; Keys '{DEL}'; Start-Sleep -Milliseconds 350 }
function Start-Helper { Get-Process ErrChatKeyboard -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 300; Start-Process $exe | Out-Null }
function Hold-Alt($digit) { Guard; [System.Windows.Forms.SendKeys]::SendWait("%$digit"); Start-Sleep -Milliseconds 450 }

$chrome = (Get-Process chrome -ErrorAction SilentlyContinue | Where-Object Path | Select-Object -First 1).Path
if (-not $chrome) { $chrome = "$env:ProgramFiles\Google\Chrome\Application\chrome.exe" }
$page = 'file:///' + ((Join-Path $PSScriptRoot 'chat.html') -replace '\\','/')
$browser = $null; $classic = $null

try {
  Start-Helper
  $browser = Start-Process $chrome -ArgumentList "--user-data-dir=`"$chromeProfile`"","--no-first-run","--no-default-browser-check","--window-size=800,620","--window-position=100,60","--app=$page" -PassThru
  $found = WaitFor 20 { $script:h = Get-Process chrome -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like 'KBTEST*' } | Select-Object -First 1; $null -ne $script:h }
  if (-not $found) { throw 'Test page did not open.' }
  $script:win = $script:h.MainWindowHandle
  [W]::SetForegroundWindow($script:win) | Out-Null
  Start-Sleep -Seconds 1
  $r0 = New-Object W+RECT; [W]::GetWindowRect($script:win, [ref]$r0) | Out-Null
  [W]::Click($r0.L + 300, $r0.B - 90)  # click the message box

  "== Chrome, WhatsApp-style box"
  Check 'panel appears on an empty box' (WaitFor 8 { PanelShown })
  $ok = WaitFor 60 { (Replies).Count -ge 2 }
  Check 'replies appear by themselves, with no click' $ok (Chips)
  Start-Sleep -Milliseconds 300
  Shot '1-chat-replies'
  $first = Chips
  "      strip: $first"
  Check 'the fixed Okay/Sure/Thanks chips are gone' ($first -notmatch '(^| \| )(Okay|Sure|Thanks|Yes|No)( \| |$)') $first
  Check 'replies fit the chat' ($first -match 'cake|bring|party|coming|there|8|yes|sure|can') $first
  Check 'replies are short, like chips' (@(Replies | Where-Object { ($_ -split ' ').Count -gt 8 }).Count -eq 0) $first
  $reply1 = (Replies)[0]
  # Hovering the close button shows its tip, without taking focus.
  $r = PanelRect; $s = [W]::GetDpiForWindow((Panel)) / 96.0
  [W]::SetCursorPos($r.R - [int](24 * $s), $r.T + [int](24 * $s)) | Out-Null
  Start-Sleep -Milliseconds 500
  Shot '1a-tooltip'
  Check 'hovering the strip keeps focus in the app' ([W]::GetForegroundWindow() -eq $script:win)
  Click-FirstChip
  $ok = WaitFor 3 { (Field).Trim() -eq $reply1 }
  Check 'clicking a reply types it' $ok "field='$(Field)' expected='$reply1'"
  Check 'clicking the panel keeps focus in the app' ([W]::GetForegroundWindow() -eq $script:win)
  Check 'it is not sent automatically' ((Text) -match 'SENT\[0\]') (Text)

  "== replies follow the chat"
  Clear-Box
  $cached = WaitFor 4 { (Replies).Count -ge 2 -and (Chips) -notmatch 'Reading' }
  Check 'same chat reuses its replies, with no new AI call' ($cached -and (Chips) -eq $first) (Chips)
  Keys '{F9}'
  $ok = WaitFor 60 { $c = Chips; $c -ne $first -and $c -notmatch 'Reading' -and (Replies).Count -ge 2 }
  Check 'a new message brings new replies' $ok (Chips)
  Start-Sleep -Milliseconds 300
  Shot '1b-new-message'
  "      strip: $(Chips)"
  Check 'new replies answer the new message, not the old one' ((Chips) -match 'address|send|office|share|location' -and (Chips) -notmatch 'cake') (Chips)
  Hold-Alt 2
  $second = (Field).Trim()
  Check 'Alt+2 types the second reply' ($second.Length -gt 0 -and (Chips) -ne '') "field='$second'"
  Clear-Box
  Keys '{F10}'
  $ok = WaitFor 8 { (Replies).Count -eq 0 -and (Chips) -match 'Smart replies' }
  Check 'no AI call when the last message is mine' $ok (Chips)

  "== typing"
  Keys 'how '; Start-Sleep -Milliseconds 500
  Shot '2-next-word'
  Hold-Alt 1
  Check 'next word: "how " + Alt+1 -> "how are "' ((Field).Trim() -eq 'how are') (Field)
  Keys 'yo'; Start-Sleep -Milliseconds 500
  Shot '3-finish-word'
  Hold-Alt 1
  Check 'finish word: "yo" + Alt+1 -> "you"' ((Field).Trim() -eq 'how are you') (Field)

  Clear-Box
  Keys 'teh'; Start-Sleep -Milliseconds 500
  Hold-Alt 1
  Check 'typo: "teh" -> "the"' ((Field).Trim() -ceq 'the') (Field)

  "== grammar (Claude)"
  Clear-Box
  Keys 'i has went to the store yesterday'
  $ok = WaitFor 45 { (Chips) -match 'Fix:' }
  Check 'grammar fix appears after a pause' $ok (Chips)
  Start-Sleep -Milliseconds 300
  Shot '4-grammar'
  Keys '^%f'; Start-Sleep -Milliseconds 800
  $fixed = Field
  "      corrected to: $fixed"
  Check 'apply replaces the text' ($fixed -notmatch 'has went' -and $fixed -match 'store') $fixed

  Clear-Box
  Keys 'she dont know him+{ENTER}he dont know her'
  $ok = WaitFor 45 { (Chips) -match 'Fix:' }
  Check 'two-line message gets a fix' $ok (Chips)
  Keys '^%f'; Start-Sleep -Milliseconds 1200
  $two = Field
  "      corrected to: $two"
  Check 'apply keeps the line break' ($two -match '\\n') $two
  Check 'apply does not send the message' ((Text) -match 'SENT\[0\]') (Text)

  "== clicking Smart replies when the last message is mine"
  Clear-Box
  [void](WaitFor 5 { (Chips) -match '^\S*\s*Smart replies' })
  Click-FirstChip
  # Chrome can leave a stray line break in a cleared box; that isn't typed text.
  Check 'clicking Smart replies types nothing' (((Field) -replace '\\n', '') -eq '') (Field)
  $ok = WaitFor 60 { (Replies).Count -ge 2 }
  Check 'a click asks for replies anyway' $ok (Chips)
  "      strip: $(Chips)"
  Clear-Box

  "== password box"
  Clear-Box
  Keys '{TAB}'; Start-Sleep -Milliseconds 700
  Check 'panel hides on a password box' (WaitFor 3 { -not (PanelShown) })

  "== Codex"
  [W]::Click($r0.L + 300, $r0.B - 90); Start-Sleep -Milliseconds 300
  Set-Content (Join-Path $data 'settings.txt') "provider=codex`nautoGrammar=true`nlearnWords=true`nautoReplies=true"
  Remove-Item $trace -ErrorAction SilentlyContinue
  Start-Helper
  [void](WaitFor 8 { PanelShown })
  Clear-Box
  Keys '{F9}'
  $ok = WaitFor 90 { (Replies).Count -ge 2 }
  Check 'Codex writes replies from the chat' $ok (Chips)
  "      strip: $(Chips)"
  Keys 'they was going to home'
  $ok = WaitFor 60 { (Chips) -match 'Fix:' }
  Check 'Codex grammar fix appears' $ok (Chips)
  Keys '^%f'; Start-Sleep -Milliseconds 800
  "      corrected to: $(Field)"
  Check 'Codex fix applied' ((Field) -notmatch 'they was') (Field)

  "== classic Windows text box"
  Set-Content (Join-Path $data 'settings.txt') "provider=claude`nautoGrammar=false`nlearnWords=true`nautoReplies=true"
  Remove-Item $trace -ErrorAction SilentlyContinue
  Start-Helper
  $classicExe = Join-Path $data 'KbClassicTest.exe'
  & "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe "/out:$classicExe" /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $PSScriptRoot 'classic.cs') | Out-Null
  $classic = Start-Process $classicExe -PassThru
  $found = WaitFor 15 { $classic.Refresh(); $classic.MainWindowHandle -ne [IntPtr]::Zero }
  if (-not $found) { throw 'Classic test window did not open.' }
  $script:win = $classic.MainWindowHandle
  [W]::SetForegroundWindow($script:win) | Out-Null
  Check 'panel appears on a classic box' (WaitFor 8 { PanelShown })
  Start-Sleep -Seconds 4
  Check 'outside chat apps nothing is read until you click' ((Replies).Count -eq 0 -and (Chips) -notmatch 'Reading') (Chips)
  Keys 'thank '; Start-Sleep -Milliseconds 500
  Shot '6-classic'
  Hold-Alt 1
  Check 'classic box: "thank " + Alt+1 -> "thank you"' ((Field).Trim() -eq 'thank you') (Field)

  "== settings window"
  Start-Process $exe | Out-Null  # a second launch asks the running app to open settings
  $found = WaitFor 8 { $script:sw = [W]::FindWindow([NullString]::Value, 'ErrChat Keyboard settings'); $script:sw -ne [IntPtr]::Zero -and [W]::IsWindowVisible($script:sw) }
  Check 'opening the app again shows settings' $found
  if (-not $found) { throw 'Settings did not open.' }
  [W]::SetForegroundWindow($script:sw) | Out-Null
  Start-Sleep -Milliseconds 300
  $script:win = $script:sw
  Check 'the strip hides while settings is in front' (WaitFor 3 { -not (PanelShown) })
  $condition = New-Object Windows.Automation.AndCondition(
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'Check grammar as I type')),
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::CheckBox)))
  $switch = [Windows.Automation.AutomationElement]::FromHandle($script:sw).FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
  Check 'switches are visible to screen readers by name and role' ($null -ne $switch)
  if ($switch) {
    $switch.SetFocus(); Start-Sleep -Milliseconds 200
    Keys ' '; Start-Sleep -Milliseconds 400
    Check 'space flips a switch and saves it' ((Get-Content (Join-Path $data 'settings.txt') -Raw) -match 'autoGrammar=true')
  }
  Keys '{ESC}'
  Check 'Esc closes settings' (WaitFor 3 { -not [W]::IsWindowVisible($script:sw) })
  Check 'the app keeps running after settings closes' ($null -ne (Get-Process ErrChatKeyboard -ErrorAction SilentlyContinue))
}
catch { $script:fail++; "ERROR  $($_.Exception.Message)" }
finally {
  $chatLog = Get-Content "$trace.chat" -Encoding UTF8 -ErrorAction SilentlyContinue
  Get-Process ErrChatKeyboard -ErrorAction SilentlyContinue | Stop-Process -Force
  if ($classic -and -not $classic.HasExited) { Stop-Process -Id $classic.Id -Force -ErrorAction SilentlyContinue }
  Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -like "*$chromeProfile*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  Start-Sleep -Seconds 1
  Remove-Item $data, $chromeProfile -Recurse -Force -ErrorAction SilentlyContinue
}
"`nfailures: $script:fail   screenshots: $Shots"
if ($chatLog) { "chat readings:"; $chatLog | Select-Object -First 12 }
exit $script:fail
