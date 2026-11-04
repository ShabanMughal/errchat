# Contributing

Bug reports and pull requests are welcome.

## Reporting a bug

Say which app you were typing in and what the strip showed. **Don't paste private
messages, and check any screenshot for them before you attach it.** To report a problem
with chat reading, run this with the chat open. It prints counts only, never message text:

```powershell
$out = "$env:TEMP\probe.txt"
Start-Process dist\ErrChatKeyboard.exe --probe-chat -Wait -RedirectStandardOutput $out
Get-Content $out
```

## Building and testing

The app is built as a Windows program with no console, so redirect its output to a file
to see test results:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1

$out = "$env:TEMP\selftest.txt"
$run = Start-Process dist\ErrChatKeyboard.exe --selftest -Wait -PassThru -RedirectStandardOutput $out
Get-Content $out
$run.ExitCode   # 0 means every check passed
```

Before opening a pull request:

1. Run `--selftest`, and add checks for any logic you change.
2. For a visual change, run `--preview <folder>` and look at the images, in both light
   and dark. Update the images in `docs/` if the look changed.
3. If you changed focus, typing, chat reading or AI calls, run `test\e2e.ps1`. It takes
   over the keyboard and mouse for a few minutes and uses a little of your AI plan, so
   leave the PC alone while it runs.

## Rules worth keeping

- **Only C# 5.** The compiler built into Windows doesn't support anything newer, and not
  needing an SDK is a feature. That rules out `?.`, `$""` and `=>` members.
- **No new dependencies or services.** The app is a single exe with no server.
- **The strip must never take focus.** Clicking it has to leave the caret in the other app.
- **Never type a newline into another app.** It would send the message.
- **Be careful with anything that reads or sends text.** Keep the automatic paths narrow,
  and update the Privacy section of the README when they change.
- **Only use sample text** in tests and screenshots, never real messages.
