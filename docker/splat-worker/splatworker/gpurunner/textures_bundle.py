"""A textures job's bundle (the server's RunnerTexturesBundle): a zip of `textures-job.json` ({"version": 1,
"options": {...}} - the wall-geometry service's `options`), `geometry.json` (the solved document) and
`photos/<camera name>.jpg|png`. Unpacked into the layout wallgeometry.jobrender.render_job reads:
geometry.json, inputs.json ({"photos": {camera name: file}, "options": validated options}) and photo_<name>.<ext>.

Only those names are read; a path that escapes, any other entry, too many entries or too many bytes refuses the
bundle (a bundle comes from a server the key trusts, but a runner is a machine in somebody's home)."""
import json
import os
import re
import zipfile

JOB_FILE, GEOMETRY_FILE, PHOTO_DIR = "textures-job.json", "geometry.json", "photos/"
MAX_ENTRIES = 5000
MAX_BYTES = 24 << 30
MAX_DOC_BYTES = 256 << 20  # a geometry document of a big wall is a few MB
PHOTO = re.compile(r"^photos/([A-Za-z0-9_.\-]{1,100})\.(jpg|jpeg|png)$")


class TexturesBundleError(Exception):
    """The bundle is not a textures bundle (reported as a failure that no retry fixes)."""


def _copy(zf, info, dest):
    with zf.open(info) as src, open(dest, "wb") as out:
        while chunk := src.read(1 << 20):
            out.write(chunk)


def extract_textures_bundle(zip_path, dest):
    """Unpacks the bundle into `dest` for render_job; returns (photo count, validated options)."""
    from wallgeometry.textures import TextureError, validate_params
    os.makedirs(dest, exist_ok=True)
    photos, total = {}, 0
    try:
        with zipfile.ZipFile(zip_path) as zf:
            infos = [i for i in zf.infolist() if not i.is_dir()]
            if len(infos) > MAX_ENTRIES:
                raise TexturesBundleError(f"the bundle has more than {MAX_ENTRIES} entries")
            names = {i.filename: i for i in infos}
            if JOB_FILE not in names or GEOMETRY_FILE not in names:
                raise TexturesBundleError(f"the bundle lacks {JOB_FILE} or {GEOMETRY_FILE}")
            for info in infos:
                total += info.file_size
                if total > MAX_BYTES:
                    raise TexturesBundleError("the bundle is too large")
                if info.filename in (JOB_FILE, GEOMETRY_FILE):
                    if info.file_size > MAX_DOC_BYTES:
                        raise TexturesBundleError(f"{info.filename} is too large")
                elif (m := PHOTO.match(info.filename)) is None or ".." in info.filename:
                    raise TexturesBundleError(f"the bundle has an unexpected file: {info.filename[:80]}")
            job = json.loads(zf.read(JOB_FILE))
            options = validate_params(job.get("options") if isinstance(job, dict) else None)
            _copy(zf, names[GEOMETRY_FILE], os.path.join(dest, "geometry.json"))
            for info in infos:
                if (m := PHOTO.match(info.filename)) is not None:
                    file = f"photo_{m.group(1)}.{m.group(2)}"
                    _copy(zf, info, os.path.join(dest, file))
                    photos[m.group(1)] = file
    except (zipfile.BadZipFile, ValueError, TextureError) as e:
        raise TexturesBundleError(f"bad textures bundle: {e}") from e
    if not photos:
        raise TexturesBundleError("the bundle has no photos")
    with open(os.path.join(dest, "inputs.json"), "w") as fh:
        json.dump({"photos": photos, "options": options}, fh)
    return len(photos), options
