"""Adaptive trend signal + dynamic TP.

Logic reimplemented (not imported) from ~/grokbot-trend-strategy src/research/adaptive_trend
(trend read, vol filter, triggers.py momentum / pullback_resume) and the v003 dynamic TP:
  target_pts = stop_atr_mult * target_rr * ATR * clamp(|move_atr| / trend_move_atr, 1, 2),
  ceil to ticks, clamp [tp_min_ticks, tp_max_ticks] (8-160).
Indicator math reused from this repo's fitzbt.indicators (itself mirrored from adaptive_trend)."""
import math
from collections import deque
from smbt.indicators import IndicatorState, linreg, efficiency_ratio, choppiness_index
from .config import TICK_SIZE


class Indicators(IndicatorState):
    def __init__(self, p):
        super().__init__(p)
        self.ema_fast_hist = deque(maxlen=max(64, p.trigger_lookback + 2))

    def update(self, b):
        super().update(b)
        self.ema_fast_hist.append(self.ema_fast.value)

    @property
    def warm(self):
        n = getattr(self.cfg, "warmup_bars", 0)
        if not n:
            return IndicatorState.warm.fget(self)
        return self.count >= n and self.atr.value is not None


def read_trend(ind, p):
    closes = [b.close for b in ind.window(p.trend_lookback)]
    slope, r2 = linreg(closes)
    atr = ind.atr.value or 0.0
    move = slope * (len(closes) - 1) / atr if atr > 0 else 0.0
    ef, es, c = ind.ema_fast.value, ind.ema_slow.value, closes[-1]
    if abs(move) < p.trend_move_atr or r2 < p.r2_min:
        return "RANGE", move
    if move > 0 and ef > es and c > es:
        return "UP", move
    if move < 0 and ef < es and c < es:
        return "DOWN", move
    return "RANGE", move


def vol_ok(ind, p):
    trs = list(ind.trs)[-p.chop_period:]
    wb = ind.window(p.chop_period)
    chop = choppiness_index(trs, [b.high for b in wb], [b.low for b in wb])
    er = efficiency_ratio([b.close for b in ind.window(p.er_period + 1)])
    rb = ind.window(p.range_lookback)
    rng = max(b.high for b in rb) - min(b.low for b in rb)
    atr_range = ind.atr.value / rng if rng > 0 else 1.0
    spike = ind.atr.value / ind.atr_long.value if ind.atr_long.value else 1.0
    return chop <= p.chop_max and er >= p.er_min and atr_range <= p.atr_range_max and spike <= p.atr_spike_max


def trig_momentum(ind, d, p):
    s = 1 if d == "UP" else -1
    b = ind.bars[-1]
    return s * (b.close - b.open) > 0 and s * (b.close - ind.ema_fast.value) > 0


def trig_pullback_resume(ind, d, p):
    s = 1 if d == "UP" else -1
    n = p.trigger_lookback
    bars = ind.window(n + 1)
    emas = list(ind.ema_fast_hist)[-(n + 1):]
    touched = any(e is not None and ((s > 0 and b.low <= e) or (s < 0 and b.high >= e)) for b, e in zip(bars[:-1], emas[:-1]))
    last, prev = bars[-1], bars[-2]
    return touched and ((last.close > prev.high) if s > 0 else (last.close < prev.low))


TRIGGERS = {"momentum": trig_momentum, "pullback_resume": trig_pullback_resume}


def dynamic_tp_ticks(atr, move_atr, p):
    scale = min(max(abs(move_atr) / p.trend_move_atr, 1.0), 2.0)
    pts = p.stop_atr_mult * p.target_rr * atr * scale
    return int(min(max(math.ceil(pts / TICK_SIZE - 1e-9), p.tp_min_ticks), p.tp_max_ticks))


def evaluate(ind, p):
    """-> dict decision (always), with 'signal' = (side, tp_ticks) or None."""
    if not ind.warm:
        return {"signal": None, "why": "warmup"}
    d, move = read_trend(ind, p)
    if d == "RANGE":
        return {"signal": None, "why": "range", "move_atr": round(move, 3)}
    if p.use_vol_filter and not vol_ok(ind, p):
        return {"signal": None, "why": "vol_filter", "trend": d}
    if not TRIGGERS[p.trigger](ind, d, p):
        return {"signal": None, "why": "no_trigger", "trend": d}
    tp = dynamic_tp_ticks(ind.atr.value, move, p)
    return {"signal": ("BUY" if d == "UP" else "SELL", tp), "why": "entry", "trend": d,
            "move_atr": round(move, 3), "atr": round(ind.atr.value, 3), "tp_ticks": tp}
