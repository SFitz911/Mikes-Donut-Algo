"""Single-machine streaming runner: ES 1m bars from the local NT8 CSV -> adaptive-trend strategy -> ES orders
via the local AddOn queue (LIVE) or a local outbox (DRY_RUN).

Copied from fitzlive/livedry.py (legacy, non-multi-setup path) with these changes:
- feed = fitzsm.feed_csv.CsvBarSource (no Dell recorder archive, no Venttari health file)
- no Jarvis account guard / breaker, no Syncthing outbox/inbox: queue + ack/results/snapshots in one local folder
- fixed protective stop (risk.stop_*) sent in every entry envelope; simulated in DRY_RUN (stop wins a same-bar tie)
- LIVE session flatten: one FLATTEN(ACCOUNT) per account per day at risk.flatten_ct (default 15:50 CT)
"""
import json, os, signal, time
from dataclasses import replace
from datetime import datetime, timezone, timedelta
from pathlib import Path
from zoneinfo import ZoneInfo
from smbt.bars import Bar
from .config import TICK_SIZE
from .breakers import Registry
from .sim import AccountSim, _close, summarize, in_entry_window, is_flatten_bar
from .execution import Executor, make_envelope, LiveRefused
from .logs import JsonlLogger
from .strategy import evaluate, read_trend
from .stops import stop_ticks, stop_price, stop_hit
from .feed_csv import CsvBarSource, update_stale_breaker

CT = ZoneInfo("America/Chicago"); UTC = timezone.utc


def ct_epoch(s):
    return int(datetime.fromisoformat(s).replace(tzinfo=CT).timestamp())


class StreamEngine:
    def __init__(self, cfg, accounts, logger, registry, executor, live_from=0, book=None):
        self.cfg, self.log, self.reg, self.ex, self.book = cfg, logger, registry, executor, book
        self.sf = cfg.risk.session_flatten
        self.stop_t = stop_ticks(cfg.risk)
        self.allow_any = cfg.execution.allow_non_sim_account_names
        tfs = {a.params.timeframe_minutes for a in accounts}; assert len(tfs) == 1, "one timeframe"
        self.tf_sec = 60 * tfs.pop()
        if any(a.params.multi_setup for a in accounts): raise RuntimeError("multi_setup not supported here")
        self.sims = [AccountSim(a, cfg.risk) for a in accounts]
        self.slip = cfg.risk.slippage_ticks_per_side * TICK_SIZE
        self.live_from = live_from
        self.bucket = []; self.bucket_key = None; self.last = None
        self.n_sig = 0; self.signals = 0
        self.flattened_day = {}     # account -> CT date of the last session-flatten envelope (LIVE)

    def _envelope(self, s, side, tp, ts):
        return make_envelope(s.a.account, side, self.cfg.risk.quantity, tp, s.a.strategy_id, self.ex.seq,
                             ts=datetime.fromtimestamp(ts, UTC), stop_ticks=self.stop_t, allow_non_sim_names=self.allow_any)

    # ------------------------------------------------------------ DRY_RUN fills / exits
    def _sim_bar(self, s, b):
        cfg, log = self.cfg, self.log
        if s.pending and s.pos is None:
            side, tp_ticks, sig_ts = s.pending; s.pending = None
            if b.ts - sig_ts > cfg.risk.stale_data_seconds:
                log.event("decision", account=s.a.account, ts=b.ts, why="entry_skipped_stale"); return
            d = 1 if side == "BUY" else -1
            fill = b.open + d * self.slip
            stop = stop_price(side, fill, self.stop_t) if self.stop_t else None
            s.pos = dict(side=side, d=d, entry=fill, entry_ts=b.ts, tp=fill + d * tp_ticks * TICK_SIZE, tp_ticks=tp_ticks, mae_pts=0.0, stop=stop)
            log.event("fill", account=s.a.account, ts=b.ts, side=side, price=fill, qty=cfg.risk.quantity, tp=s.pos["tp"], stop=stop, hypothetical=True)
        if s.pos:
            p = s.pos
            p["mae_pts"] = max(p["mae_pts"], (p["entry"] - b.low) if p["d"] > 0 else (b.high - p["entry"]))
            if p.get("stop") is not None and stop_hit(p["side"], p["stop"], b.high, b.low):
                # conservative: a bar touching both stop and TP is scored as the stop; a gap through the stop fills at the open
                px = min(b.open, p["stop"]) if p["d"] > 0 else max(b.open, p["stop"])
                _close(s, px - p["d"] * self.slip, b.ts, "STOP", cfg, log)
            elif (b.high >= p["tp"]) if p["d"] > 0 else (b.low <= p["tp"]):
                _close(s, p["tp"] - p["d"] * self.slip, b.ts, "TP", cfg, log)
            elif self.sf and is_flatten_bar(b.ts, cfg.risk.flatten_ct):
                _close(s, b.close - p["d"] * self.slip, b.ts, "SESSION_FLATTEN", cfg, log)

    def on_bar(self, b):
        cfg, log, live = self.cfg, self.log, b.ts > self.live_from
        k = -(-b.ts // self.tf_sec) * self.tf_sec
        if k != self.bucket_key: self.bucket, self.bucket_key = [], k
        self.bucket.append(b)
        if live and self.book is not None:
            self.book.refresh()
            for s in self.sims:
                st = self.book.state.get(s.a.account) or {}
                p = st.get("position")
                s.pos = None if not p else dict(side=p["side"], d=1 if p["side"] == "BUY" else -1, entry=p["avg"], entry_ts=None, tp=p["tp"], tp_ticks=None, mae_pts=0.0, confirmed_qty=p["qty"])
                s.pending = ("CONFIRMING", None, b.ts) if st.get("unconfirmed") else None
        elif live:
            for s in self.sims: self._sim_bar(s, b)
        if b.ts == self.bucket_key:
            bb = self.bucket
            sig = Bar(b.ts, bb[0].open, max(x.high for x in bb), min(x.low for x in bb), bb[-1].close, sum(x.volume for x in bb))
            self.n_sig += 1
            ok_win = in_entry_window(b.ts, cfg.risk.entry_windows_ct)
            if live: self.reg.update("session_window", not ok_win, "outside entry window", at=b.ts)
            for s in self.sims:
                s.ind.update(sig)
                dec = evaluate(s.ind, s.a.params)
                if not live: continue
                ctx = dict(tf_min=self.tf_sec // 60, bar=dict(o=sig.open, h=sig.high, l=sig.low, c=sig.close))
                if s.ind.warm:
                    trend, move = read_trend(s.ind, s.a.params)
                    ctx.update(regime=trend, move_atr=round(move, 3), atr=round(s.ind.atr.value, 3))
                if dec["signal"] and self.book is not None:
                    ok, why = self.book.can_enter(s.a.account)
                    blocks = [x["name"] for x in self.reg.blocks_entry(s.a.account)]
                    if not ok or blocks:
                        log.event("decision", account=s.a.account, ts=b.ts, why="blocked_live", reasons=why, breakers=blocks, signal=dec["signal"], **ctx)
                        continue
                    side, tp = dec["signal"]
                    try:
                        env = self._envelope(s, side, tp, b.ts)
                        self.ex.submit(env, s.a); self.book.note_submitted(env); self.signals += 1
                        log.event("decision", account=s.a.account, ts=b.ts, live_order=env["order_id"], tp_distance_pts=tp * TICK_SIZE, stop_ticks=self.stop_t, **{**ctx, **dec})
                    except Exception as e:
                        log.event("decision", account=s.a.account, ts=b.ts, why="live_refused", error=str(e), signal=dec["signal"], **ctx)
                elif dec["signal"] and s.pos is None and s.pending is None:
                    blocks = self.reg.blocks_entry(s.a.account)
                    if blocks:
                        log.event("decision", account=s.a.account, ts=b.ts, why="blocked", breakers=[x["name"] for x in blocks], signal=dec["signal"], **ctx)
                        continue
                    side, tp = dec["signal"]; self.signals += 1
                    env = self._envelope(s, side, tp, b.ts)
                    self.ex.submit(env, s.a)
                    s.pending = (side, tp, b.ts)
                    log.event("decision", account=s.a.account, ts=b.ts, would_order=True, tp_distance_pts=tp * TICK_SIZE, stop_ticks=self.stop_t, **{**ctx, **dec})
                else:
                    extra = {"signal_ignored": "position_or_pending"} if dec["signal"] else {}
                    log.event("decision", account=s.a.account, ts=b.ts, why=dec["why"], warm=s.ind.warm, in_position=s.pos is not None, **extra, **ctx)
        self.last = b

    def tick(self, now, health=None):
        """Per-loop evaluation independent of bars: stale_data (feed freshness), session_window (wall clock),
        LiveBook reconcile, LIVE session flatten."""
        if now <= self.live_from: return
        if health is not None: update_stale_breaker(self.reg, health, now)
        self.reg.update("session_window", not in_entry_window(int(now), self.cfg.risk.entry_windows_ct), "outside entry window")
        if self.book is None: return
        self.book.refresh()
        if self.sf and is_flatten_bar(int(now), self.cfg.risk.flatten_ct):
            day = datetime.fromtimestamp(now, CT).date().isoformat()
            for s in self.sims:
                a = s.a.account
                if self.flattened_day.get(a) == day: continue
                st = self.book.state.get(a) or {}
                if not st.get("position") and not st.get("unconfirmed"): continue
                env = make_envelope(a, None, flatten=True, strategy_id=s.a.strategy_id, seq=self.ex.seq,
                                    ts=datetime.fromtimestamp(now, UTC), allow_non_sim_names=self.allow_any)
                try:
                    self.ex.submit(env, s.a); self.book.note_submitted(env)
                    self.flattened_day[a] = day
                    self.log.event("session_flatten", account=a, order_id=env["order_id"], ct=datetime.fromtimestamp(now, CT).strftime("%H:%M"))
                except Exception as e:
                    self.log.event("session_flatten_failed", account=a, error=str(e))

    def summary(self):
        out = []
        for s in self.sims:
            if self.book is None:
                s.open_trade = None if not s.pos else dict(side=s.pos["side"], entry=s.pos["entry"], mae_usd=round(s.pos["mae_pts"] * s.usd_pt, 2))
                out.append(summarize(s))
            else:
                out.append(dict(account=s.a.account, live_state=self.book.state.get(s.a.account)))
        return out


def run(a, cfg):
    out = Path(a.out); out.mkdir(parents=True, exist_ok=True)
    accts = [x for x in cfg.accounts if x.enabled]
    if not accts: raise SystemExit("no accounts enabled in config")
    if a.live:
        from .execution import live_gate
        cfg.execution = replace(cfg.execution, mode="LIVE")
        try:
            for x in accts: live_gate(cfg.execution, x)
        except LiveRefused as e:
            raise SystemExit(f"LIVE refused: {e}")
    else:
        cfg.execution = replace(cfg.execution, mode="DRY_RUN", live_enabled=False, outbox_file=str(out / "dry_run_outbox.jsonl"))
    now = time.time()
    live_from = ct_epoch(a.live_from) if a.live_from else int(now)
    warm_from = ct_epoch(a.warmup_from) if a.warmup_from else live_from - 24 * 3600
    until = ct_epoch(a.until) if a.until else None
    log = JsonlLogger(out / "decisions.jsonl")
    reg = Registry([x.account for x in accts], out / "breakers_status.json")
    book = None
    if a.live:
        from .livestate import LiveBook
        book = LiveBook(cfg.execution.queue_dir, [x.account for x in accts], reg, out / "live_submitted.jsonl",
                        stale_s=30, ack_timeout_s=cfg.risk.ack_timeout_seconds, stuck_s=cfg.risk.stuck_order_seconds,
                        require_stop=stop_ticks(cfg.risk) is not None)
        log.event("startup_reconcile", state=book.refresh())
    eng = StreamEngine(cfg, accts, log, reg, Executor(cfg.execution, log, require_stop=cfg.risk.stop_enabled), live_from=live_from, book=book)
    src = CsvBarSource(cfg.feed.path, cfg.feed.instrument_root, cfg.feed.stale_after_s, min_ts=warm_from, logger=log)
    log.event("run_start", pid=os.getpid(), live=a.live, live_from=live_from, warmup_from=warm_from, accounts=[x.account for x in accts],
              qty_mes=cfg.risk.quantity, stop_ticks=eng.stop_t, session_flatten=cfg.risk.session_flatten, flatten_ct=cfg.risk.flatten_ct,
              feed=str(cfg.feed.path), queue=cfg.execution.queue_dir if a.live else None)
    print(f"fitzsm {'LIVE (NT8 SIM)' if a.live else 'DRY_RUN'}: accounts={[x.account for x in accts]} qty={cfg.risk.quantity} ES "
          f"stop={eng.stop_t} ticks flatten={'%s CT' % cfg.risk.flatten_ct if cfg.risk.session_flatten else 'off'} feed={cfg.feed.path} out={out}", flush=True)
    stop = {"v": False}
    for sg in (signal.SIGINT, getattr(signal, "SIGTERM", None), getattr(signal, "SIGBREAK", None)):
        if sg is not None:
            try: signal.signal(sg, lambda *_: stop.update(v=True))
            except (ValueError, OSError): pass

    def write_summary(final=False):
        d = dict(written_at=datetime.now(UTC).isoformat(timespec="seconds"), final=final, live=a.live,
                 last_bar_ct=datetime.fromtimestamp(eng.last.ts, CT).isoformat() if eng.last else None,
                 signal_bars_seen=eng.n_sig, orders=eng.signals, feed=src.health.as_dict(), accounts=eng.summary())
        tmp = out / "summary.json.tmp"; tmp.write_text(json.dumps(d, indent=1, default=str)); os.replace(tmp, out / "summary.json")

    last_write = last_tick = 0
    while not stop["v"] and (until is None or time.time() < until):
        for b in src.step():
            if until and b.ts > until: break
            eng.on_bar(Bar(b.ts, b.open, b.high, b.low, b.close, b.volume))
            if b.ts > live_from:
                log.event("bar", ts=b.ts, ct=datetime.fromtimestamp(b.ts, CT).strftime("%H:%M"), o=b.open, h=b.high, l=b.low, c=b.close, v=b.volume, contract=b.contract)
        log.f.flush()
        now = time.time()
        if now - last_tick >= 2:
            try: eng.tick(now, src.health)
            except Exception as e: log.event("tick_error", error=f"{type(e).__name__}: {e}")
            last_tick = now
        if now - last_write > 30:
            write_summary(); reg.write({"mode": cfg.execution.mode}); last_write = now
        if a.once: break
        time.sleep(a.poll)
    write_summary(final=True); reg.write({"mode": cfg.execution.mode}); log.event("run_stop"); log.close()
