"""Upload for POST /v1/jobs/splat: the shared streaming multipart reader (computejobs.upload) with a
photo hook that sanitizes each photo (ingest.sanitize: metadata-free re-encode) before it is written;
the original bytes never reach the disk."""
import os

from computejobs.upload import PhotoRejected, StreamingUpload, json_field, text_field  # noqa: F401

from .ingest import PhotoError, sanitize
from .settings import settings

PHOTO_EXT = {".jpg", ".jpeg", ".png", ".webp", ".tif", ".tiff"}
FIELDS = {"geometry", "options", "callbackUrl"}


class SplatUpload(StreamingUpload):
    """Parses the body into job_dir/arrived/<stem>.jpg (clean) + fields; returns (fields, photos)."""

    def __init__(self, job_dir):
        self.dir = os.path.join(job_dir, "arrived")
        os.makedirs(self.dir, exist_ok=True)
        super().__init__(fields=FIELDS, photo_ext=PHOTO_EXT, process_photo=self._store, kind="splat")

    def _store(self, stem, _ext, raw):
        try:
            jpg, facts = sanitize(raw, settings.ingest_max_edge, settings.ingest_jpeg_quality)
        except PhotoError as e:
            raise PhotoRejected(422, str(e)) from e
        with open(os.path.join(self.dir, f"{stem}.jpg"), "wb") as fh:
            fh.write(jpg)
        return facts
