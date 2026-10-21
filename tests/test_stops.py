import json
from dataclasses import replace
from datetime import datetime, timezone
import pytest
from fitzsm.config import RiskConfig, ExecConfig, Config, AccountConfig, StrategyParams
from fitzsm.stops import stop_ticks, stop_price, stop_hit
from fitzsm.execution import make_envelope, validate, Executor, live_gate, LiveRefused
from fitzsm.accounts import account_allowed
from fitzsm.breakers import Registry
from fitzsm.runner import StreamEngine
from smbt.bars import Bar


class NullLog:
    def __init__(self): self.events = []
    def event(self, kind, **kw): self.events.append((kind, kw))


# ---------------------------------------------------------------- distance
def test_default_stop_on_40_ticks():
    r = RiskConfig(); assert r.stop_enabled and stop_ticks(r) == 40

def test_stop_points_override_ticks():
    assert stop_ticks(RiskConfig(stop_points=6.5, stop_ticks=40)) == 26

def test_stop_disabled_returns_none():
    assert stop_ticks(RiskConfig(stop_enabled=False)) is None

@pytest.mark.parametrize("kw", [dict(stop_points=0), dict(stop_points=-1), dict(stop_points=1.1), dict(stop_ticks=0),
                                dict(stop_ticks=2001), dict(stop_ticks=None), dict(stop_ticks=2.5), dict(stop_ticks=True)])
def test_bad_stop_config_raises(kw):
    with pytest.raises(ValueError): stop_ticks(RiskConfig(**kw))

# ---------------------------------------------------------------- price / hit
def test_stop_price_long_below_short_above():
    assert stop_price("BUY", 6000.25, 40) == 5990.25
    assert stop_price("SELL", 6000.25, 40) == 6010.25

def test_stop_price_snaps_to_tick_grid():
    assert stop_price("BUY", 6000.30, 4) == 5999.25

def test_stop_hit_touch_counts():
    assert stop_hit("BUY", 5990.25, 6001, 5990.25) and not stop_hit("BUY", 5990.25, 6001, 5990.5)
    assert stop_hit("SELL", 6010.25, 6010.25, 6000) and not stop_hit("SELL", 6010.25, 6010.0, 6000)

# ---------------------------------------------------------------- envelope / gates
def test_envelope_carries_stop_and_tp():
    e = make_envelope("Sim101", "BUY", 1, 24, "X", stop_ticks=40)
    assert e["risk"]["stop_loss_ticks"] == 40 and e["risk"]["take_profit_ticks"] == 24 and e["slot"] == 0

def test_executor_requires_stop_when_enabled(tmp_path):
    ex = Executor(ExecConfig(outbox_file=str(tmp_path / "o.jsonl")), NullLog(), require_stop=True)
    with pytest.raises(ValueError, match="stop_loss_ticks required"):
        ex.submit(make_envelope("Sim101", "BUY", 1, 24, "X", stop_ticks=None))
    ex.submit(make_envelope("Sim101", "BUY", 1, 24, "X", stop_ticks=40))
    ex.submit(make_envelope("Sim101", None, flatten=True))      # FLATTEN never needs a stop

@pytest.mark.parametrize("bad", [0, 2001, "40"])
def test_validate_rejects_bad_stop(bad):
    e = make_envelope("Sim101", "SELL", 1, 24, "X", stop_ticks=40); e["risk"]["stop_loss_ticks"] = bad
    with pytest.raises(ValueError): validate(e)

def test_sim_only_account_names():
    assert account_allowed("Sim101") and account_allowed("SimVQE06")
    assert not account_allowed("DEMO12345") and not account_allowed("APEX-1") and not account_allowed("Sim")
    assert account_allowed("DEMO12345", allow_non_sim_names=True)
    with pytest.raises(ValueError): make_envelope("DEMO12345", "BUY", 1, 24, "X", stop_ticks=40)

def test_live_gate_uses_configured_approver(tmp_path):
    ap = tmp_path / "a.json"
    ap.write_text(json.dumps({"approved_by": "Friend Name", "statement": "ok", "accounts": ["Sim101"]}))
    acc = AccountConfig("Sim101", True)
    cfg = ExecConfig(mode="LIVE", live_enabled=True, approval_file=str(ap), queue_dir=str(tmp_path / "q"))
    with pytest.raises(LiveRefused, match="approved_by"): live_gate(cfg, acc)
    assert live_gate(replace(cfg, approver="Friend Name"), acc)
    with pytest.raises(LiveRefused, match="nt8_queue"):
        live_gate(replace(cfg, approver="Friend Name", queue_dir=str(tmp_path / "data" / "nt8_queue" / "x")), acc)

# ---------------------------------------------------------------- DRY_RUN simulated exits
def _engine(tmp_path, **risk):
    cfg = Config(risk=RiskConfig(**risk), execution=ExecConfig(outbox_file=str(tmp_path / "o.jsonl")))
    acc = AccountConfig("Sim101", True, "X", StrategyParams())
    log = NullLog()
    eng = StreamEngine(cfg, [acc], log, Registry(["Sim101"]), Executor(cfg.execution, log, require_stop=True), live_from=0)
    return eng, eng.sims[0], log

T = 1_792_000_020 // 60 * 60    # some minute boundary (a Tuesday ~ midday CT is not required for exits)

def _open(eng, s, side, ts, tp_ticks=40):
    s.pending = (side, tp_ticks, ts - 60)

def test_dry_long_stop_exit(tmp_path):
    eng, s, log = _engine(tmp_path, session_flatten=False, slippage_ticks_per_side=0)
    _open(eng, s, "BUY", T); eng._sim_bar(s, Bar(T, 6000, 6001, 5999, 6000.5))
    assert s.pos["stop"] == 5990.0
    eng._sim_bar(s, Bar(T + 60, 6000, 6002, 5989.75, 5995))
    assert s.pos is None and s.trades[-1]["reason"] == "STOP" and s.trades[-1]["exit"] == 5990.0
    assert s.trades[-1]["net"] == -10 * 50 * 10 - 5.0   # 10 pts x $50 x 10 ES + $5 fee = -$5,005

def test_dry_short_stop_gap_fills_at_open(tmp_path):
    eng, s, log = _engine(tmp_path, session_flatten=False, slippage_ticks_per_side=0)
    _open(eng, s, "SELL", T); eng._sim_bar(s, Bar(T, 6000, 6001, 5999, 6000))
    eng._sim_bar(s, Bar(T + 60, 6015, 6016, 6014, 6015))   # gapped above 6010 stop
    assert s.trades[-1]["reason"] == "STOP" and s.trades[-1]["exit"] == 6015

def test_dry_same_bar_tie_is_scored_as_stop(tmp_path):
    eng, s, log = _engine(tmp_path, session_flatten=False, slippage_ticks_per_side=0)
    _open(eng, s, "BUY", T, tp_ticks=8); eng._sim_bar(s, Bar(T, 6000, 6000, 6000, 6000))
    eng._sim_bar(s, Bar(T + 60, 6000, 6003, 5989, 6000))   # TP 6002 and stop 5990 both touched
    assert s.trades[-1]["reason"] == "STOP"

def test_dry_tp_still_works_with_stop(tmp_path):
    eng, s, log = _engine(tmp_path, session_flatten=False, slippage_ticks_per_side=0)
    _open(eng, s, "BUY", T, tp_ticks=8); eng._sim_bar(s, Bar(T, 6000, 6000, 6000, 6000))
    eng._sim_bar(s, Bar(T + 60, 6000, 6002.25, 5995, 6002))
    assert s.trades[-1]["reason"] == "TP" and s.trades[-1]["exit"] == 6002

def test_dry_no_stop_when_disabled(tmp_path):
    eng, s, log = _engine(tmp_path, session_flatten=False, slippage_ticks_per_side=0, stop_enabled=False)
    _open(eng, s, "BUY", T); eng._sim_bar(s, Bar(T, 6000, 6000, 6000, 6000))
    eng._sim_bar(s, Bar(T + 60, 6000, 6001, 5900, 5950))
    assert s.pos is not None and s.pos["stop"] is None

def test_dry_session_flatten_1550_ct(tmp_path):
    eng, s, log = _engine(tmp_path, slippage_ticks_per_side=0)
    t = int(datetime(2026, 10, 20, 20, 50, tzinfo=timezone.utc).timestamp())   # 15:50 CDT
    _open(eng, s, "BUY", t - 60); eng._sim_bar(s, Bar(t - 60, 6000, 6000, 6000, 6000))
    eng._sim_bar(s, Bar(t, 6000, 6001, 5999, 6000.5))
    assert s.trades[-1]["reason"] == "SESSION_FLATTEN"


# ---------------------------------------------------------------- ES contract (Sean 2026-10-20: 10 ES default)
def test_default_is_10_es_and_risk_is_5000_at_40_ticks():
    from fitzsm.config import ES_TICK_VALUE, ORDER_SYMBOL
    r = RiskConfig()
    assert r.quantity == 10 and ORDER_SYMBOL == "ES"
    assert stop_ticks(r) * ES_TICK_VALUE * r.quantity == 5000.0

def test_envelope_is_es_and_qty_cap_10():
    e = make_envelope("Sim101", "BUY", 10, 24, "X", stop_ticks=40)
    assert e["symbol"] == "ES" and e["quantity"] == 10
    with pytest.raises(ValueError, match="quantity"): make_envelope("Sim101", "BUY", 11, 24, "X", stop_ticks=40)
    e["symbol"] = "MES"
    with pytest.raises(ValueError, match="symbol must be ES"): validate(e)

def test_config_rejects_qty_over_cap(tmp_path):
    from fitzsm.config import load
    p = tmp_path / "c.json"; p.write_text(json.dumps({"risk": {"quantity": 11}}))
    with pytest.raises(ValueError): load(p)
