"""The child process of a textures job: `python -m splatworker.gpurunner.textures_child JOB_DIR BLEND_MAX_BYTES
MAX_IMAGE_PIXELS` renders the unpacked bundle in JOB_DIR with wallgeometry.jobrender.render_job (the wall-geometry
service's own renderer). A child, so the runner can kill it on a pause, a cancel or a memory overrun, and so the
photos and the blend's arrays are returned to the OS when it ends.

Progress goes to stdout as `PROGRESS <fraction> <stage>` lines; the outcome to JOB_DIR/child-result.json
({"ok": true, "files": [...]} or {"ok": false, "input": <the job's input is at fault>, "error": "..."}), exit code 0
for both (a crash or a kill has no result file)."""
import json
import os
import sys

RESULT = "child-result.json"


def write_result(job_dir, doc):
    with open(os.path.join(job_dir, RESULT), "w") as fh:
        json.dump(doc, fh)


def main(argv):
    job_dir, blend_max_bytes, max_pixels = argv[1], int(argv[2]), int(argv[3])

    def progress(fraction, stage):
        print(f"PROGRESS {min(1.0, max(0.0, float(fraction))):.4f} {stage}", flush=True)

    from wallgeometry.jobrender import render_job
    from wallgeometry.textures import TextureError
    try:
        manifest = render_job(job_dir, progress, blend_max_bytes, max_pixels)
    except TextureError as e:  # the inputs are inconsistent (a photo's size differs from its solved camera ...)
        write_result(job_dir, {"ok": False, "input": True, "error": f"invalid input: {e}"})
        return 0
    write_result(job_dir, {"ok": True, "files": manifest["files"],
                           "facets": len(manifest["facets"])})
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
