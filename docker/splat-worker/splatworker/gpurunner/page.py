"""The status page's HTML (web.py): plain server-rendered HTML with inline CSS; forms for the pause switch, so it
works without JavaScript; a few lines of JavaScript swap in /fragment every 2 s. Everything shown is escaped."""
import time
from html import escape

STYLE = """
:root{--bg:#f6f7f9;--card:#fff;--ink:#1d2330;--mute:#677085;--line:#e2e5eb;--ok:#1f7a4d;--warn:#a05a00;--bad:#b42318;
--acc:#2f5bd3}
@media (prefers-color-scheme:dark){:root{--bg:#14171c;--card:#1d2128;--ink:#e6e8ec;--mute:#9aa3b2;--line:#2c313a;
--ok:#4cc38a;--warn:#f0a43a;--bad:#f2706a;--acc:#7c9cff}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.45 system-ui,sans-serif}
main{max-width:1100px;margin:0 auto;padding:16px}h1{font-size:20px;margin:4px 0 2px}h2{font-size:16px;margin:0 0 10px}
.sub{color:var(--mute);font-size:13px}.card{background:var(--card);border:1px solid var(--line);border-radius:10px;
padding:14px 16px;margin:12px 0}.row{display:flex;flex-wrap:wrap;gap:8px;align-items:center}
.badge{display:inline-block;padding:2px 10px;border-radius:99px;font-weight:600;font-size:13px;border:1px solid}
.running{color:var(--ok)}.paused{color:var(--warn)}.bad{color:var(--bad)}
button{font:inherit;padding:6px 14px;border-radius:8px;border:1px solid var(--line);background:var(--card);
color:var(--ink);cursor:pointer}button.primary{background:var(--acc);border-color:var(--acc);color:#fff}
.bar{height:10px;background:var(--line);border-radius:99px;overflow:hidden;margin:8px 0}
.bar>i{display:block;height:100%;background:var(--acc)}dl{display:grid;grid-template-columns:max-content 1fr;
gap:2px 14px;margin:0}dt{color:var(--mute)}dd{margin:0}.scroll{overflow-x:auto}
table{border-collapse:collapse;width:100%;font-size:13px}th,td{text-align:left;padding:5px 8px;
border-bottom:1px solid var(--line);white-space:nowrap}th{color:var(--mute);font-weight:500}td.err{white-space:normal;
min-width:200px}code{font-size:12px}
"""
SCRIPT = """
const stale=(on)=>{document.getElementById('stale').hidden=!on;};
setInterval(async()=>{try{const r=await fetch('fragment',{cache:'no-store'});if(!r.ok)throw r.status;
document.getElementById('live').innerHTML=await r.text();stale(false);}catch(e){stale(true);}},2000);
"""
OUTCOME_CLASS = {"succeeded": "running", "paused": "paused", "shutdown": "paused", "failed": "bad", "error": "bad",
                 "abandoned": "bad"}


def when(ts):
    return time.strftime("%Y-%m-%d %H:%M:%S", time.localtime(ts)) if ts else "–"


def dur(seconds):
    if seconds is None:
        return "–"
    s = int(round(seconds))
    if s >= 3600:
        return f"{s // 3600} h {s % 3600 // 60:02d} min"
    return f"{s // 60} min {s % 60:02d} s" if s >= 60 else f"{s} s"


def short(value, n=8):
    return escape(str(value)[:n]) if value else "–"


def state_badge(snap):
    mode, busy = snap["pause"]["mode"], snap["busy"]
    if mode == "paused":
        since = snap["pause"].get("since")
        return f'<span class="badge paused">Paused</span> <span class="sub">since {when(since)}</span>'
    if mode == "after-job":
        return '<span class="badge paused">Pausing after the current job</span>'
    return f'<span class="badge running">{"Training" if busy else "Waiting for jobs"}</span>'


def form(action, label, mode=None, primary=False):
    field = f'<input type="hidden" name="mode" value="{mode}">' if mode else ""
    cls = ' class="primary"' if primary else ""
    return f'<form method="post" action="{action}">{field}<button{cls}>{escape(label)}</button></form>'


def controls(snap):
    mode, busy = snap["pause"]["mode"], snap["busy"]
    if mode == "paused":
        return form("resume", "Resume", primary=True)
    buttons = [form("pause", "Pause now" if busy else "Pause", "now")]
    if mode == "running" and busy:
        buttons.append(form("pause", "Finish this job, then pause", "after-job"))
    if mode == "after-job":
        buttons.insert(0, form("resume", "Keep taking jobs", primary=True))
    hint = ('<p class="sub">"Pause now" stops the training and hands the job back at no cost; it resumes from its '
            'last checkpoint later.</p>' if busy else "")
    return f'<div class="row">{"".join(buttons)}</div>{hint}'


def current_job(cur):
    if not cur:
        return '<div class="card"><h2>Current job</h2><p class="sub">None.</p></div>'
    p = cur.get("progress") or {}
    frac = float(p.get("fraction") or 0.0)
    steps = f'{p["step"]} / {p["totalSteps"]}' if p.get("step") is not None and p.get("totalSteps") else "–"
    stages = ", ".join(f"{escape(k)} {dur(v)}" for k, v in (cur.get("stages") or {}).items()) or "–"
    eta = dur(cur.get("etaS")) if cur.get("etaS") is not None else "–"
    return f"""<div class="card"><h2>Current job <code>{short(cur.get("jobId"))}</code></h2>
<div class="bar"><i style="width:{max(0.0, min(frac, 1.0)) * 100:.1f}%"></i></div>
<dl><dt>Stage</dt><dd>{escape(str(cur.get("stage") or "–"))} · {frac * 100:.1f} %</dd>
<dt>Step</dt><dd>{steps}</dd><dt>ETA</dt><dd>{eta}</dd><dt>Detail</dt><dd>{escape(str(p.get("detail") or "–"))}</dd>
<dt>Quality</dt><dd>{escape(str(cur.get("quality") or "–"))}</dd>
<dt>Wall / capture</dt><dd><code>{short(cur.get("wallId"))}</code> / <code>{short(cur.get("captureId"))}</code></dd>
<dt>Started</dt><dd>{when(cur.get("startedAt"))} ({dur(cur.get("elapsedS"))} ago)</dd>
<dt>Stages</dt><dd>{stages}</dd></dl></div>"""


def job_row(j):
    st = j.get("stages") or {}
    outcome = str(j.get("outcome") or "–")
    cls = OUTCOME_CLASS.get(outcome, "")
    previews = j.get("previewsUploaded")
    return (f'<tr><td>{when(j.get("startedAt"))}</td><td>{escape(str(j.get("server") or "–"))}</td>'
            f'<td><code>{short(j.get("wallId"))}</code></td><td><code>{short(j.get("captureId"))}</code></td>'
            f'<td>{escape(str(j.get("quality") or "–"))}</td><td class="{cls}">{escape(outcome)}</td>'
            f'<td>{dur(j.get("durationS"))}</td><td>{dur(st.get("download"))}</td><td>{dur(st.get("train"))}</td>'
            f'<td>{dur(st.get("upload"))}</td><td>{"–" if previews is None else int(previews)}</td>'
            f'<td class="err">{escape(str(j.get("error") or ""))}</td></tr>')


def history(jobs):
    if not jobs:
        return '<div class="card"><h2>Jobs</h2><p class="sub">No jobs yet.</p></div>'
    head = "".join(f"<th>{h}</th>" for h in ("Started", "Server", "Wall", "Capture", "Quality", "Outcome",
                                               "Total", "Download", "Train", "Upload", "Previews", "Error"))
    rows = "".join(job_row(j) for j in jobs)
    return (f'<div class="card"><h2>Jobs (newest first)</h2><div class="scroll"><table><thead><tr>{head}</tr>'
            f'</thead><tbody>{rows}</tbody></table></div></div>')


def render_main(snap):
    caps = snap.get("caps") or {}
    vram = f'{caps["vramMb"] / 1024:.1f} GB' if caps.get("vramMb") else "–"
    error = (f'<p class="bad">Server not reachable: {escape(str(snap["lastError"]))}</p>'
             if snap.get("lastError") else "")
    return f"""<div class="card"><div class="row">{state_badge(snap)}</div>
<p class="sub">Runner {escape(str(snap.get("runnerName") or "(not connected yet)"))}
· {escape(str(snap.get("server")))}
· {escape(str(caps.get("gpuName") or "GPU unknown"))}, {vram} · {escape(str(caps.get("trainer") or ""))}
· up to {escape(str(caps.get("maxQuality") or "–"))} · running since {when(snap.get("startedAt"))}</p>
{error}{controls(snap)}</div>{current_job(snap.get("current"))}{history(snap.get("jobs"))}"""


def render(snap):
    return f"""<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1"><title>Blocwerk 3D runner</title>
<style>{STYLE}</style></head><body><main><h1>Blocwerk 3D runner</h1>
<p class="sub" id="stale" hidden>The runner is not answering; showing the last state.</p>
<div id="live">{render_main(snap)}</div></main><script>{SCRIPT}</script></body></html>"""
