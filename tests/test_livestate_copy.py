"""Copied from tests/test_livestate.py, pointed at fitzsm.livestate (unchanged LiveBook behaviour)."""
import json
from datetime import datetime, timezone
import pytest
from fitzsm.breakers import Registry
from fitzsm.livestate import LiveBook

A = "SimVQE06"
T0 = 1_800_000_000.0

def iso(t): return datetime.fromtimestamp(t, timezone.utc).isoformat()

class Clock:
    def __init__(self): self.t = T0
    def __call__(self): return self.t

def mk(tmp_path, clock=None):
    for d in ("ack", "results", "snapshots"): (tmp_path / "inbox" / d).mkdir(parents=True, exist_ok=True)
    reg = Registry([A])
    return LiveBook(tmp_path / "inbox", [A], reg, tmp_path / "j.jsonl", clock=clock or Clock()), reg

def snap(tmp_path, t, positions=(), orders=(), mode="SIM_EXECUTION"):
    d = {"mode": mode, "observed_at": iso(t), "accounts": {A: {"present": True, "connection": "Connected", "positions": list(positions), "orders": list(orders)}}}
    (tmp_path / "inbox" / "snapshots" / "fitz_snapshot.json").write_text(json.dumps(d))

def w(tmp_path, sub, name, d): (tmp_path / "inbox" / sub / name).write_text(json.dumps(d))

ENV = {"order_id": "FitzLabs_SimVQE06-X-0001", "account": A, "side": "BUY", "quantity": 10}
OID = ENV["order_id"]

def test_flat_fresh_allows_entry(tmp_path):
    b, _ = mk(tmp_path); snap(tmp_path, T0); b.refresh()
    assert b.can_enter(A) == (True, [])

def test_unconfirmed_blocks_then_fill_confirms(tmp_path):
    b, _ = mk(tmp_path); snap(tmp_path, T0); b.note_submitted(ENV); b.refresh()
    ok, why = b.can_enter(A); assert not ok and any("no ack" in x for x in why)
    w(tmp_path, "ack", OID + ".json", {"order_id": OID, "status": "SUBMITTED", "account": A})
    w(tmp_path, "results", OID + ".trade.json", {"order_id": OID, "account": A, "side": "BUY", "filled": 10, "avg_fill": 6000.25, "tp_price": 6010.25, "status": "OPEN"})
    snap(tmp_path, T0, positions=[{"instrument": "ES 12-26", "quantity": 10, "direction": "Long"}], orders=[{"name": "FitzTP_SimVQE06-X-0001", "fitz": True}])
    st = b.refresh()[A]
    assert st["position"]["qty"] == 10 and not st["unconfirmed"]
    ok, why = b.can_enter(A); assert not ok and "confirmed position open" in why
    assert all(x["state"] == "CLEAR" for x in b.reg.snapshot() if x["name"] == "position_mismatch")

def test_partial_fill_blocks_and_tp_sized_to_confirmed(tmp_path):
    b, reg = mk(tmp_path); b.note_submitted(ENV)
    w(tmp_path, "ack", OID + ".json", {"order_id": OID, "status": "SUBMITTED", "account": A})
    w(tmp_path, "results", OID + ".trade.json", {"order_id": OID, "account": A, "side": "BUY", "filled": 0, "status": "SUBMITTED"})
    w(tmp_path, "results", OID + ".order.20261019220000000.json", {"name": OID, "state": "PartFilled", "filled": 7, "account": A})
    snap(tmp_path, T0, positions=[{"instrument": "ES 12-26", "quantity": 7, "direction": "Long"}])
    st = b.refresh()[A]; assert st["unconfirmed"] and not b.can_enter(A)[0]
    assert reg.items[(A, "position_mismatch")]["state"] == "CLEAR"   # in-flight: no false mismatch
    w(tmp_path, "results", OID + ".trade.json", {"order_id": OID, "account": A, "side": "BUY", "filled": 7, "avg_fill": 6000.0, "tp_price": 6010.0, "status": "OPEN"})
    w(tmp_path, "results", OID + ".order.20261019220001000.json", {"name": OID, "state": "Cancelled", "filled": 7, "account": A})
    snap(tmp_path, T0, positions=[{"instrument": "ES 12-26", "quantity": 7, "direction": "Long"}], orders=[{"name": "FitzTP_SimVQE06-X-0001", "fitz": True, "quantity": 7}])
    st = b.refresh()[A]; assert st["position"]["qty"] == 7 and not st["unconfirmed"]

def test_reject_clears_unconfirmed(tmp_path):
    b, _ = mk(tmp_path); snap(tmp_path, T0); b.note_submitted(ENV)
    w(tmp_path, "ack", OID + ".json", {"order_id": OID, "status": "REJECTED", "account": A, "reason": "x"})
    b.refresh(); assert b.can_enter(A) == (True, [])

def test_stale_snapshot_blocks(tmp_path):
    c = Clock(); b, reg = mk(tmp_path, c); snap(tmp_path, T0 - 31); b.refresh()
    ok, why = b.can_enter(A); assert not ok and "snapshot missing/stale" in why
    assert reg.items[("LAB", "bridge_unreachable")]["state"] == "TRIPPED"

def test_missing_snapshot_and_held_block(tmp_path):
    b, _ = mk(tmp_path); b.refresh(); assert not b.can_enter(A)[0]
    snap(tmp_path, T0, mode="HELD"); b.refresh(); ok, why = b.can_enter(A); assert not ok and any("HELD" in x for x in why)

def test_mismatch_grace_then_trip_then_autoclear(tmp_path):
    c = Clock(); b, reg = mk(tmp_path, c); S = [{"instrument": "ES 12-26", "quantity": 3, "direction": "Short"}]
    for k in range(3):   # 3 fresh snapshots but < 90 s: still in grace, entries blocked meanwhile
        c.t = T0 + 3 * k; snap(tmp_path, c.t, positions=S); b.refresh()
        assert reg.items[(A, "position_mismatch")]["state"] == "CLEAR" and not b.can_enter(A)[0]
    c.t = T0 + 91; snap(tmp_path, c.t, positions=S); b.refresh()
    assert reg.items[(A, "position_mismatch")]["state"] == "TRIPPED"
    for k in range(1, 3):
        c.t = T0 + 91 + 3 * k; snap(tmp_path, c.t); b.refresh()
        assert reg.items[(A, "position_mismatch")]["state"] == "TRIPPED"
    c.t += 3; snap(tmp_path, c.t); b.refresh()   # 3rd consecutive flat/flat, no orders -> auto-clear, no approval
    assert reg.items[(A, "position_mismatch")]["state"] == "CLEAR" and b.can_enter(A)[0]

def test_foreign_order_is_mismatch(tmp_path):
    b, reg = mk(tmp_path); snap(tmp_path, T0, orders=[{"name": "JarvisEntry", "fitz": False}]); b.refresh()
    assert reg.items[(A, "position_mismatch")]["state"] == "TRIPPED"

def test_ack_timeout(tmp_path):
    c = Clock(); b, reg = mk(tmp_path, c); snap(tmp_path, T0); b.note_submitted(ENV)
    c.t = T0 + 31; snap(tmp_path, c.t); b.refresh()
    assert reg.items[(A, "ack_timeout")]["state"] == "TRIPPED"

def test_restart_reconcile(tmp_path):
    b, _ = mk(tmp_path); b.note_submitted(ENV)        # submitted, then runner dies before any ack
    b2, _ = mk(tmp_path); snap(tmp_path, T0); b2.refresh()
    assert not b2.can_enter(A)[0]                      # journal survives restart -> still unconfirmed
    w(tmp_path, "results", OID + ".trade.json", {"order_id": OID, "account": A, "side": "SELL", "filled": 10, "avg_fill": 6000.0, "tp_price": 5990.0, "status": "OPEN"})
    snap(tmp_path, T0, positions=[{"instrument": "ES 12-26", "quantity": 10, "direction": "Short"}], orders=[{"name": "FitzTP_x", "fitz": True}])
    b3, reg3 = mk(tmp_path); st = b3.refresh()[A]
    assert st["position"] == {"order_id": OID, "side": "SELL", "qty": 10, "avg": 6000.0, "tp": 5990.0}
    assert reg3.items[(A, "position_mismatch")]["state"] == "CLEAR"

def test_restart_uncertain_trade_blocks(tmp_path):
    b, _ = mk(tmp_path); snap(tmp_path, T0)
    w(tmp_path, "results", OID + ".trade.json", {"order_id": OID, "account": A, "side": "BUY", "filled": 0, "status": "UNCERTAIN"})
    b.refresh(); ok, why = b.can_enter(A); assert not ok and any("UNCERTAIN" in x for x in why)
