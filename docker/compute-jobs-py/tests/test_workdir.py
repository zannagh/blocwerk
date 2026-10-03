"""Start-up sweep of WORK_DIR: job directories of a previous process go, caches and a live neighbour's stay."""
import errno
import os

import pytest

from computejobs import workdir

pytestmark = pytest.mark.skipif(workdir.fcntl is None, reason="needs POSIX flock")

JOB_A, JOB_B = "a" * 32, "0123456789abcdef" * 2


def _job(root, name, size=1000):
    d = root / name
    d.mkdir()
    (d / "photo.jpg").write_bytes(b"x" * size)
    return d


def test_alone_sweeps_job_dirs_and_keeps_everything_else(tmp_path):
    _job(tmp_path, JOB_A)
    _job(tmp_path, JOB_B)
    (tmp_path / "_brush-cache").mkdir()
    (tmp_path / "notes.txt").write_text("keep")
    (tmp_path / ("g" * 32)).mkdir()  # not a uuid4().hex name
    handle = workdir.claim(str(tmp_path))
    try:
        left = sorted(os.listdir(tmp_path))
        assert left == sorted([workdir.LOCK_FILE, "_brush-cache", "notes.txt", "g" * 32])
    finally:
        handle.close()


def test_a_live_process_on_the_same_dir_blocks_the_sweep(tmp_path):
    first = workdir.claim(str(tmp_path))
    try:
        live = _job(tmp_path, JOB_A)  # a job of the first process
        second = workdir.claim(str(tmp_path))
        try:
            assert live.exists()
        finally:
            second.close()
    finally:
        first.close()


def test_after_the_owner_is_gone_the_next_start_sweeps(tmp_path):
    first = workdir.claim(str(tmp_path))
    _job(tmp_path, JOB_A)
    first.close()  # the kernel drops the lock however the process ended
    again = workdir.claim(str(tmp_path))
    try:
        assert not (tmp_path / JOB_A).exists()
    finally:
        again.close()


def test_sweep_is_idempotent_and_counts_bytes(tmp_path):
    _job(tmp_path, JOB_A, size=2048)
    assert workdir.sweep_orphans(str(tmp_path)) == (1, 2048)
    assert workdir.sweep_orphans(str(tmp_path)) == (0, 0)
    assert workdir.sweep_orphans(str(tmp_path / "missing")) == (0, 0)


def test_creates_a_missing_work_dir(tmp_path):
    target = tmp_path / "new" / "jobs"
    handle = workdir.claim(str(target))
    try:
        assert target.is_dir()
    finally:
        handle.close()


def test_a_file_system_without_locks_skips_the_sweep_and_starts(tmp_path, monkeypatch):
    _job(tmp_path, JOB_A)

    def refuse(*_args):
        raise OSError(errno.ENOLCK, "No locks available")

    monkeypatch.setattr(workdir.fcntl, "flock", refuse)
    assert workdir.claim(str(tmp_path)) is None
    assert (tmp_path / JOB_A).exists()


def test_a_network_work_dir_is_never_swept(tmp_path, monkeypatch):
    _job(tmp_path, JOB_A)
    monkeypatch.setattr(workdir, "mount_type", lambda _path: "nfs4")
    assert workdir.claim(str(tmp_path)) is None
    assert (tmp_path / JOB_A).exists()


def test_mount_type_takes_the_longest_mount_point(tmp_path):
    mounts = tmp_path / "mounts"
    work = tmp_path / "share" / "jobs"
    work.mkdir(parents=True)
    share = os.path.realpath(tmp_path / "share")
    mounts.write_text(f"/dev/sda1 / ext4 rw 0 0\nserver:/x {share} nfs4 rw 0 0\n")
    assert workdir.mount_type(str(work), str(mounts)) == "nfs4"
    assert workdir.mount_type("/", str(mounts)) == "ext4"
    assert workdir.mount_type("/", str(tmp_path / "missing")) is None
