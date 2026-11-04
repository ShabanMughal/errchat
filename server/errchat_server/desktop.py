"""errchat for desktop apps (WhatsApp, Teams, Outlook, ...) on Windows.

Type a message in any app, press a hotkey, and it is replaced with a better
version. Run with:  python -m errchat_server.desktop

How it works:
1. A global hotkey fires (RegisterHotKey, no keyboard hook).
2. Your selection is copied with Ctrl+C. If nothing is selected, Ctrl+A selects
   the whole input box first.
3. The text goes to the errchat server's /correct endpoint (started in this
   process if it isn't already running).
4. The result is pasted over the selection with Ctrl+V, and your old clipboard
   is put back. Ctrl+Z in the app undoes it.

A small "Improving..." badge near the bottom-right of the screen shows progress
and errors. It never takes keyboard focus, so the paste goes to the right app.
"""

import ctypes
import json
import logging
import queue
import threading
import time
import tkinter as tk
import urllib.error
import urllib.request
from ctypes import wintypes

from errchat_server.config import load_settings

logger = logging.getLogger("errchat.desktop")

# hotkey id -> (modifiers, virtual key, label, request options)
MOD_ALT, MOD_CONTROL, MOD_NOREPEAT = 0x0001, 0x0002, 0x4000
HOTKEYS = {
    1: (MOD_CONTROL | MOD_ALT, ord("G"), "Ctrl+Alt+G", {"mode": "improve", "tone": "match"}),
    2: (MOD_CONTROL | MOD_ALT, ord("P"), "Ctrl+Alt+P", {"mode": "improve", "tone": "formal"}),
    3: (MOD_CONTROL | MOD_ALT, ord("F"), "Ctrl+Alt+F", {"mode": "fix", "tone": "match"}),
}
HOTKEY_HELP = {
    1: "better version, same tone",
    2: "better version, professional",
    3: "fix grammar and spelling only",
}

# --- Win32 bindings ---------------------------------------------------------

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

user32.RegisterHotKey.argtypes = [wintypes.HWND, ctypes.c_int, wintypes.UINT, wintypes.UINT]
user32.GetMessageW.argtypes = [ctypes.POINTER(wintypes.MSG), wintypes.HWND, wintypes.UINT, wintypes.UINT]
user32.GetForegroundWindow.restype = wintypes.HWND
user32.GetAsyncKeyState.restype = ctypes.c_short
user32.keybd_event.argtypes = [wintypes.BYTE, wintypes.BYTE, wintypes.DWORD, ctypes.c_void_p]
user32.OpenClipboard.argtypes = [wintypes.HWND]
user32.GetClipboardData.restype = ctypes.c_void_p
user32.SetClipboardData.argtypes = [wintypes.UINT, ctypes.c_void_p]
user32.SetClipboardData.restype = ctypes.c_void_p
user32.IsClipboardFormatAvailable.argtypes = [wintypes.UINT]
user32.GetClipboardSequenceNumber.restype = wintypes.DWORD
user32.GetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int]
user32.SetWindowLongW.argtypes = [wintypes.HWND, ctypes.c_int, ctypes.c_long]
user32.GetParent.argtypes = [wintypes.HWND]
user32.GetParent.restype = wintypes.HWND
user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
kernel32.GlobalAlloc.argtypes = [wintypes.UINT, ctypes.c_size_t]
kernel32.GlobalAlloc.restype = ctypes.c_void_p
kernel32.GlobalLock.argtypes = [ctypes.c_void_p]
kernel32.GlobalLock.restype = ctypes.c_void_p
kernel32.GlobalUnlock.argtypes = [ctypes.c_void_p]
kernel32.GlobalFree.argtypes = [ctypes.c_void_p]

WM_HOTKEY = 0x0312
CF_UNICODETEXT = 13
GMEM_MOVEABLE = 0x0002
KEYEVENTF_KEYUP = 0x0002
VK_CONTROL, VK_SHIFT, VK_MENU, VK_LWIN, VK_RWIN = 0x11, 0x10, 0x12, 0x5B, 0x5C
GWL_EXSTYLE = -20
WS_EX_TOPMOST, WS_EX_TOOLWINDOW, WS_EX_NOACTIVATE = 0x8, 0x80, 0x08000000
SW_SHOWNOACTIVATE, SW_HIDE = 4, 0


def _wait_for_modifiers_released(timeout: float = 3.0) -> None:
    """The hotkey's Ctrl/Alt are still down when it fires; sending Ctrl+C now
    would arrive as Ctrl+Alt+C."""
    end = time.monotonic() + timeout
    keys = (VK_CONTROL, VK_SHIFT, VK_MENU, VK_LWIN, VK_RWIN)
    while time.monotonic() < end:
        if not any(user32.GetAsyncKeyState(k) & 0x8000 for k in keys):
            return
        time.sleep(0.02)


def _press_combo(*vks: int) -> None:
    for vk in vks:
        user32.keybd_event(vk, 0, 0, None)
    for vk in reversed(vks):
        user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, None)


def _open_clipboard() -> bool:
    for _ in range(20):  # another app may hold it for a moment
        if user32.OpenClipboard(None):
            return True
        time.sleep(0.02)
    return False


def get_clipboard_text() -> str | None:
    if not user32.IsClipboardFormatAvailable(CF_UNICODETEXT) or not _open_clipboard():
        return None
    try:
        handle = user32.GetClipboardData(CF_UNICODETEXT)
        if not handle:
            return None
        ptr = kernel32.GlobalLock(handle)
        try:
            return ctypes.wstring_at(ptr)
        finally:
            kernel32.GlobalUnlock(handle)
    finally:
        user32.CloseClipboard()


def set_clipboard_text(text: str) -> bool:
    data = ctypes.create_unicode_buffer(text)
    size = ctypes.sizeof(data)
    if not _open_clipboard():
        return False
    try:
        user32.EmptyClipboard()
        handle = kernel32.GlobalAlloc(GMEM_MOVEABLE, size)
        ptr = kernel32.GlobalLock(handle)
        ctypes.memmove(ptr, data, size)
        kernel32.GlobalUnlock(handle)
        if not user32.SetClipboardData(CF_UNICODETEXT, handle):
            kernel32.GlobalFree(handle)
            return False
        return True  # the clipboard owns the memory now
    finally:
        user32.CloseClipboard()


def _copy_and_wait(timeout: float = 0.6) -> bool:
    """Send Ctrl+C and wait for the clipboard to change."""
    before = user32.GetClipboardSequenceNumber()
    _press_combo(VK_CONTROL, ord("C"))
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if user32.GetClipboardSequenceNumber() != before:
            time.sleep(0.03)  # let the app finish writing all formats
            return True
        time.sleep(0.02)
    return False


# --- server -----------------------------------------------------------------


class ServerError(Exception):
    pass


_ERROR_HINTS = {
    "auth": "Claude CLI is not logged in. Run: claude login",
    "not_found": "Claude CLI not found. Install Claude Code first.",
    "timeout": "Claude took too long. Try again.",
}


def call_correct(base_url: str, text: str, options: dict, timeout: float) -> str:
    body = json.dumps({"text": text, **options}).encode()
    req = urllib.request.Request(
        f"{base_url}/correct", data=body, headers={"Content-Type": "application/json"}
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.load(resp)["corrected"]
    except urllib.error.HTTPError as e:
        try:
            detail = json.load(e).get("detail", {})
        except ValueError:
            detail = {}
        if isinstance(detail, dict) and detail.get("kind") in _ERROR_HINTS:
            raise ServerError(_ERROR_HINTS[detail["kind"]]) from e
        if e.code == 422:
            raise ServerError("Message is too long (max 4000 characters).") from e
        msg = detail.get("message") if isinstance(detail, dict) else None
        raise ServerError(msg or f"Server error {e.code}") from e
    except (urllib.error.URLError, TimeoutError, OSError) as e:
        raise ServerError("Can't reach the errchat server.") from e


def server_is_up(base_url: str) -> bool:
    try:
        with urllib.request.urlopen(f"{base_url}/health", timeout=1) as resp:
            return resp.status == 200
    except (urllib.error.URLError, TimeoutError, OSError):
        return False


def start_server_in_background(base_url: str) -> None:
    import uvicorn

    from errchat_server.main import create_app

    settings = load_settings()
    config = uvicorn.Config(
        create_app(settings), host=settings.host, port=settings.port, log_level="warning"
    )
    threading.Thread(target=uvicorn.Server(config).run, daemon=True, name="server").start()
    for _ in range(50):
        if server_is_up(base_url):
            return
        time.sleep(0.1)
    raise SystemExit(f"errchat server did not start on {base_url}")


# --- status badge (never takes focus) ----------------------------------------


class Badge:
    COLORS = {"busy": "#2b6cb0", "ok": "#2f855a", "error": "#c53030", "info": "#4a5568"}

    def __init__(self, root: tk.Tk):
        self.root = root
        self.root.overrideredirect(True)
        self.root.attributes("-topmost", True)
        self.label = tk.Label(
            root, font=("Segoe UI", 10), fg="white", padx=12, pady=6, wraplength=360, justify="left"
        )
        self.label.pack()
        self.root.withdraw()
        self.root.update_idletasks()
        self.hwnd = user32.GetParent(self.root.winfo_id()) or self.root.winfo_id()
        style = user32.GetWindowLongW(self.hwnd, GWL_EXSTYLE)
        user32.SetWindowLongW(
            self.hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST
        )
        self._hide_job = None

    def show(self, kind: str, text: str, hide_after_ms: int | None = None) -> None:
        if self._hide_job:
            self.root.after_cancel(self._hide_job)
            self._hide_job = None
        self.label.config(text=text, bg=self.COLORS[kind])
        self.root.config(bg=self.COLORS[kind])
        self.root.update_idletasks()
        w, h = self.root.winfo_reqwidth(), self.root.winfo_reqheight()
        x = self.root.winfo_screenwidth() - w - 24
        y = self.root.winfo_screenheight() - h - 72  # above the taskbar
        self.root.geometry(f"+{x}+{y}")
        user32.ShowWindow(self.hwnd, SW_SHOWNOACTIVATE)
        if hide_after_ms:
            self._hide_job = self.root.after(hide_after_ms, self.hide)

    def hide(self) -> None:
        self._hide_job = None
        user32.ShowWindow(self.hwnd, SW_HIDE)


# --- main flow ----------------------------------------------------------------


class App:
    def __init__(self, base_url: str, timeout: float):
        self.base_url = base_url
        self.timeout = timeout
        self.events: queue.Queue = queue.Queue()  # (kind, text, hide_after_ms) for the badge
        self.busy = threading.Lock()

    def status(self, kind: str, text: str, hide_after_ms: int | None = None) -> None:
        if kind == "error":
            logger.warning(text)
        self.events.put((kind, text, hide_after_ms))

    def on_hotkey(self, hotkey_id: int) -> None:
        if not self.busy.acquire(blocking=False):
            return  # already working on one
        threading.Thread(target=self._run, args=(hotkey_id,), daemon=True).start()

    def _run(self, hotkey_id: int) -> None:
        try:
            self._rewrite(HOTKEYS[hotkey_id][3])
        except Exception:  # keep the hotkey alive no matter what
            logger.exception("Rewrite failed")
            self.status("error", "Something went wrong. See the errchat window.", 5000)
        finally:
            self.busy.release()

    def _rewrite(self, options: dict) -> None:
        window = user32.GetForegroundWindow()
        _wait_for_modifiers_released()
        saved = get_clipboard_text()

        if not _copy_and_wait():
            # nothing selected: take the whole input box
            _press_combo(VK_CONTROL, ord("A"))
            time.sleep(0.05)
            if not _copy_and_wait():
                self.status("error", "No text found. Click in the message box first.", 3500)
                return

        text = (get_clipboard_text() or "").strip()
        if not text:
            self.status("error", "No text found. Click in the message box first.", 3500)
            self._restore(saved)
            return

        self.status("busy", "✨ Improving your message…")
        try:
            better = call_correct(self.base_url, text, options, self.timeout).strip()
        except ServerError as e:
            self.status("error", str(e), 6000)
            self._restore(saved)
            return

        if not better or better == text:
            self.status("ok", "✓ Looks good already", 2000)
            self._restore(saved)
            return

        if user32.GetForegroundWindow() != window:
            # you switched apps while waiting; don't paste into the wrong one
            set_clipboard_text(better)
            self.status("info", "Done. You switched windows, so it's on your clipboard: press Ctrl+V.", 6000)
            return

        set_clipboard_text(better)
        _press_combo(VK_CONTROL, ord("V"))
        time.sleep(0.4)  # let the app read the clipboard before we put the old one back
        self._restore(saved)
        self.status("ok", "✓ Replaced (Ctrl+Z to undo)", 2000)

    @staticmethod
    def _restore(saved: str | None) -> None:
        if saved is not None:
            set_clipboard_text(saved)


def hotkey_loop(app: App, registered: threading.Event, failed: list) -> None:
    """RegisterHotKey binds hotkeys to the calling thread, so this thread owns
    them and pumps their messages."""
    for hid, (mods, vk, label, _) in HOTKEYS.items():
        if not user32.RegisterHotKey(None, hid, mods | MOD_NOREPEAT, vk):
            failed.append(label)
    registered.set()
    msg = wintypes.MSG()
    while user32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
        if msg.message == WM_HOTKEY:
            app.on_hotkey(msg.wParam)


def main() -> None:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    settings = load_settings()
    base_url = f"http://{settings.host}:{settings.port}"

    if server_is_up(base_url):
        logger.info("Using the errchat server already running at %s", base_url)
    else:
        logger.info("Starting the errchat server at %s (model: %s)", base_url, settings.model)
        start_server_in_background(base_url)

    app = App(base_url, timeout=settings.timeout_seconds + 10)
    registered, failed = threading.Event(), []
    threading.Thread(target=hotkey_loop, args=(app, registered, failed), daemon=True).start()
    registered.wait()

    print("\nerrchat is running. In WhatsApp, Teams or any app, type a message, then press:")
    for hid, (_, _, label, _) in HOTKEYS.items():
        status = "  (taken by another app!)" if label in failed else ""
        print(f"  {label}  {HOTKEY_HELP[hid]}{status}")
    print("Select part of the text to change only that part. Close this window to stop.\n")
    if len(failed) == len(HOTKEYS):
        raise SystemExit("No hotkeys could be registered.")

    root = tk.Tk()
    badge = Badge(root)

    def pump() -> None:
        try:
            while True:
                badge.show(*app.events.get_nowait())
        except queue.Empty:
            pass
        root.after(50, pump)

    pump()
    try:
        root.mainloop()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
