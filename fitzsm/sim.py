"""(single-machine copy) Session-time helpers + simulated account book for DRY_RUN, copied from
fitzlive/engine.py. Exits: TP, fixed stop (if enabled), session flatten."""
from datetime import datetime
from zoneinfo import ZoneInfo
from .config import TICK_SIZE, ES_TICK_VALUE
from .strategy import Indicators

CT = ZoneInfo("America/Chicago")
UTC = ZoneInfo("UTC")


def _hm(s):
    h, m = s.split(":"); return int(h) * 60 + int(m)


def in_entry_window(ts, windows):
    t = datetime.fromtimestamp(ts, CT); m = t.hour * 60 + t.minute
    return any(_hm(a) <= m <= _hm(b) for a, b in windows)


def is_flatten_bar(ts, flatten_ct):
    """First bar at/after flatten time up to the 17:00 reopen (a missing 15:50 bar must not skip the flatten)."""
    t = datetime.fromtimestamp(ts, CT); m = t.hour * 60 + t.minute
    return _hm(flatten_ct) <= m < 17 * 60


class AccountSim:
    def __init__(self, acfg, risk):
        self.a, self.r = acfg, risk
        self.ind = Indicators(acfg.params)
        self.pos = None; self.pending = None; self.trades = []

    @property
    def usd_pt(self):
        return ES_TICK_VALUE / TICK_SIZE * self.r.quantity   # $50/pt x ES contracts


def _close(s, price, ts, reason, cfg, logger):
    p = s.pos
    gross = (price - p["entry"]) * p["d"] * s.usd_pt
    net = gross - cfg.risk.fee_round_trip_usd
    t = dict(side=p["side"], entry=p["entry"], exit=price, entry_ts=p["entry_ts"], exit_ts=ts, reason=reason,
             tp_ticks=p["tp_ticks"], stop=p.get("stop"), gross=round(gross, 2), net=round(net, 2),
             mae_pts=round(p["mae_pts"], 2), mae_usd=round(p["mae_pts"] * s.usd_pt, 2), hold_min=(ts - p["entry_ts"]) // 60 + 1)
    s.trades.append(t); s.pos = None
    logger.event("fill", account=s.a.account, ts=ts, exit=price, reason=reason)
    logger.event("pnl", account=s.a.account, **t)


def summarize(s):
    tr = s.trades; nets = [t["net"] for t in tr]
    wins = [x for x in nets if x > 0]; losses = [x for x in nets if x <= 0]
    eq = pk = dd = 0.0
    for x in nets:
        eq += x; pk = max(pk, eq); dd = max(dd, pk - eq)
    ot = s.open_trade
    worst_mae = max([t["mae_usd"] for t in tr] + ([ot["mae_usd"]] if ot else []), default=0.0)
    return dict(account=s.a.account, trigger=s.a.params.trigger, trades=len(tr), net=round(sum(nets), 2),
                win_rate=round(len(wins) / len(tr), 3) if tr else None,
                pf=round(sum(wins) / -sum(losses), 2) if losses and sum(losses) < 0 else None,
                max_dd_realized=round(dd, 2), worst_mae_usd=round(worst_mae, 2),
                avg_mae_usd=round(sum(t["mae_usd"] for t in tr) / len(tr), 2) if tr else None,
                avg_hold_min=round(sum(t["hold_min"] for t in tr) / len(tr), 1) if tr else None,
                max_hold_min=max((t["hold_min"] for t in tr), default=None),
                open_at_end=ot)
