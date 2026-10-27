"""
"View in Unity": opens the VR app as a normal desktop window on this PC.

The app is the Unity project's Windows build (Unity menu: BioGears > Build Desktop App
(Windows), written to <unity project>/Builds/Desktop/LungVisionVR.exe). Started with
-desktop it runs without a headset (mouse + keyboard, every slider, real BioGears), and
like the headset it shows the backend's active patient - so "View in Unity" makes the
patient active and starts the app if it isn't running yet; an open app switches to the
new patient by itself within a few seconds. Without a build, an open Unity Editor (in
Play mode) is used the same way.

The exe can be moved with the LUNGVISION_UNITY_EXE environment variable.
"""

from __future__ import annotations

import os
import subprocess
import sys

EXE_NAME = "LungVisionVR.exe"

# Default: the Unity project's build folder in the same repository
# (<repo>/backend/services -> <repo>/unity/Builds/Desktop).
_DEFAULT_EXE = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "..", "unity", "Builds", "Desktop", EXE_NAME))


def exe_path() -> str:
    return os.environ.get("LUNGVISION_UNITY_EXE", _DEFAULT_EXE)


def is_built() -> bool:
    return os.path.isfile(exe_path())


def _process_running(name: str) -> bool:
    if sys.platform != "win32":
        return False
    try:
        out = subprocess.run(["tasklist", "/FI", f"IMAGENAME eq {name}", "/NH"],
                             capture_output=True, text=True, timeout=10).stdout
    except (OSError, subprocess.SubprocessError):
        return False
    return name.lower() in out.lower()


def is_running() -> bool:
    """The desktop app is open."""
    return _process_running(os.path.basename(exe_path()))


def editor_running() -> bool:
    """The Unity Editor is open - in Play mode it follows the active patient like the
    headset and the desktop app do, so "View in Unity" can use it when there is no build."""
    return _process_running("Unity.exe")


def open_app(backend_url: str) -> bool:
    """Starts the desktop app unless it is already open. Returns True if it was started."""
    if sys.platform != "win32":
        raise RuntimeError("View in Unity needs the Windows desktop build (this backend isn't on Windows).")
    exe = exe_path()
    if not os.path.isfile(exe):
        raise FileNotFoundError(
            f"The Unity desktop app isn't built yet ({exe}). In Unity: BioGears > Build Desktop App (Windows).")
    if is_running():
        return False
    subprocess.Popen([exe, "-desktop", "-backend", backend_url],
                     cwd=os.path.dirname(exe),
                     creationflags=subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP,
                     close_fds=True)
    return True
