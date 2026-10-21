"""Execution adapter (single machine). Envelopes are written straight into the local AddOn queue folder
(no Syncthing, no Jarvis guard, no Dell). Schema 1.1, same fields the FitzLabsBridge contract uses, plus
risk.stop_loss_ticks for the fixed protective stop.

DRY_RUN (default): envelopes go to a local JSONL outbox only. LIVE: every gate below must pass."""
import json, os
from datetime import datetime, timezone
from pathlib import Path
from . import CLIENT_ID, ORDER_PREFIX
from .accounts import account_allowed
from .config import MAX_QUANTITY, ORDER_SYMBOL

JARVIS_QUEUE_NAMES = {"nt8_queue"}


class LiveRefused(RuntimeError):
    pass


def not_before_epoch(s):
    from zoneinfo import ZoneInfo
    return int(datetime.fromisoformat(s).replace(tzinfo=ZoneInfo("America/Chicago")).timestamp())


def time_gate(exec_cfg, now=None):
    now = datetime.now(timezone.utc).timestamp() if now is None else now
    if now < not_before_epoch(exec_cfg.not_before_ct):
        raise LiveRefused(f"time gate: no orders before {exec_cfg.not_before_ct} CT")
    return True


def write_queue_atomic(queue_dir, env):
    """<order_id>.json.tmp -> fsync -> rename to <order_id>.json (the AddOn reads only *.json)."""
    d = Path(queue_dir); d.mkdir(parents=True, exist_ok=True)
    final = d / (env["order_id"] + ".json")
    if final.exists(): raise LiveRefused(f"duplicate order_id in queue: {final.name}")
    tmp = d / (env["order_id"] + ".json.tmp")
    with tmp.open("w", encoding="utf-8") as f:
        f.write(json.dumps(env)); f.flush(); os.fsync(f.fileno())
    os.replace(tmp, final)
    return final


def make_envelope(account, side, qty=None, tp_ticks=None, strategy_id="", seq=0, ts=None, flatten=False,
                  stop_ticks=None, allow_non_sim_names=False):
    ts = ts or datetime.now(timezone.utc)
    oid = f"{ORDER_PREFIX}{account}-{ts.strftime('%Y%m%dT%H%M%S')}-{seq:04d}"
    risk = None
    if not flatten:
        risk = {"stop_loss_ticks": None if stop_ticks is None else int(stop_ticks), "take_profit_ticks": int(tp_ticks),
                "notes": "Fitz Labs single-machine: TP + fixed stop (OCO)" if stop_ticks else "Fitz Labs single-machine: TP only"}
    env = {
        "schema_version": "1.1", "bridge_status": "QUEUED", "client_id": CLIENT_ID,
        "order_id": oid, "account": account,
        "symbol": None if flatten else ORDER_SYMBOL,
        "side": "FLATTEN" if flatten else side,
        "quantity": None if flatten else int(qty),
        "flatten_scope": "ACCOUNT" if flatten else None,
        "entry_type": "MARKET", "entry_price": None,
        "risk": risk,
        "risk_status": "GREEN", "paper_only": True, "verifier_status": "APPROVED",
        "confidence": 0, "queued_at": ts.isoformat(timespec="seconds"),
        "strategy_id": strategy_id or "FITZLABS-SM", "experiment_id": "FITZLABS-SM-001", "slot": 0,
        "evidence_tier": "FITZ_LABS_SIM", "max_hold_seconds": None,
    }
    validate(env, allow_non_sim_names)
    return env


def validate(env, allow_non_sim_names=False, require_stop=False):
    """Python mirror of the standalone AddOn gates."""
    errs = []
    if env.get("client_id") != CLIENT_ID: errs.append("client_id must be FITZ_LABS")
    if not str(env.get("order_id", "")).startswith(ORDER_PREFIX): errs.append("order_id must start with FitzLabs_")
    if len(str(env.get("order_id", ""))) > 64: errs.append("order_id max 64 chars")
    if env.get("schema_version") != "1.1": errs.append("schema_version")
    if env.get("bridge_status") != "QUEUED": errs.append("bridge_status")
    if env.get("paper_only") is not True: errs.append("paper_only must be true")
    if not account_allowed(env.get("account"), allow_non_sim_names): errs.append("account name not allowed (sim-only rule)")
    side = env.get("side")
    if side in ("BUY", "SELL"):
        if env.get("symbol") != ORDER_SYMBOL: errs.append(f"symbol must be {ORDER_SYMBOL}")
        q = env.get("quantity")
        if not isinstance(q, int) or isinstance(q, bool) or not 0 < q <= MAX_QUANTITY: errs.append(f"quantity 1..{MAX_QUANTITY}")
        if env.get("flatten_scope") is not None: errs.append("flatten_scope must be null")
        r = env.get("risk") or {}
        tp = r.get("take_profit_ticks")
        if not isinstance(tp, int) or not 1 <= tp <= 2000: errs.append("take_profit_ticks 1..2000")
        sl = r.get("stop_loss_ticks")
        if sl is not None and (not isinstance(sl, int) or isinstance(sl, bool) or not 1 <= sl <= 2000): errs.append("stop_loss_ticks 1..2000 or null")
        if require_stop and sl is None: errs.append("stop_loss_ticks required (risk.stop_enabled)")
    elif side == "FLATTEN":
        if env.get("symbol") is not None or env.get("quantity") is not None or env.get("risk") is not None:
            errs.append("FLATTEN must have no symbol/quantity/risk")
        if env.get("flatten_scope") != "ACCOUNT": errs.append("FLATTEN needs flatten_scope ACCOUNT")
    else:
        errs.append("side")
    if env.get("entry_type") != "MARKET": errs.append("entry_type must be MARKET")
    if errs:
        raise ValueError("; ".join(errs))
    return True


def live_gate(exec_cfg, account_cfg=None):
    """LIVE needs: mode LIVE + live_enabled + approval file signed by the configured approver naming the account
    + queue dir is not a Jarvis queue + sim-only account name."""
    if exec_cfg.mode != "LIVE": raise LiveRefused("mode is not LIVE")
    if not exec_cfg.live_enabled: raise LiveRefused("execution.live_enabled is false")
    q = Path(str(exec_cfg.queue_dir).replace("\\", "/"))
    if q.name.lower() in JARVIS_QUEUE_NAMES or "nt8_queue" in [p.lower() for p in q.parts[:-1]]:
        raise LiveRefused("queue_dir points at/inside a Jarvis nt8_queue")
    ap = Path(exec_cfg.approval_file)
    if not ap.exists(): raise LiveRefused(f"approval file missing: {ap}")
    try:
        a = json.loads(ap.read_text(encoding="utf-8"))
    except Exception:
        raise LiveRefused("approval file unreadable")
    if not exec_cfg.approver or a.get("approved_by") != exec_cfg.approver or not a.get("statement"):
        raise LiveRefused(f"approval must have approved_by={exec_cfg.approver!r} and an explicit statement")
    if account_cfg is not None:
        if not account_allowed(account_cfg.account, exec_cfg.allow_non_sim_account_names):
            raise LiveRefused(f"{account_cfg.account}: not a Sim* account name (set allow_non_sim_account_names only for a renamed SIM account)")
        if not account_cfg.enabled: raise LiveRefused(f"{account_cfg.account} not enabled in config")
        if account_cfg.account not in a.get("accounts", []): raise LiveRefused(f"{account_cfg.account} not in approval")
    return True


class Executor:
    def __init__(self, exec_cfg, logger, require_stop=False):
        self.cfg, self.log, self.seq, self.require_stop = exec_cfg, logger, 0, require_stop

    def submit(self, env, account_cfg=None):
        self.seq += 1
        validate(env, self.cfg.allow_non_sim_account_names, self.require_stop and env.get("side") in ("BUY", "SELL"))
        if self.cfg.mode == "LIVE":
            live_gate(self.cfg, account_cfg)
            if env.get("side") in ("BUY", "SELL"): time_gate(self.cfg)   # exits/flatten never time-gated
            path = write_queue_atomic(self.cfg.queue_dir, env)
            self.log.event("order", mode="LIVE", envelope=env, queue=str(path))
            return env["order_id"]
        p = Path(self.cfg.outbox_file); p.parent.mkdir(parents=True, exist_ok=True)
        with p.open("a", encoding="utf-8") as f:
            f.write(json.dumps(env) + "\n")
        self.log.event("order", mode="DRY_RUN", envelope=env)
        return env["order_id"]
