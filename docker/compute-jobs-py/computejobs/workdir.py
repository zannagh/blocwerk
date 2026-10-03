"""WORK_DIR ownership and the start-up sweep of job directories a previous process left behind.

Job state lives in memory (jobs.py), so after a crash, an OOM kill or a restart every job directory under
WORK_DIR belongs to nobody: its job can never be polled, fetched or expired again. A starting process removes
them, but only when it is sure no other live process uses the same WORK_DIR (two native workers pointed at one
folder, say):

- every process holds a lock on WORK_DIR/.computejobs.lock for its whole life (shared);
- a starting process first tries to take it exclusively, without waiting; only if that succeeds is it alone,
  and only then does it sweep (and then keeps the lock shared, like everyone else);
- the kernel drops the lock when a process dies, however it dies, so a crashed process never blocks a sweep.

Only entries named like a job id (32 lowercase hex digits, `uuid4().hex`) are removed: caches such as the
splat worker's `_brush-cache` and anything else in the folder are left alone.

flock only sees processes on the same host. A WORK_DIR on a network file system (NFS, SMB/CIFS, ...) that several
hosts share would let each one sweep the others' live jobs, so on Linux such a mount (from /proc/self/mounts) is never
swept; elsewhere it cannot be detected: keep WORK_DIR local (every compose service has its own, in the container).
When the file system refuses locks altogether (ENOLCK, EOPNOTSUPP, ...), the sweep is skipped too and the service
starts as before.
"""
import errno
import logging
import os
import re
import shutil

try:
    import fcntl
except ImportError:  # not POSIX: no lock, no sweep
    fcntl = None

log = logging.getLogger("computejobs.workdir")
LOCK_FILE = ".computejobs.lock"
JOB_DIR = re.compile(r"^[0-9a-f]{32}$")
NETWORK_FS = ("nfs", "nfs4", "cifs", "smb3", "smbfs", "9p", "ceph", "glusterfs", "lustre", "afs", "fuse.sshfs")
BUSY = (errno.EWOULDBLOCK, errno.EAGAIN, errno.EACCES)


def _size(path):
    total = 0
    for root, _dirs, files in os.walk(path):
        for name in files:
            try:
                total += os.lstat(os.path.join(root, name)).st_size
            except OSError:
                pass
    return total


def sweep_orphans(work_dir):
    """Removes every job-id-named entry of work_dir; returns (count, bytes). The caller must own work_dir."""
    count = freed = 0
    try:
        names = os.listdir(work_dir)
    except OSError:
        return 0, 0
    for name in names:
        if not JOB_DIR.match(name):
            continue
        path = os.path.join(work_dir, name)
        size = _size(path) if os.path.isdir(path) else os.path.getsize(path)
        try:
            if os.path.isdir(path) and not os.path.islink(path):
                shutil.rmtree(path)
            else:
                os.remove(path)
        except OSError as e:
            log.warning("could not remove orphaned job directory %s: %s", path, e)
            continue
        count, freed = count + 1, freed + size
    return count, freed


def mount_type(path, mounts="/proc/self/mounts"):
    """The file-system type of the mount holding path (Linux), or None when it cannot be told."""
    try:
        with open(mounts) as f:
            entries = [line.split() for line in f]
    except OSError:
        return None
    real = os.path.realpath(path)
    best, kind = "", None
    for parts in entries:
        if len(parts) < 3:
            continue
        point = parts[1].replace("\\040", " ")
        inside = real == point or real.startswith(point.rstrip("/") + "/")
        if inside and len(point) >= len(best):
            best, kind = point, parts[2]
    return kind


def _shared_fs(work_dir):
    kind = mount_type(work_dir)
    return kind is not None and (kind in NETWORK_FS or kind.startswith("nfs"))


def claim(work_dir):
    """Creates work_dir, takes its lock and sweeps it when this process is alone. Returns the lock's file
    object (keep it referenced: closing it releases the lock), or None when there is no lock to hold."""
    os.makedirs(work_dir, exist_ok=True)
    if fcntl is None:
        return None
    if _shared_fs(work_dir):
        log.warning("WORK_DIR %s is on a network file system (%s); not sweeping job directories left from before",
                    work_dir, mount_type(work_dir))
        return None
    handle = open(os.path.join(work_dir, LOCK_FILE), "a+")
    try:
        fcntl.flock(handle, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError as e:
        if e.errno not in BUSY:
            log.warning("WORK_DIR %s does not support file locks (%s); not sweeping job directories left from before",
                        work_dir, e)
            handle.close()
            return None
        log.warning("WORK_DIR %s is in use by another process; not sweeping job directories left from before", work_dir)
        _hold_shared(handle, work_dir)
        return handle
    count, freed = sweep_orphans(work_dir)
    if count:
        log.info("removed %d job director%s left by a previous process (%.1f MB)",
                 count, "y" if count == 1 else "ies", freed / (1024 * 1024))
    _hold_shared(handle, work_dir)
    return handle


def _hold_shared(handle, work_dir):
    """Keeps a shared lock for the process's life (so a later start knows it is not alone); never fatal."""
    try:
        fcntl.flock(handle, fcntl.LOCK_SH)
    except OSError as e:
        log.warning("could not keep the lock on WORK_DIR %s (%s); another process starting now may sweep it", work_dir, e)
