import json, subprocess, sys
from pathlib import Path
from datetime import datetime, timezone
import pytest
from fitzsm.feed_csv import CsvBarSource, parse_row, HEADER

def row(ts, o=6000, h=6001, l=5999, c=6000.5, v=10, inst="ES 12-26"):
    return f"{ts},{datetime.fromtimestamp(ts, timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')},{o},{h},{l},{c},{v},{inst}\n"

T = 1_792_000_000 // 60 * 60

def test_reads_complete_rows_only_and_keeps_gaps(tmp_path):
    p = tmp_path / "b.csv"
    p.write_text(",".join(HEADER) + "\n" + row(T) + row(T + 120) + row(T + 180)[:20])
    src = CsvBarSource(p)
    bars = src.step()
    assert [b.ts for b in bars] == [T, T + 120]            # gap at T+60 stays a gap; partial line not read
    with p.open("a") as f: f.write(row(T + 180)[20:])
    assert [b.ts for b in src.step()] == [T + 180]

def test_duplicates_and_old_rows_skipped(tmp_path):
    p = tmp_path / "b.csv"; p.write_text(row(T) + row(T) + row(T - 60) + row(T + 60))
    assert [b.ts for b in CsvBarSource(p).step()] == [T, T + 60]

def test_mes_rows_refused(tmp_path):
    p = tmp_path / "b.csv"; p.write_text(row(T, inst="MES 12-26") + row(T + 60))
    src = CsvBarSource(p); assert [b.ts for b in src.step()] == [T + 60] and src.health.bad_rows == 1

@pytest.mark.parametrize("bad", [row(T + 1), row(T, h=5990), row(T).replace("Z,", "+01:00,", 1)])
def test_bad_rows(bad):
    with pytest.raises(ValueError): parse_row(bad.strip().split(","))

def test_min_ts_and_stale(tmp_path):
    p = tmp_path / "b.csv"; p.write_text(row(T) + row(T + 60))
    src = CsvBarSource(p, min_ts=T + 60, stale_after_s=180)
    assert [b.ts for b in src.step()] == [T + 60]
    assert src.health.stale(T + 100) == (False, "") and src.health.stale(T + 400)[0]

def test_missing_file_is_not_an_error(tmp_path):
    src = CsvBarSource(tmp_path / "nope.csv"); assert src.step() == [] and src.health.stale(T)[0]

def test_cli_dry_run_once_smoke(tmp_path):
    root = Path(__file__).resolve().parents[1]
    p = tmp_path / "b.csv"; p.write_text(",".join(HEADER) + "\n" + "".join(row(T + 60 * i) for i in range(200)))
    cfg = json.loads((root / "config" / "fitzsm.example.json").read_text())
    cfg["feed"]["path"] = str(p); cfg["execution"].update(mode="DRY_RUN", live_enabled=False, queue_dir=str(tmp_path / "q"))
    (tmp_path / "c.json").write_text(json.dumps(cfg))
    r = subprocess.run([sys.executable, "-m", "fitzsm", "--config", str(tmp_path / "c.json"), "run", "--once", "--out", str(tmp_path / "out"),
                        "--warmup-from", "2026-10-01T00:00", "--live-from", "2026-10-01T00:00"], cwd=root, capture_output=True, text=True)
    assert r.returncode == 0, r.stderr
    s = json.loads((tmp_path / "out" / "summary.json").read_text())
    assert s["feed"]["bars"] == 200 and not s["live"]
    assert not (tmp_path / "q").exists()                    # DRY_RUN never touches the AddOn queue
