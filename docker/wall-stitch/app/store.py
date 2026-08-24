"""Disk-backed job records: one directory per job under the data dir.

State lives in `state.json` next to the artifacts, written atomically, so a restart
can tell a finished job from one that was interrupted mid-flight. Anything found in
`queued`/`running` at startup is marked failed rather than left to hang forever.
"""
from __future__ import annotations

import json
import os
import shutil
import threading
import time
import uuid
from typing import Any, Dict, List, Optional

STATE_FILE = "state.json"
ARTIFACT_DIR = "artifacts"
UPLOAD_DIR = "input"
WORK_DIR = "work"


def new_job_id() -> str:
    return uuid.uuid4().hex


def is_valid_job_id(job_id: str) -> bool:
    return bool(job_id) and len(job_id) <= 64 and all(c.isalnum() or c in "-_" for c in job_id)


class JobStore:
    """Thread-safe accessor for job directories. All mutation goes through `update`."""

    def __init__(self, data_dir: str):
        self.data_dir = data_dir
        self.lock = threading.RLock()
        os.makedirs(self.data_dir, exist_ok=True)

    def job_dir(self, job_id: str) -> str:
        return os.path.join(self.data_dir, job_id)

    def artifact_dir(self, job_id: str) -> str:
        return os.path.join(self.job_dir(job_id), ARTIFACT_DIR)

    def upload_dir(self, job_id: str) -> str:
        return os.path.join(self.job_dir(job_id), UPLOAD_DIR)

    def work_dir(self, job_id: str) -> str:
        return os.path.join(self.job_dir(job_id), WORK_DIR)

    def create(self, job_id: str, options: Dict[str, Any]) -> Dict[str, Any]:
        with self.lock:
            for path in (self.artifact_dir(job_id), self.upload_dir(job_id), self.work_dir(job_id)):
                os.makedirs(path, exist_ok=True)
            record = {
                "jobId": job_id,
                "status": "queued",
                "progress": 0.0,
                "stage": "queued",
                "error": None,
                "result": None,
                "options": options,
                "createdAt": time.time(),
                "updatedAt": time.time(),
            }
            self._write(job_id, record)
            return record

    def read(self, job_id: str) -> Optional[Dict[str, Any]]:
        if not is_valid_job_id(job_id):
            return None
        path = os.path.join(self.job_dir(job_id), STATE_FILE)
        with self.lock:
            try:
                with open(path, "r", encoding="utf-8") as handle:
                    return json.load(handle)
            except (FileNotFoundError, NotADirectoryError, json.JSONDecodeError):
                return None

    def update(self, job_id: str, **fields: Any) -> Optional[Dict[str, Any]]:
        with self.lock:
            record = self.read(job_id)
            if record is None:
                return None
            record.update(fields)
            record["updatedAt"] = time.time()
            self._write(job_id, record)
            return record

    def delete(self, job_id: str) -> bool:
        if not is_valid_job_id(job_id):
            return False
        with self.lock:
            path = self.job_dir(job_id)
            if not os.path.isdir(path):
                return False
            shutil.rmtree(path, ignore_errors=True)
            return True

    def list_ids(self) -> List[str]:
        with self.lock:
            try:
                return [n for n in os.listdir(self.data_dir)
                        if is_valid_job_id(n) and os.path.isdir(self.job_dir(n))]
            except FileNotFoundError:
                return []

    def fail_orphans(self, code: str, message: str) -> int:
        """Marks jobs left mid-flight by a restart as failed. Returns how many."""
        count = 0
        for job_id in self.list_ids():
            record = self.read(job_id)
            if record and record.get("status") in ("queued", "running"):
                self.update(job_id, status="failed", stage=None,
                            error={"code": code, "message": message})
                count += 1
        return count

    def reap(self, ttl_seconds: int, now: Optional[float] = None) -> List[str]:
        """Deletes job directories older than the TTL. Returns the ids removed."""
        cutoff = (now if now is not None else time.time()) - ttl_seconds
        removed = []
        for job_id in self.list_ids():
            record = self.read(job_id)
            created = record.get("createdAt") if record else None
            if created is None:
                try:
                    created = os.path.getmtime(self.job_dir(job_id))
                except OSError:
                    continue
            if created < cutoff and self.delete(job_id):
                removed.append(job_id)
        return removed

    def _write(self, job_id: str, record: Dict[str, Any]) -> None:
        path = os.path.join(self.job_dir(job_id), STATE_FILE)
        tmp = path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as handle:
            json.dump(record, handle)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(tmp, path)
