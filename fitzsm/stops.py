"""Fixed protective stop: distance from the actual entry fill, in ticks (or points converted to ticks).
The runner only puts `stop_loss_ticks` in the envelope; the AddOn computes the price from the confirmed
average fill (same as the TP) and submits stop + target as one OCO pair."""
import math
from .config import TICK_SIZE

MAX_STOP_TICKS = 2000


def stop_ticks(risk):
    """Return the stop distance in whole ticks, or None when stops are disabled. Raises ValueError on bad config."""
    if not risk.stop_enabled:
        return None
    if risk.stop_points is not None:
        pts = float(risk.stop_points)
        if not math.isfinite(pts) or pts <= 0: raise ValueError("risk.stop_points must be > 0")
        t = pts / TICK_SIZE
        if abs(t - round(t)) > 1e-9: raise ValueError("risk.stop_points must be a multiple of 0.25")
        t = int(round(t))
    else:
        if risk.stop_ticks is None: raise ValueError("stop_enabled needs stop_ticks or stop_points")
        if isinstance(risk.stop_ticks, bool) or int(risk.stop_ticks) != risk.stop_ticks: raise ValueError("risk.stop_ticks must be an integer")
        t = int(risk.stop_ticks)
    if not 1 <= t <= MAX_STOP_TICKS: raise ValueError(f"stop distance must be 1..{MAX_STOP_TICKS} ticks")
    return t


def stop_price(side, entry, ticks):
    """Stop price for a fill at `entry`. BUY -> below, SELL -> above. Rounded to the tick grid."""
    d = 1 if side == "BUY" else -1
    return round(round((entry - d * ticks * TICK_SIZE) / TICK_SIZE) * TICK_SIZE, 2)


def stop_hit(side, stop, bar_high, bar_low):
    return bar_low <= stop if side == "BUY" else bar_high >= stop
