"""Streaming multipart reader for photo uploads (shared by the splat and textures kinds).

Unlike Starlette's form parser (which spools every upload > 1 MB to a temp file first), each photo is
held in memory only until its part ends, then handed to `process_photo(stem, ext, raw)` (run in a
thread) which cleans it (metadata-free) and writes it; the original bytes never reach the disk.
Memory is bounded by one photo (MAX_PHOTO_MB) plus the small JSON fields.
"""
import os

from python_multipart.multipart import MultipartParser, parse_options_header
from starlette.concurrency import run_in_threadpool

from .service import SAFE_NAME, bad, loads_strict
from .settings import settings

MAX_FIELD_BYTES = 8 << 20


class PhotoRejected(ValueError):
    """process_photo refuses a photo: (status code, message)."""

    def __init__(self, code, message):
        super().__init__(message)
        self.code = code


class _Part:
    def __init__(self):
        self.headers, self.name, self.filename, self.data = {}, None, None, bytearray()


class StreamingUpload:
    """read(request) -> (fields {name: bytes}, photos {stem: process_photo result})."""

    def __init__(self, *, fields, photo_ext, process_photo, photo_field="photos", kind="upload"):
        self.allowed_fields, self.photo_ext, self.process_photo = set(fields), set(photo_ext), process_photo
        self.photo_field, self.kind = photo_field, kind
        self.fields, self.photos = {}, {}
        self.part, self.hname, self.hvalue = None, b"", b""
        self.finished, self.error = [], None

    # ----- parser callbacks (sync) -----
    def on_part_begin(self):
        self.part = _Part()

    def on_header_field(self, data, start, end):
        self.hname += data[start:end]

    def on_header_value(self, data, start, end):
        self.hvalue += data[start:end]

    def on_header_end(self):
        self.part.headers[self.hname.lower()] = self.hvalue
        self.hname, self.hvalue = b"", b""

    def on_headers_finished(self):
        _, opts = parse_options_header(self.part.headers.get(b"content-disposition", b""))
        self.part.name = opts.get(b"name", b"").decode("utf-8", "replace")
        fn = opts.get(b"filename")
        self.part.filename = None if fn is None else fn.decode("utf-8", "replace")
        if self.part.name == self.photo_field:
            self._check_photo_header(self.part)
        elif self.part.name not in self.allowed_fields:
            expected = ", ".join([self.photo_field, *sorted(self.allowed_fields)])
            self.error = self.error or (400, f"unexpected form part {self.part.name!r}; expected {expected}")
        elif self.part.name in self.fields or any(q.name == self.part.name for q in self.finished):
            self.error = self.error or (400, f"form part {self.part.name!r} given twice")

    def on_part_data(self, data, start, end):
        p = self.part
        p.data += data[start:end]
        limit = settings.max_photo_bytes if p.name == self.photo_field else MAX_FIELD_BYTES
        if len(p.data) > limit and not self.error:
            self.error = (413, f"part {p.filename or p.name!r} exceeds {limit >> 20} MB")

    def on_part_end(self):
        self.finished.append(self.part)

    # ----- validation / processing -----
    def _check_photo_header(self, p):
        stem, ext = os.path.splitext(os.path.basename(p.filename or ""))
        if not p.filename or ext.lower() not in self.photo_ext or not SAFE_NAME.match(stem):
            exts = "|".join(sorted(e.lstrip(".") for e in self.photo_ext))
            self.error = self.error or (400, f"photo file name {p.filename!r}: expected <name>.<{exts}>")
        elif stem in self.photos or any(q.filename and os.path.splitext(q.filename)[0] == stem
                                        for q in self.finished):
            self.error = self.error or (400, f"photo {stem} uploaded twice")
        elif len(self.photos) + len(self.finished) >= settings.max_photos:
            self.error = self.error or (413, f"too many photos (max {settings.max_photos})")

    async def _drain(self):
        for p in self.finished:
            if p.name == self.photo_field:
                stem, ext = os.path.splitext(os.path.basename(p.filename))
                raw, p.data = bytes(p.data), bytearray()
                try:
                    self.photos[stem] = await run_in_threadpool(self.process_photo, stem, ext.lower(), raw)
                except PhotoRejected as e:
                    bad(f"photo {stem}: {e}", e.code)
                del raw
            else:
                self.fields[p.name] = bytes(p.data)
        self.finished.clear()

    async def read(self, request):
        ctype = request.headers.get("content-type", "")
        if not ctype.startswith("multipart/form-data"):
            bad(f"{self.kind} expects multipart/form-data: {self.photo_field} (files) "
                f"[+ {', '.join(sorted(self.allowed_fields))}]")
        _, params = parse_options_header(ctype)
        if b"boundary" not in params:
            bad("multipart body without boundary")
        parser = MultipartParser(params[b"boundary"], {
            "on_part_begin": self.on_part_begin, "on_part_data": self.on_part_data,
            "on_part_end": self.on_part_end, "on_header_field": self.on_header_field,
            "on_header_value": self.on_header_value, "on_header_end": self.on_header_end,
            "on_headers_finished": self.on_headers_finished})
        async for chunk in request.stream():
            parser.write(chunk)
            if self.error:
                bad(self.error[1], self.error[0])
            await self._drain()
        parser.finalize()
        if self.error:
            bad(self.error[1], self.error[0])
        await self._drain()
        return self.fields, self.photos


def json_field(fields, key):
    raw = fields.get(key)
    if raw is None:
        return None
    try:
        return loads_strict(raw)
    except (ValueError, UnicodeDecodeError, RecursionError):
        bad(f"'{key}' must be JSON")


def text_field(fields, key):
    raw = fields.get(key)
    return None if raw is None else raw.decode("utf-8", "replace")

