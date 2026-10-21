"""Local ES 1-minute bar feed: tails the CSV written by the FitzLabsBarWriter NT8 indicator (same PC).

CSV contract (header row + one row per CLOSED 1-minute bar, appended in time order):
    bar_end_utc_epoch,bar_end_utc,open,high,low,close,volume,instrument
    1792530000,2026-10-20T19:00:00Z,6712.25,6713.00,6711.75,6712.50,1543,ES 12-26
- bar_end_utc_epoch: bar END in UTC epoch seconds (same convention as es_1m.sqlite / NT8 bar time). The writer
  converts NT8's bar time from NT8's configured time zone to UTC before writing.
- Only rows that end in a newline are read (a half-written last line is left until it is complete).
- Gaps are NOT filled: a minute with no bar simply has no row. Duplicate / older timestamps are skipped.
- Rows from a non-ES instrument are refused (never substitute MES for ES).
"""
import csv, io, os, time
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import List, Optional

HEADER = ["bar_end_utc_epoch", "bar_end_utc", "open", "high", "low", "close", "volume", "instrument"]


@dataclass(frozen=True)
class CsvBar:
    ts: int            # bar END epoch UTC
    open: float
    high: float
    low: float
    close: float
    volume: float
    contract: str


def root_of(instrument: str) -> str:
    return (instrument or "").strip().split(" ")[0].upper()


def parse_row(row, instrument_root="ES") -> Optional[CsvBar]:
    """Return a bar or raise ValueError with the reason. Header rows return None."""
    if not row or row[0] == HEADER[0]: return None
    if len(row) != len(HEADER): raise ValueError(f"expected {len(HEADER)} columns, got {len(row)}")
    ts = int(row[0])
    if ts % 60 != 0: raise ValueError("bar_end_utc_epoch is not on a minute boundary")
    iso = datetime.fromtimestamp(ts, timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    if row[1].strip() != iso: raise ValueError(f"bar_end_utc {row[1]!r} != epoch {iso}")
    o, h, l, c, v = (float(x) for x in row[2:7])
    if not (l <= min(o, c) and h >= max(o, c) and l > 0): raise ValueError("OHLC inconsistent")
    if v < 0: raise ValueError("negative volume")
    if root_of(row[7]) != instrument_root.upper(): raise ValueError(f"instrument {row[7]!r} is not {instrument_root}")
    return CsvBar(ts, o, h, l, c, v, row[7].strip())


class FeedHealth:
    def __init__(self, stale_after_s=180.0, clock=time.time):
        self.stale_after_s, self.clock = stale_after_s, clock
        self.last_bar_ts = None; self.last_rx = None; self.bars = 0; self.bad_rows = 0; self.last_error = ""
        self.contract = None; self.contract_changes = 0

    def stale(self, now=None):
        now = self.clock() if now is None else now
        if self.last_bar_ts is None: return True, "no ES bar received yet from the NT8 CSV"
        age = now - self.last_bar_ts
        if age > self.stale_after_s: return True, f"newest ES bar ended {age:.0f}s ago (> {self.stale_after_s:.0f}s)"
        return False, ""

    def as_dict(self):
        return dict(last_bar_ts=self.last_bar_ts, bars=self.bars, bad_rows=self.bad_rows, last_error=self.last_error,
                    contract=self.contract, contract_changes=self.contract_changes, stale_after_s=self.stale_after_s)


def update_stale_breaker(registry, health, now=None, scope="LAB"):
    bad, why = health.stale(now)
    return registry.update("stale_data", bad, why, scope=scope)


class CsvBarSource:
    """Poll-driven reader. step() returns new complete bars (strictly increasing ts). Survives the file being
    absent (returns nothing) and the file being truncated/replaced (re-reads from the start, still skipping
    timestamps already delivered)."""

    def __init__(self, path, instrument_root="ES", stale_after_s=180.0, min_ts=None, clock=time.time, logger=None):
        self.path, self.root, self.log = Path(path), instrument_root, logger
        self.offset = 0; self.last_ts = None if min_ts is None else int(min_ts) - 1
        self._size_seen = 0
        self.health = FeedHealth(stale_after_s, clock)

    def step(self) -> List[CsvBar]:
        try:
            size = os.path.getsize(self.path)
        except OSError:
            return []
        if size < self.offset:              # truncated / replaced: start over, ts filter prevents re-delivery
            self.offset = 0
        if size == self.offset: return []
        with open(self.path, "rb") as f:
            f.seek(self.offset); chunk = f.read(size - self.offset)
        end = chunk.rfind(b"\n")
        if end < 0: return []               # no complete line yet
        complete = chunk[: end + 1]; self.offset += len(complete)
        out = []
        for row in csv.reader(io.StringIO(complete.decode("utf-8-sig", errors="replace"))):
            try:
                b = parse_row(row, self.root)
            except (ValueError, IndexError) as e:
                self.health.bad_rows += 1; self.health.last_error = f"{e}: {row[:2]}"
                if self.log: self.log.event("feed_bad_row", error=str(e), row=row[:8])
                continue
            if b is None: continue
            if self.last_ts is not None and b.ts <= self.last_ts: continue
            if self.health.contract and b.contract != self.health.contract:
                self.health.contract_changes += 1
                if self.log: self.log.event("feed_contract_change", old=self.health.contract, new=b.contract, ts=b.ts)
            self.health.contract = b.contract
            self.last_ts = b.ts; out.append(b)
        if out:
            self.health.bars += len(out); self.health.last_bar_ts = out[-1].ts; self.health.last_rx = self.health.clock()
        return out
