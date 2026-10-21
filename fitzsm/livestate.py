"""(single-machine copy) LIVE position/order state driven ONLY by the standalone AddOn outputs, read from the
local AddOn queue folder (same PC, no sync):
  ack/<order_id>.json            {order_id, status: SUBMITTED|REJECTED|DUPLICATE, account, reason, timestamp}
  results/<order_id>.trade.json  {order_id, account, side, instrument, tp_ticks, filled, avg_fill, tp_price,
                                  status: SUBMITTED|OPEN|TP_FILLED|CLOSED|ENTRY_FAILED|UNCERTAIN}
  results/<name>.order.<ts>.json {name, state, filled, avg_fill, account, error, time}   (NT8 OrderUpdate)
  snapshots/fitz_snapshot.json   {mode, observed_at, accounts:{acct:{present, connection, positions[], orders[]}}}
The add-on itself places the TP only after the entry order is terminal with filled>0, sized to the confirmed
filled qty (FitzLabsBridge.OnOrderUpdate -> PlaceTarget); the runner never sends a TP or a stop.
Fail closed: no new entry on an account while any order is unconfirmed, when the snapshot is missing/stale/HELD,
or when snapshot and internal state disagree (position_mismatch breaker: 3-snapshot/90s trip grace, auto-clears only on strict agreement; docs/BREAKERS.md)."""
import glob, json, os, time
from datetime import datetime
from pathlib import Path

ORDER_TERMINAL = {"Filled", "Cancelled", "Rejected"}
TERMINAL_DONE = {"TP_FILLED", "SL_FILLED", "CLOSED", "ENTRY_FAILED"}
UNCONFIRMED_TRADE = {"SUBMITTED", "UNCERTAIN"}
from . import ORDER_PREFIX, TARGET_PREFIX, STOP_PREFIX, CLOSE_PREFIX
from .config import ORDER_SYMBOL


def _read(p):
    try:
        return json.loads(Path(p).read_text())
    except Exception:
        return None


def _epoch(iso):
    try:
        return datetime.fromisoformat(str(iso).replace("Z", "+00:00")).timestamp()
    except Exception:
        return None


class LiveBook:
    def __init__(self, inbox_dir, accounts, registry, journal_file, stale_s=30, ack_timeout_s=30, clock=time.time, stuck_s=120, require_stop=False):
        self.inbox = Path(inbox_dir); self.accounts = list(accounts); self.reg = registry
        self.journal = Path(journal_file); self.stale_s, self.ack_timeout_s, self.clock = stale_s, ack_timeout_s, clock
        self.stuck_s, self._cp_seen, self._last_obs = stuck_s, {}, None
        self.require_stop = require_stop
        self.submitted = {}   # order_id -> {account, side, qty, ts}  (persisted journal, survives restart)
        if self.journal.exists():
            for line in self.journal.read_text().splitlines():
                try:
                    d = json.loads(line); self.submitted[d["order_id"]] = d
                except Exception:
                    pass
        self.acks, self.trades, self.order_events, self.snapshot = {}, {}, {}, None
        self.state = {a: {} for a in self.accounts}

    def note_submitted(self, env):
        d = dict(order_id=env["order_id"], account=env["account"], side=env["side"], qty=env.get("quantity"), ts=self.clock())
        self.submitted[d["order_id"]] = d
        self.journal.parent.mkdir(parents=True, exist_ok=True)
        with self.journal.open("a") as f:
            f.write(json.dumps(d) + "\n"); f.flush(); os.fsync(f.fileno())

    def _load(self):
        self.acks = {}
        for p in glob.glob(str(self.inbox / "ack" / "*.json")):
            d = _read(p)
            if d and d.get("order_id") and ".duplicate_" not in os.path.basename(p): self.acks[d["order_id"]] = d
        self.trades = {}
        for p in glob.glob(str(self.inbox / "results" / "*.trade.json")):
            d = _read(p)
            if d and str(d.get("order_id", "")).startswith(ORDER_PREFIX): self.trades[d["order_id"]] = d
        self.order_events = {}
        for p in sorted(glob.glob(str(self.inbox / "results" / "*.order.*.json"))):   # names sort by timestamp
            d = _read(p)
            if d and d.get("name"): self.order_events[d["name"]] = d                  # keep latest per order
        self.snapshot = _read(self.inbox / "snapshots" / "fitz_snapshot.json")

    def refresh(self):
        """Reconcile add-on outputs with the runner. Safe to call every loop (not only on bars): breaker streaks
        for position_mismatch / ack_timeout / stuck_order advance only once per NEW snapshot (observed_at)."""
        self._load(); now = self.clock()
        snap = self.snapshot; snap_reason = None
        if not snap: snap_reason = "no fitz_snapshot.json"
        else:
            age = now - (_epoch(snap.get("observed_at")) or 0)
            if age > self.stale_s: snap_reason = f"snapshot stale ({age:.0f}s > {self.stale_s}s)"
        self.reg.update("bridge_unreachable", snap_reason is not None, snap_reason or "", now=now)
        fresh_new = snap_reason is None and snap.get("observed_at") != self._last_obs
        if fresh_new: self._last_obs = snap.get("observed_at")
        for a in self.accounts:
            st = dict(position=None, unconfirmed=[], blocks=[], snapshot_ok=snap_reason is None)
            for oid, t in self.trades.items():
                if t.get("account") != a: continue
                s = t.get("status")
                if s == "OPEN":
                    if st["position"]: st["blocks"].append(f"two OPEN trades ({st['position']['order_id']}, {oid})")
                    st["position"] = dict(order_id=oid, side=t["side"], qty=int(t.get("filled") or 0), avg=t.get("avg_fill"), tp=t.get("tp_price"))
                elif s in UNCONFIRMED_TRADE:
                    st["unconfirmed"].append(f"{oid}: trade {s}")
            timed_out = []
            for oid, sub in self.submitted.items():
                if sub["account"] != a or oid in self.trades: continue
                ack = self.acks.get(oid)
                if ack and ack.get("status") == "REJECTED": continue          # confirmed terminal, nothing placed
                ev = self.order_events.get(oid)
                if ev and ev.get("state") in ("Rejected", "Cancelled") and not ev.get("filled"): continue
                st["unconfirmed"].append(f"{oid}: {'ack ' + ack['status'] if ack else 'no ack yet'}")
                confirmed = ack is not None or (ev is not None and ev.get("state") in ORDER_TERMINAL)
                if not confirmed and now - sub["ts"] > self.ack_timeout_s:
                    timed_out.append(f"{oid} no ack after {now - sub['ts']:.0f}s")
            for name, ev in self.order_events.items():                       # partial fills = still working
                if ev.get("account") == a and name.startswith(ORDER_PREFIX) and ev.get("state") in ("PartFilled", "Accepted", "Working", "Submitted", "Initialized", "CancelPending", "ChangePending"):
                    if not any(name in u for u in st["unconfirmed"]): st["unconfirmed"].append(f"{name}: order {ev['state']} filled={ev.get('filled')}")
            mismatch, hard, agree, stuck = None, False, False, []
            if snap_reason is None:
                acc = (snap.get("accounts") or {}).get(a)
                if not acc or not acc.get("present"): st["blocks"].append("account not present in snapshot")
                elif acc.get("connection") != "Connected": st["blocks"].append(f"account connection {acc.get('connection')}")
                else:
                    orders = acc.get("orders", []) or []
                    pos = [p for p in acc.get("positions", []) if p.get("quantity")]
                    snap_signed = sum((1 if p.get("direction") == "Long" else -1) * int(p["quantity"]) for p in pos)
                    exp = st["position"]; exp_signed = 0 if not exp else (1 if exp["side"] == "BUY" else -1) * exp["qty"]
                    foreign = [o["name"] for o in orders if not o.get("fitz")]
                    working = [o for o in orders if o.get("state") not in ORDER_TERMINAL]
                    if foreign: mismatch, hard = f"foreign working order(s) {foreign}", True
                    elif any(str(p.get("instrument", "")).strip().split(" ")[0].upper() != ORDER_SYMBOL for p in pos): mismatch, hard = f"non-{ORDER_SYMBOL} position", True
                    elif not st["unconfirmed"] and snap_signed != exp_signed:
                        mismatch = f"snapshot position {snap_signed} != runner {exp_signed}"
                    elif not st["unconfirmed"]:
                        agree = (snap_signed == exp_signed != 0) or (snap_signed == exp_signed == 0 and not working)
                        if exp and not [o for o in orders if o.get("name", "").startswith(TARGET_PREFIX)]:
                            st["blocks"].append("OPEN trade without working FitzTP_ yet")
                        if exp and self.require_stop and not [o for o in orders if o.get("name", "").startswith(STOP_PREFIX)]:
                            st["blocks"].append("OPEN trade without working FitzSL_ stop yet")
                    # stuck_order: age CancelPending from first sighting (snapshot has no since field)
                    seen = self._cp_seen.setdefault(a, {})
                    cp = {o["name"] for o in orders if o.get("name", "").startswith(ORDER_PREFIX) and o.get("state") == "CancelPending"}
                    for n in list(seen):
                        if n not in cp: del seen[n]
                    for n in cp: seen.setdefault(n, now)
                    stuck = [n for n in cp if now - seen[n] > self.stuck_s]
                    st["snapshot_mode"] = snap.get("mode")
            if mismatch: st["blocks"].append(f"mismatch: {mismatch}")      # block during trip grace too
            if fresh_new:
                if mismatch: self.reg.update("position_mismatch", True, mismatch, scope=a, now=now, immediate=hard)
                elif agree: self.reg.update("position_mismatch", False, scope=a, now=now)
                else: self.reg.neutral("position_mismatch", scope=a)          # in flight / not comparable
                if timed_out: self.reg.update("ack_timeout", True, "; ".join(timed_out), scope=a, now=now)
                elif not mismatch and (agree or not st["unconfirmed"]): self.reg.update("ack_timeout", False, scope=a, now=now)
                else: self.reg.neutral("ack_timeout", scope=a)
                self.reg.update("stuck_order", bool(stuck), f"CancelPending > {self.stuck_s}s: {sorted(stuck)}" if stuck else "", scope=a, now=now)
            if snap and snap.get("mode") != "SIM_EXECUTION": st["blocks"].append(f"add-on mode {snap.get('mode')}")
            self.state[a] = st
        return self.state

    def can_enter(self, account):
        st = self.state.get(account) or {}
        why = []
        if not st.get("snapshot_ok"): why.append("snapshot missing/stale")
        if st.get("position"): why.append("confirmed position open")
        why += [f"unconfirmed {u}" for u in st.get("unconfirmed", [])] + st.get("blocks", [])
        why += [f"breaker {b['name']}" for b in self.reg.blocks_entry(account) if b["name"] != "session_window"]
        return (not why), why
