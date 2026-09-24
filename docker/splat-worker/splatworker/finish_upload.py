"""Upload for POST /v1/jobs/splat-finish: JSON fields `prepared` (the prepare job's prepared.json) and
`trainStats`, plus ONE file part `splat` (splat.ply or splat.spz, a runner's trained scene) streamed
straight to the job dir, capped at SPLAT_MAX_RESULT_MB."""
import os

from python_multipart.multipart import MultipartParser, parse_options_header

from computejobs.service import bad

from .settings import settings

MAX_JSON_BYTES = 32 << 20
FILE_NAMES = {"splat.ply", "splat.spz"}
FIELDS = {"prepared", "trainStats"}


class FinishUpload:
    """read(request) -> (fields {name: bytes}, stored file name)."""

    def __init__(self, job_dir):
        self.dir = job_dir
        self.fields, self.file_name, self.fh, self.size = {}, None, None, 0
        self.part, self.headers, self.hname, self.hvalue, self.error = None, {}, b"", b"", None

    def on_part_begin(self):
        self.part, self.headers = {"name": None, "data": bytearray()}, {}

    def on_header_field(self, data, start, end):
        self.hname += data[start:end]

    def on_header_value(self, data, start, end):
        self.hvalue += data[start:end]

    def on_header_end(self):
        self.headers[self.hname.lower()] = self.hvalue
        self.hname, self.hvalue = b"", b""

    def on_headers_finished(self):
        _, opts = parse_options_header(self.headers.get(b"content-disposition", b""))
        name = opts.get(b"name", b"").decode("utf-8", "replace")
        fn = (opts.get(b"filename") or b"").decode("utf-8", "replace")
        self.part["name"] = name
        if name == "splat":
            if self.file_name or fn not in FILE_NAMES:
                self.error = self.error or (400, "expected ONE file part 'splat' named splat.ply or splat.spz")
                return
            self.file_name, self.fh = fn, open(os.path.join(self.dir, fn), "wb")
        elif name not in FIELDS or name in self.fields:
            self.error = self.error or (400, f"unexpected or repeated form part {name!r}")

    def on_part_data(self, data, start, end):
        if self.error:
            return
        if self.part["name"] == "splat" and self.fh:
            self.size += end - start
            if self.size > settings.max_result_bytes:
                self.error = (413, f"the trained scene exceeds {settings.max_result_bytes >> 20} MB")
                return
            self.fh.write(data[start:end])
        else:
            self.part["data"] += data[start:end]
            if len(self.part["data"]) > MAX_JSON_BYTES:
                self.error = (413, f"part {self.part['name']!r} exceeds {MAX_JSON_BYTES >> 20} MB")

    def on_part_end(self):
        if self.part["name"] == "splat" and self.fh:
            self.fh.close()
            self.fh = None
        elif self.part["name"] in FIELDS:
            self.fields[self.part["name"]] = bytes(self.part["data"])

    async def read(self, request):
        ctype = request.headers.get("content-type", "")
        _, params = parse_options_header(ctype)
        if not ctype.startswith("multipart/form-data") or b"boundary" not in params:
            bad("splat-finish expects multipart/form-data: prepared (JSON) + splat (file) [+ trainStats]")
        parser = MultipartParser(params[b"boundary"], {
            "on_part_begin": self.on_part_begin, "on_part_data": self.on_part_data,
            "on_part_end": self.on_part_end, "on_header_field": self.on_header_field,
            "on_header_value": self.on_header_value, "on_header_end": self.on_header_end,
            "on_headers_finished": self.on_headers_finished})
        try:
            async for chunk in request.stream():
                parser.write(chunk)
                if self.error:
                    bad(self.error[1], self.error[0])
            parser.finalize()
        finally:
            if self.fh:
                self.fh.close()
        if self.error:
            bad(self.error[1], self.error[0])
        if not self.file_name or "prepared" not in self.fields:
            bad("splat-finish needs the parts 'prepared' and 'splat'", 422)
        return self.fields, self.file_name
