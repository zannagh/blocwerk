"""File names that a bundle unpacks to, checked against the file system it lands on (bundle.py, textures_bundle.py).

A bundle is built on Linux, where `IMG_1.jpg` and `img_1.jpg` are two files; on macOS (APFS, case-insensitive by
default) and Windows (NTFS) the second overwrites the first, silently, and a COLMAP model then points at the wrong
photo. Windows also refuses device names (CON, NUL, COM1, ... with any extension), names ending in a dot or a space
and the characters <>:"|?*. Such a bundle is not broken, it just cannot be unpacked HERE: BundleNotPortable is a
retryable failure of this runner (another runner, e.g. a Linux one, can take the job)."""
import os
import re
import sys
import tempfile
import unicodedata

RESERVED = re.compile(r"^(con|prn|aux|nul|com[0-9¹²³]|lpt[0-9¹²³])(\..*)?$", re.I)
BAD_CHARS = re.compile(r'[<>:"|?*\x00-\x1f]')


class BundleNotPortable(Exception):
    """The bundle's names collide or are invalid on this machine's file system (retryable elsewhere)."""


def windows_problem(part):
    """Why one path component cannot exist on Windows, or None."""
    if RESERVED.match(part):
        return f"{part!r} is a reserved device name on Windows"
    if part.endswith((".", " ")):
        return f"{part!r} ends in a dot or a space"
    if BAD_CHARS.search(part):
        return f"{part!r} has a character Windows does not allow"
    return None


def fold(name, case_insensitive, norm_insensitive):
    """The key under which this file system finds `name`: NFC when it ignores Unicode normalization (APFS: "é" composed
    and decomposed are one file), lower-cased when it ignores case. lower(), not casefold(): no file system folds
    "ß" to "ss", so casefold() would report collisions that are not."""
    key = unicodedata.normalize("NFC", name) if norm_insensitive else name
    return key.lower() if case_insensitive else key


def name_problem(names, windows, case_insensitive, norm_insensitive=False):
    """The first reason the archive names (POSIX '/' separated) cannot be unpacked side by side, or None."""
    seen = {}
    for name in names:
        if windows:
            for part in name.split("/"):
                why = windows_problem(part)
                if why:
                    return why
        if case_insensitive or norm_insensitive:
            key = fold(name, case_insensitive, norm_insensitive)
            if key in seen and seen[key] != name:
                return f"{seen[key]!r} and {name!r} are the same file here (case or Unicode normalization)"
            seen.setdefault(key, name)
    return None


def is_case_insensitive(directory):
    """Whether files in `directory` are found under another case (APFS / NTFS defaults); probed with a temp file."""
    try:
        os.makedirs(directory, exist_ok=True)
        fd, path = tempfile.mkstemp(prefix="CaseProbe-", dir=directory)
        os.close(fd)
    except OSError:
        return sys.platform in ("win32", "darwin")
    try:
        head, tail = os.path.split(path)
        return os.path.exists(os.path.join(head, tail.swapcase()))
    finally:
        try:
            os.remove(path)
        except OSError:
            pass


def check_names(names, directory, platform=None):
    """Raises BundleNotPortable when `names` cannot all be unpacked into `directory` on this machine."""
    platform = platform or sys.platform
    why = name_problem(names, platform == "win32", is_case_insensitive(directory), norm_insensitive=platform == "darwin")
    if why:
        raise BundleNotPortable(f"this runner's file system cannot hold the bundle: {why}")
