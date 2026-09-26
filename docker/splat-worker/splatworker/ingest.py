"""Photo hygiene. Phone photos carry GPS and device metadata: NOTHING of it is persisted or forwarded.

`sanitize()` runs on arrival (in the upload handler, before anything touches the disk): decode,
apply the EXIF orientation, read the two numbers the pipeline needs (35 mm-equivalent focal length,
and an opaque hash of make/model/lens to group photos that share a lens), then re-encode the pixels
into a brand-new JPEG with no EXIF, XMP, ICC, comments or thumbnails. The original bytes are dropped.
"""
import hashlib
import io
import warnings

from computejobs.settings import settings
from PIL import Image, ImageOps

# Pillow only WARNS between MAX_IMAGE_PIXELS and 2x that; sanitize() checks the header size itself,
# before decoding, so anything above settings.max_image_pixels (100 MP) is refused outright.
Image.MAX_IMAGE_PIXELS = settings.max_image_pixels
# ... and the warn-only window (1x..2x the limit) is an error too, in case anything opens an image elsewhere.
warnings.simplefilter("error", Image.DecompressionBombWarning)
ACCEPTED_FORMATS = {"JPEG", "PNG", "WEBP", "TIFF", "MPO"}
EXIF_IFD = 0x8769
TAG_MAKE, TAG_MODEL = 0x010F, 0x0110
TAG_FOCAL, TAG_FOCAL35, TAG_LENS_MODEL = 0x920A, 0xA405, 0xA434


class PhotoError(ValueError):
    pass


def _exif_facts(im):
    """(focal35 or None, lens group key or None) -- the only facts kept from the metadata."""
    try:
        exif = im.getexif()
        sub = exif.get_ifd(EXIF_IFD)
    except Exception:  # noqa: BLE001 - broken EXIF is simply ignored
        return None, None
    f35 = sub.get(TAG_FOCAL35)
    try:
        f35 = float(f35) if f35 else None
    except (TypeError, ValueError):
        f35 = None
    parts = [exif.get(TAG_MAKE), exif.get(TAG_MODEL), sub.get(TAG_LENS_MODEL), sub.get(TAG_FOCAL), f35]
    if not any(parts):
        return f35, None
    key = hashlib.sha256("|".join(str(p) for p in parts).encode()).hexdigest()[:10]
    return f35, key


def clean_jpeg(rgb, quality):
    """Encode pixels only: a fresh Image has an empty .info, so no EXIF/XMP/ICC can ride along."""
    clean = Image.frombytes("RGB", rgb.size, rgb.tobytes())
    buf = io.BytesIO()
    clean.save(buf, "JPEG", quality=quality, optimize=True)
    return buf.getvalue()


def sanitize(raw, max_edge, quality=95):
    """Raw upload bytes -> (metadata-free JPEG bytes, facts dict)."""
    try:
        im = Image.open(io.BytesIO(raw))
        if im.format not in ACCEPTED_FORMATS:
            raise PhotoError(f"unsupported image format {im.format} (send JPEG, PNG, WebP or TIFF)")
        if im.width * im.height > settings.max_image_pixels:
            raise PhotoError(f"image too large ({im.width}x{im.height} > "
                             f"{settings.max_image_pixels // 1_000_000} MP)")
        focal35, lens_key = _exif_facts(im)
        stored = im.size  # the size as uploaded (sparse.zip rescales COLMAP's intrinsics to it)
        if im.format in ("JPEG", "MPO"):
            im.draft("RGB", (max_edge, max_edge))  # fast DCT downscale; never below max_edge
        im = ImageOps.exif_transpose(im)
        rgb = im.convert("RGB")
    except PhotoError:
        raise
    except (Image.DecompressionBombError, Image.DecompressionBombWarning) as e:
        raise PhotoError("image too large (pixel count)") from e
    except Exception as e:  # noqa: BLE001 - anything Pillow cannot decode
        raise PhotoError(f"cannot decode image ({type(e).__name__})") from e
    if (rgb.width >= rgb.height) != (stored[0] >= stored[1]):  # the EXIF orientation turned it
        stored = stored[::-1]
    rgb = downscale(rgb, max_edge)
    return clean_jpeg(rgb, quality), {"width": rgb.width, "height": rgb.height,
                                      "storedWidth": stored[0], "storedHeight": stored[1],
                                      "focal35": focal35, "lensKey": lens_key}


def downscale(im, max_edge):
    if max(im.size) <= max_edge:
        return im
    s = max_edge / max(im.size)
    return im.resize((max(1, round(im.width * s)), max(1, round(im.height * s))), Image.LANCZOS)
