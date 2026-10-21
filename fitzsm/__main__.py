"""python -m fitzsm {run,check,status,flatten} --config config\\fitzsm.json"""
import argparse, json, sys
from dataclasses import replace
from pathlib import Path
from . import config as C
from .execution import Executor, LiveRefused, live_gate, make_envelope
from .logs import JsonlLogger
from .stops import stop_ticks


def cmd_check(a):
    cfg = C.load(a.config)
    print(f"config OK: mode={cfg.execution.mode} live_enabled={cfg.execution.live_enabled} qty={cfg.risk.quantity} ES "
          f"stop_ticks={stop_ticks(cfg.risk)} session_flatten={cfg.risk.session_flatten}@{cfg.risk.flatten_ct} CT")
    print(f"feed: {cfg.feed.path} exists={Path(cfg.feed.path).exists()}")
    print(f"queue: {cfg.execution.queue_dir} exists={Path(cfg.execution.queue_dir).exists()}")
    rel = Path(cfg.execution.queue_dir) / "fitzlabs_execution_release.txt"
    print(f"AddOn release file present: {rel.exists()} (AddOn stays HELD without it)")
    for x in cfg.accounts:
        try:
            live_gate(replace(cfg.execution, mode="LIVE", live_enabled=True), x); g = "LIVE gate OK"
        except LiveRefused as e:
            g = f"LIVE gate refuses: {e}"
        print(f"  {x.account}: {'ENABLED' if x.enabled else 'disabled'} trigger={x.params.trigger} -> {g}")


def cmd_status(a):
    p = Path(a.out) / "breakers_status.json"
    if not p.exists(): sys.exit(f"no status yet: {p}")
    d = json.loads(p.read_text()); print(f"breakers (written {d['written_at']}):")
    bad = [b for b in d["breakers"] if b["state"] != "CLEAR"]
    for b in (d["breakers"] if a.all else bad):
        print(f"  [{b['state']}] {b['scope']}/{b['name']} reason={b['reason']!r} clear_when={b['clear_condition']}")
    if not bad: print("  all CLEAR")
    s = Path(a.out) / "summary.json"
    if s.exists(): print(s.read_text())


def cmd_flatten(a):
    cfg = C.load(a.config); acc = cfg.account(a.account)
    if not acc: sys.exit(f"unknown account {a.account}")
    log = JsonlLogger(Path(a.out) / "manual.jsonl")
    env = make_envelope(a.account, None, flatten=True, strategy_id=acc.strategy_id, allow_non_sim_names=cfg.execution.allow_non_sim_account_names)
    ex_cfg = replace(cfg.execution, mode="LIVE" if a.live else "DRY_RUN", outbox_file=str(Path(a.out) / "manual_outbox.jsonl"))
    try:
        oid = Executor(ex_cfg, log).submit(env, acc)
        print(f"FLATTEN {a.account} {'written to AddOn queue' if a.live else 'DRY_RUN (outbox only)'}: {oid}")
    except LiveRefused as e:
        sys.exit(f"LIVE flatten refused: {e}")


def main(argv=None):
    p = argparse.ArgumentParser(prog="fitzsm")
    p.add_argument("--config", default=str(C.ROOT / "config" / "fitzsm.json"))
    sp = p.add_subparsers(dest="cmd", required=True)
    r = sp.add_parser("run")
    r.add_argument("--live", action="store_true", help="LIVE NT8 SIM orders via the AddOn queue (all gates must pass)")
    r.add_argument("--out", default=str(C.ROOT / "runs" / "current"))
    r.add_argument("--warmup-from", default=None, help="CT 'YYYY-MM-DDTHH:MM' (default: 24h before live-from)")
    r.add_argument("--live-from", default=None, help="CT time; decisions/orders only for bars ending after it (default: now)")
    r.add_argument("--until", default=None); r.add_argument("--poll", type=float, default=1.0)
    r.add_argument("--once", action="store_true", help="one poll cycle then exit (smoke test)")
    sp.add_parser("check")
    s = sp.add_parser("status"); s.add_argument("--out", default=str(C.ROOT / "runs" / "current")); s.add_argument("--all", action="store_true")
    f = sp.add_parser("flatten"); f.add_argument("account"); f.add_argument("--live", action="store_true"); f.add_argument("--out", default=str(C.ROOT / "runs" / "current"))
    a = p.parse_args(argv)
    if a.cmd == "run":
        from .runner import run
        return run(a, C.load(a.config))
    {"check": cmd_check, "status": cmd_status, "flatten": cmd_flatten}[a.cmd](a)


if __name__ == "__main__":
    main()
