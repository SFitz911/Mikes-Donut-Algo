"""Bar type (copied from fitzbt.data.Bar; no sqlite / data access in the single-machine build)."""
from dataclasses import dataclass


@dataclass(frozen=True)
class Bar:
    ts: int            # epoch seconds UTC, bar END
    open: float
    high: float
    low: float
    close: float
    volume: float = 0.0
