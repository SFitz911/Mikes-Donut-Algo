"""Stdlib indicator math, mirrored (not imported) from grokbot-trend-strategy adaptive_trend."""
import math
from collections import deque


def true_range(h, l, pc):
    return h - l if pc is None else max(h - l, abs(h - pc), abs(l - pc))


class WilderATR:
    def __init__(self, n):
        self.n, self.value, self._seed = n, None, []

    def update(self, tr):
        if self.value is None:
            self._seed.append(tr)
            if len(self._seed) == self.n:
                self.value = sum(self._seed) / self.n
        else:
            self.value = (self.value * (self.n - 1) + tr) / self.n
        return self.value


class EMA:
    def __init__(self, n):
        self.alpha, self.value, self.n, self.count = 2.0 / (n + 1), None, n, 0

    def update(self, x):
        self.count += 1
        self.value = x if self.value is None else self.value + self.alpha * (x - self.value)
        return self.value


def linreg(v):
    n = len(v)
    if n < 3:
        return 0.0, 0.0
    mx, my = (n - 1) / 2.0, sum(v) / n
    sxx = sum((i - mx) ** 2 for i in range(n))
    sxy = sum((i - mx) * (x - my) for i, x in enumerate(v))
    syy = sum((x - my) ** 2 for x in v)
    return sxy / sxx, (0.0 if syy == 0 else sxy * sxy / (sxx * syy))


def efficiency_ratio(c):
    if len(c) < 2:
        return 0.0
    path = sum(abs(c[i] - c[i - 1]) for i in range(1, len(c)))
    return 0.0 if path == 0 else abs(c[-1] - c[0]) / path


def choppiness_index(trs, highs, lows):
    n, rng = len(trs), max(highs) - min(lows)
    if n < 2 or rng <= 0:
        return 100.0
    return 100.0 * math.log10(sum(trs) / rng) / math.log10(n)


class IndicatorState:
    def __init__(self, cfg):
        self.cfg = cfg
        keep = max(cfg.trend_lookback, cfg.chop_period, cfg.er_period + 1, cfg.range_lookback, 60) + 5
        self.bars, self.trs = deque(maxlen=keep), deque(maxlen=keep)
        self.atr, self.atr_long = WilderATR(cfg.atr_period), WilderATR(cfg.atr_long_period)
        self.ema_fast, self.ema_slow = EMA(cfg.ema_fast), EMA(cfg.ema_slow)
        self.count = 0

    def update(self, b):
        tr = true_range(b.high, b.low, self.bars[-1].close if self.bars else None)
        self.bars.append(b); self.trs.append(tr)
        self.atr.update(tr); self.atr_long.update(tr)
        self.ema_fast.update(b.close); self.ema_slow.update(b.close)
        self.count += 1

    @property
    def warm(self):
        c = self.cfg
        need = max(c.atr_long_period, c.ema_slow, c.trend_lookback, c.range_lookback, c.er_period + 1, c.chop_period) + 1
        return self.count >= need and self.atr_long.value is not None

    def window(self, n):
        return list(self.bars)[-n:]
