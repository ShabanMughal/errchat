# How it works

ErrChat Keyboard is a single native exe, built with the C# compiler included in Windows.
It has no server.

| File | Role |
| --- | --- |
| `src/FocusWatcher.cs` | Polls the focused box through UI Automation every 150 ms, on its own thread. |
| `src/Conversation.cs` | Reads the chat on screen and works out who said what. |
| `src/Predictor.cs` | Offline word suggestions, typo fixes and learned words. |
| `src/TextDiff.cs` | Finds which words a grammar fix changed, so the strip can mark them. |
| `src/Panel.cs` | The floating strip, which never takes focus. Also the color theme. |
| `src/SettingsWindow.cs` | The settings window. |
| `src/Typer.cs` | Types into the app in front with `SendInput`. |
| `src/Ai.cs` | One-shot `claude -p` and `codex exec` calls with no tools. |
| `src/Program.cs` | Decides what the strip shows; tray icon and saved settings. |
| `src/SelfTest.cs` | `--selftest`, `--preview` and `--probe-chat`. |

## Decisions that aren't obvious from the code

- **Two speeds.** A CLI call takes seconds, so anything shown while typing is local. Only
  grammar fixes and smart replies use the plan.
- **Chrome's text ranges can't be trusted.** On an empty box, `DocumentRange` returns the
  box's label ("Type a message"), and moving a range backwards walks out of the box into
  the rest of the page. So `ValuePattern` decides what the box contains, and ranges are
  clipped to it. Otherwise other people's messages would be learned as the user's words.
- **Replies come from the chat, not a fixed list.** WhatsApp Web barely labels its messages
  for UI Automation, so `Conversation.cs` walks the page's text lines and uses each line's
  position: left is them, right is me, and centered lines and timestamps are dropped.
  `--probe-chat` checks this on a real open chat and prints counts only.
- **Automatic replies protect quota and privacy.** Chat text is only read by itself in
  known chat apps, only while the box is empty. The AI is only called when the latest
  messages changed and the last one isn't the user's, at most once every 8 seconds, and
  results are cached per chat state.
- **AI calls are locked down.** Both CLIs run with tools, MCP servers and session history
  turned off, and API-key variables are removed from their environment so the user's
  subscription is what gets used.
- **Never type a newline.** It would press Enter and send the message. Multi-line fixes are
  pasted instead, and the old clipboard text is put back.
- **No keyboard hook.** Text is read from the box on demand, so nothing is captured outside
  it and password boxes are never read.
- **The strip is drawn by hand.** It's a window with `WS_EX_NOACTIVATE` that answers
  `WM_MOUSEACTIVATE` with `MA_NOACTIVATE`, so a click can't move focus out of the other
  app. Real buttons would try to take focus.
- **Text uses `TextRenderer`, not GDI+.** Only native Windows text drawing falls back to
  Segoe UI Emoji, so emoji in a reply don't turn into empty boxes.
- **The first run opens Settings.** Both automatic features send text by default, so people
  see that and can turn them off.
