"""(single-machine copy, no Jarvis breaker) Circuit breakers (Fitz Labs R-001, auto-clear only since 2026-10-20): visible name, reason, tripped_at,
state, clear_condition. States: CLEAR | TRIPPED. There is NO approval step and NO PENDING_FIX state: every
breaker re-evaluates on every sample and returns to CLEAR by itself once its (strict) clear condition has held
for `clear_n` consecutive samples. A breaker whose bad condition persists stays TRIPPED and blocks entries.
Optional trip grace: `trip_n` consecutive bad samples AND `trip_s` seconds of continuous badness before tripping
(callers block entries themselves while a condition is bad-but-not-yet-tripped). `immediate=True` skips grace.
Per Sean 2026-10-19 15:48 CT there are NO loss-based breakers."""
import json, os, time
from datetime import datetime, timezone
from pathlib import Path

DEFS = {
    # name: clear_condition (all auto-clear)
    "stale_data": "newest ES 1m bar in the NT8 CSV ends within feed.stale_after_s (evaluated every runner loop)",
    "bridge_unreachable": "AddOn queue readable and fitz_snapshot.json fresh",
    "ack_timeout": "every timed-out order acked or terminal in NT8 order events, AND account position matches NT8",
    "position_mismatch": "runner and NT8 agree (same signed ES position, or both flat with no working orders), no foreign/non-ES orders or positions, for clear_n consecutive fresh snapshots",
    "stuck_order": "no FitzLabs_ order in CancelPending > stuck_order_seconds for clear_n consecutive fresh snapshots",
    "session_window": "inside an entry window (00:00-15:45, 17:00-23:59 CT), evaluated every runner loop",

}

DEFAULT_POLICY = {n: dict(trip_n=1, trip_s=0.0, clear_n=1) for n in DEFS}
DEFAULT_POLICY["position_mismatch"] = dict(trip_n=3, trip_s=90.0, clear_n=3)
DEFAULT_POLICY["stuck_order"] = dict(trip_n=1, trip_s=0.0, clear_n=2)


def _now():
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


class Registry:
    def __init__(self, scope_accounts=(), status_file=None, policy=None, clock=time.time):
        self.status_file = Path(status_file) if status_file else None
        self.clock = clock
        self.policy = {n: dict(DEFAULT_POLICY[n], **((policy or {}).get(n) or {})) for n in DEFS}
        self.items = {}
        for scope in ["LAB", *scope_accounts]:
            for n in DEFS:
                self.items[(scope, n)] = {"scope": scope, "name": n, "state": "CLEAR", "reason": "",
                                          "tripped_at": None, "cleared_at": None, "clear_condition": DEFS[n],
                                          "auto_clear": True, "bad_streak": 0, "good_streak": 0,
                                          "bad_since": None, "last_bad_reason": ""}

    def update(self, name, bad, reason="", scope="LAB", at=None, now=None, immediate=False):
        b = self.items[(scope, name)]; p = self.policy[name]
        at = at or _now(); now = self.clock() if now is None else now
        if bad:
            b["good_streak"] = 0; b["bad_streak"] += 1; b["last_bad_reason"] = reason
            if b["bad_since"] is None: b["bad_since"] = now
            if b["state"] != "TRIPPED" and (immediate or (b["bad_streak"] >= p["trip_n"] and now - b["bad_since"] >= p["trip_s"])):
                b.update(state="TRIPPED", reason=reason, tripped_at=at, cleared_at=None)
            return b
        b["bad_streak"] = 0; b["bad_since"] = None
        if b["state"] == "TRIPPED":
            b["good_streak"] += 1
            if b["good_streak"] >= p["clear_n"]:
                b.update(state="CLEAR", cleared_at=at, good_streak=0)
        else:
            b["good_streak"] = 0
        return b

    def neutral(self, name, scope="LAB"):
        """A sample that is neither good nor bad (e.g. order in flight): breaks both streaks, keeps state."""
        b = self.items[(scope, name)]; b.update(bad_streak=0, good_streak=0, bad_since=None)
        return b

    def blocks_entry(self, scope):
        return [b for (s, _), b in self.items.items() if s in ("LAB", scope) and b["state"] != "CLEAR"]

    def snapshot(self):
        return list(self.items.values())

    def write(self, extra=None):
        if not self.status_file:
            return
        self.status_file.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.status_file.with_suffix(".tmp")
        tmp.write_text(json.dumps({"written_at": _now(), "breakers": self.snapshot(), **(extra or {})}, indent=1))
        os.replace(tmp, self.status_file)


def check_stuck_orders(orders, now_epoch, limit_s):
    """orders: [{name, state, since_epoch}] from an NT8 snapshot. Only FitzLabs_ orders count."""
    return [o for o in orders if o.get("name", "").startswith("FitzLabs_") and o.get("state") == "CancelPending"
            and now_epoch - o.get("since_epoch", now_epoch) > limit_s]


def check_mismatch(expected: dict, snapshot: dict):
    """expected/snapshot: {account: signed qty}. Returns accounts that differ."""
    return sorted(a for a in set(expected) | set(snapshot) if expected.get(a, 0) != snapshot.get(a, 0))
