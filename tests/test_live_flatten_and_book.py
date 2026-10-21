import json
from datetime import datetime, timezone
from fitzsm.config import Config, RiskConfig, ExecConfig, AccountConfig, StrategyParams
from fitzsm.breakers import Registry
from fitzsm.livestate import LiveBook
from fitzsm.runner import StreamEngine
from fitzsm.execution import Executor

A = "Sim101"

class Log:
    def __init__(self): self.events = []
    def event(self, kind, **kw): self.events.append((kind, kw))

def iso(t): return datetime.fromtimestamp(t, timezone.utc).isoformat()

def setup(tmp_path, now, positions=(), orders=(), trade=None):
    q = tmp_path / "q"
    for d in ("ack", "results", "snapshots"): (q / d).mkdir(parents=True, exist_ok=True)
    (q / "snapshots" / "fitz_snapshot.json").write_text(json.dumps({"mode": "SIM_EXECUTION", "observed_at": iso(now),
        "accounts": {A: {"present": True, "connection": "Connected", "positions": list(positions), "orders": list(orders)}}}))
    if trade: (q / "results" / (trade["order_id"] + ".trade.json")).write_text(json.dumps(trade))
    ap = tmp_path / "ap.json"; ap.write_text(json.dumps({"approved_by": "Sean Fitzgerald", "statement": "ok", "accounts": [A]}))
    ex = ExecConfig(mode="LIVE", live_enabled=True, queue_dir=str(q), approval_file=str(ap), not_before_ct="2026-01-01T00:00")
    cfg = Config(risk=RiskConfig(), execution=ex)
    reg = Registry([A]); clock = lambda: now
    book = LiveBook(q, [A], reg, tmp_path / "j.jsonl", clock=clock, require_stop=True)
    log = Log()
    eng = StreamEngine(cfg, [AccountConfig(A, True, "X", StrategyParams())], log, reg, Executor(ex, log, require_stop=True), live_from=0, book=book)
    return eng, book, q, log

OID = "FitzLabs_Sim101-X-0001"
TRADE = {"order_id": OID, "account": A, "side": "BUY", "filled": 1, "avg_fill": 6000.0, "tp_price": 6010.0, "status": "OPEN"}
POS = [{"instrument": "ES 12-26", "quantity": 1, "direction": "Long"}]

def test_open_trade_without_stop_blocks_entries(tmp_path):
    now = 1_800_000_000
    eng, book, q, _ = setup(tmp_path, now, POS, [{"name": "FitzTP_Sim101-X-0001", "fitz": True, "state": "Working"}], TRADE)
    st = book.refresh()[A]
    assert any("FitzSL_" in b for b in st["blocks"])

def test_open_trade_with_tp_and_stop_has_no_stop_block(tmp_path):
    now = 1_800_000_000
    orders = [{"name": "FitzTP_Sim101-X-0001", "fitz": True, "state": "Working"}, {"name": "FitzSL_Sim101-X-0001", "fitz": True, "state": "Accepted"}]
    eng, book, q, _ = setup(tmp_path, now, POS, orders, TRADE)
    assert not any("FitzSL_" in b for b in book.refresh()[A]["blocks"])

def test_live_session_flatten_once_per_day(tmp_path):
    now = int(datetime(2026, 10, 20, 20, 51, tzinfo=timezone.utc).timestamp())    # 15:51 CDT
    orders = [{"name": "FitzTP_Sim101-X-0001", "fitz": True, "state": "Working"}, {"name": "FitzSL_Sim101-X-0001", "fitz": True, "state": "Accepted"}]
    eng, book, q, log = setup(tmp_path, now, POS, orders, TRADE)
    eng.tick(now); eng.tick(now + 5)
    files = [p for p in q.glob("*.json")]
    assert len(files) == 1
    env = json.loads(files[0].read_text())
    assert env["side"] == "FLATTEN" and env["account"] == A and env["flatten_scope"] == "ACCOUNT"

def test_no_live_flatten_before_time_or_when_flat(tmp_path):
    now = int(datetime(2026, 10, 20, 20, 40, tzinfo=timezone.utc).timestamp())    # 15:40 CDT
    eng, book, q, log = setup(tmp_path, now, POS, [], TRADE)
    eng.tick(now); assert not list(q.glob("*.json"))
    now2 = int(datetime(2026, 10, 20, 20, 52, tzinfo=timezone.utc).timestamp())
    eng2, book2, q2, _ = setup(tmp_path / "flat", now2)
    eng2.tick(now2); assert not list(q2.glob("*.json"))

def test_mes_position_is_a_hard_mismatch_for_the_es_lane(tmp_path):
    now = 1_800_000_000
    mes = [{"instrument": "MES 12-26", "quantity": 1, "direction": "Long"}]
    eng, book, q, _ = setup(tmp_path, now, mes, [], None)
    st = book.refresh()[A]
    assert any("non-ES position" in b for b in st["blocks"])
