#!/usr/bin/env python3
"""Drive the Rambutan desktop app on a virtual display: launch, click, type, screenshot.

For cloud sessions and CI, where there is no screen and usually no route to YouTube
Music. It pairs with demo mode (RAMBUTAN_DEMO, see src/Rambutan.App/Services/Demo),
which gives the app recorded content, generated audio and placeholder artwork.

Coordinates are relative to the app window's top-left corner, which is also where a
screenshot from this tool starts, so a point read off a screenshot can be clicked as is.

    ui.py start [--anonymous] [--latency MS] [--fail AREAS] [--no-build]
    ui.py shot NAME                 # → $UI_SHOTS/NAME.png (default ./ui-shots)
    ui.py click X Y [--double] [--right] [--button N]   # 8/9: mouse back/forward
    ui.py scroll X Y CLICKS         # wheel at X,Y: positive scrolls down
    ui.py move X Y
    ui.py drag X1 Y1 X2 Y2          # left-button drag, in small steps
    ui.py key Control_L+f           # X keysyms joined by +: Return, Escape, F1, Left …
    ui.py type "daft punk"
    ui.py wait SECONDS
    ui.py stop
    ui.py run "click 80 100" "wait 1" "shot home"   # several steps in one call

Needs Xvfb, and the Python packages pillow and python-xlib
(.claude/hooks/session-start.sh installs them in cloud sessions).
"""
import argparse
import os
import shlex
import signal
import subprocess
import sys
import time
from pathlib import Path

DISPLAY = os.environ.get("UI_DISPLAY", ":99")
SCREEN = "1400x900x24"
REPO = Path(__file__).resolve().parents[2]
STATE = Path(os.environ.get("UI_STATE", "/tmp/rambutan-ui"))
SHOTS = Path(os.environ.get("UI_SHOTS", "ui-shots"))


def _display():
    from Xlib import display
    return display.Display(DISPLAY)


def _app_window(d):
    """(x, y, width, height) of the Rambutan main window on the root, or None."""
    root = d.screen().root
    best = None
    for w in root.query_tree().children:
        try:
            name = w.get_wm_name() or ""
            attrs = w.get_attributes()
        except Exception:
            continue
        if "Rambutan" not in name or attrs.map_state == 0:
            continue
        g = w.get_geometry()
        pos = w.translate_coords(root, 0, 0)
        rect = (-pos.x, -pos.y, g.width, g.height)
        # Dialogs are windows too; the main window is the largest.
        if best is None or g.width * g.height > best[2] * best[3]:
            best = rect
    return best


def _origin(d):
    rect = _app_window(d)
    if rect is None:
        sys.exit("No Rambutan window on the display; run `ui.py start` first.")
    return rect[0], rect[1]


def _display_running():
    """True when an X server already holds UI_DISPLAY. Checked per display, not
    "is any Xvfb running", so sessions on different displays don't skip each
    other's servers."""
    number = DISPLAY.lstrip(":").split(".")[0]
    return Path(f"/tmp/.X{number}-lock").exists() or Path(f"/tmp/.X11-unix/X{number}").exists()


def start(args):
    STATE.mkdir(parents=True, exist_ok=True)
    if not _display_running():
        subprocess.Popen(["Xvfb", DISPLAY, "-screen", "0", SCREEN],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                         start_new_session=True)
        for _ in range(20):
            if _display_running():
                break
            time.sleep(0.25)
        time.sleep(0.5)

    if not args.no_build:
        subprocess.run(["dotnet", "build", str(REPO / "src/Rambutan.App"), "-v", "q", "-nologo"],
                       check=True)

    env = dict(os.environ, DISPLAY=DISPLAY,
               RAMBUTAN_DEMO="anonymous" if args.anonymous else "1")
    if args.latency is not None:
        env["RAMBUTAN_DEMO_LATENCY"] = str(args.latency)
    if args.fail:
        env["RAMBUTAN_DEMO_FAIL"] = args.fail
    log = open(STATE / "app.log", "w")
    proc = subprocess.Popen(
        ["dotnet", "run", "--no-build", "--project", str(REPO / "src/Rambutan.App")],
        env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
    (STATE / "app.pid").write_text(str(proc.pid))

    d = _display()
    for _ in range(120):
        if _app_window(d):
            time.sleep(2)  # first layout and the Home fetch
            print(f"Rambutan is up on {DISPLAY} (log: {STATE / 'app.log'})")
            return
        if proc.poll() is not None:
            sys.exit(f"The app exited early; see {STATE / 'app.log'}")
        time.sleep(0.5)
    sys.exit("Timed out waiting for the Rambutan window.")


def stop(_args):
    """Stops this state directory's app only: `dotnet run` was started in its own
    process group, which the app it launches shares, so the group is exactly this
    session's processes. (A `pkill -x Rambutan.App` here used to stop every
    session's app on the machine.)"""
    pid_file = STATE / "app.pid"
    if not pid_file.exists():
        print(f"nothing to stop (no {pid_file})")
        return
    group = int(pid_file.read_text())
    try:
        os.killpg(group, signal.SIGTERM)
        for _ in range(20):
            time.sleep(0.25)
            os.killpg(group, 0)  # raises once the whole group is gone
        os.killpg(group, signal.SIGKILL)
    except ProcessLookupError:
        pass
    pid_file.unlink()
    print("stopped")


def shot(args):
    from PIL import ImageGrab
    d = _display()
    rect = _app_window(d)
    image = ImageGrab.grab(xdisplay=DISPLAY)
    if rect:
        x, y, w, h = rect
        image = image.crop((x, y, x + w, y + h))
    SHOTS.mkdir(parents=True, exist_ok=True)
    path = SHOTS / f"{args.name}.png"
    image.save(path)
    print(path)


def _move(d, x, y):
    from Xlib import X
    from Xlib.ext import xtest
    ox, oy = _origin(d)
    xtest.fake_input(d, X.MotionNotify, x=ox + x, y=oy + y)
    d.sync()


def move(args):
    _move(_display(), args.x, args.y)


def click(args):
    from Xlib import X
    from Xlib.ext import xtest
    d = _display()
    _move(d, args.x, args.y)
    time.sleep(0.15)
    button = args.button or (3 if args.right else 1)
    for _ in range(2 if args.double else 1):
        xtest.fake_input(d, X.ButtonPress, button)
        d.sync()
        time.sleep(0.04)
        xtest.fake_input(d, X.ButtonRelease, button)
        d.sync()
        time.sleep(0.06)


def drag(args):
    from Xlib import X
    from Xlib.ext import xtest
    d = _display()
    _move(d, args.x1, args.y1)
    time.sleep(0.15)
    xtest.fake_input(d, X.ButtonPress, 1)
    d.sync()
    steps = 12
    for i in range(1, steps + 1):
        time.sleep(0.03)
        _move(d, args.x1 + (args.x2 - args.x1) * i // steps, args.y1 + (args.y2 - args.y1) * i // steps)
    time.sleep(0.1)
    xtest.fake_input(d, X.ButtonRelease, 1)
    d.sync()


def scroll(args):
    from Xlib import X
    from Xlib.ext import xtest
    d = _display()
    _move(d, args.x, args.y)
    time.sleep(0.1)
    button = 5 if args.clicks > 0 else 4
    for _ in range(abs(args.clicks)):
        xtest.fake_input(d, X.ButtonPress, button)
        xtest.fake_input(d, X.ButtonRelease, button)
        d.sync()
        time.sleep(0.03)


def _keys(d, names):
    from Xlib import X, XK
    from Xlib.ext import xtest
    codes = []
    for name in names:
        sym = XK.string_to_keysym(name)
        if sym == 0:
            sys.exit(f"Unknown key name: {name}")
        codes.append(d.keysym_to_keycode(sym))
    for c in codes:
        xtest.fake_input(d, X.KeyPress, c)
    for c in reversed(codes):
        xtest.fake_input(d, X.KeyRelease, c)
    d.sync()


def key(args):
    _keys(_display(), args.combo.split("+"))


def type_(args):
    d = _display()
    names = {" ": "space", "-": "minus", ".": "period", ",": "comma", "'": "apostrophe",
             "=": "equal", ";": "semicolon", "/": "slash", "\\": "backslash",
             "[": "bracketleft", "]": "bracketright", "`": "grave"}
    # Xvfb's keymap is US: these are the shifted keys, typed as Shift + base key.
    shifted = {":": "semicolon", '"': "apostrophe", "_": "minus", "+": "equal",
               "?": "slash", "<": "comma", ">": "period", "{": "bracketleft",
               "}": "bracketright", "|": "backslash", "~": "grave", "!": "1", "@": "2",
               "#": "3", "$": "4", "%": "5", "^": "6", "&": "7", "*": "8", "(": "9", ")": "0"}
    for ch in args.text:
        if ch.isupper():
            _keys(d, ["Shift_L", ch.lower()])
        elif ch in shifted:
            _keys(d, ["Shift_L", shifted[ch]])
        else:
            _keys(d, [names.get(ch, ch)])
        time.sleep(0.03)


def wait(args):
    time.sleep(args.seconds)


def parser():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("start")
    s.add_argument("--anonymous", action="store_true", help="demo signed out")
    s.add_argument("--latency", type=int, help="ms added to each catalog response")
    s.add_argument("--fail", help="areas that fail as if offline, e.g. home,search or all")
    s.add_argument("--no-build", action="store_true")
    s.set_defaults(fn=start)

    sub.add_parser("stop").set_defaults(fn=stop)

    s = sub.add_parser("shot"); s.add_argument("name"); s.set_defaults(fn=shot)
    s = sub.add_parser("move"); s.add_argument("x", type=int); s.add_argument("y", type=int); s.set_defaults(fn=move)
    s = sub.add_parser("click")
    s.add_argument("x", type=int); s.add_argument("y", type=int)
    s.add_argument("--double", action="store_true"); s.add_argument("--right", action="store_true")
    s.add_argument("--button", type=int, help="X button number; 8 and 9 are the mouse's back/forward")
    s.set_defaults(fn=click)
    s = sub.add_parser("drag")
    for name in ("x1", "y1", "x2", "y2"):
        s.add_argument(name, type=int)
    s.set_defaults(fn=drag)
    s = sub.add_parser("scroll")
    s.add_argument("x", type=int); s.add_argument("y", type=int); s.add_argument("clicks", type=int)
    s.set_defaults(fn=scroll)
    s = sub.add_parser("key"); s.add_argument("combo"); s.set_defaults(fn=key)
    s = sub.add_parser("type"); s.add_argument("text"); s.set_defaults(fn=type_)
    s = sub.add_parser("wait"); s.add_argument("seconds", type=float); s.set_defaults(fn=wait)
    s = sub.add_parser("run"); s.add_argument("steps", nargs="+"); s.set_defaults(fn=None)
    return p


def main(argv):
    p = parser()
    args = p.parse_args(argv)
    if args.cmd == "run":
        for step in args.steps:
            main(shlex.split(step))
        return
    args.fn(args)


if __name__ == "__main__":
    main(sys.argv[1:])
