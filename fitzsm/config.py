"""Single-machine runner configuration (one Windows PC, NT8 SIM). No Dell / Jarvis / Syncthing paths.
StrategyParams is copied verbatim from fitzlive/config.py so signals match the full runner.
Defaults chosen for a friend's PC (marked DEFAULT in docs/README): 10 ES (full-size, front month), fixed stop ON,
15:50 CT flatten ON. Signals AND orders are ES (Sean 2026-10-20 19:31 CT; was 1 MES)."""
import json
from dataclasses import dataclass, field, replace, fields
from pathlib import Path
from typing import List, Optional, Tuple

ROOT = Path(__file__).resolve().parents[1]          # single_machine/
TICK_SIZE = 0.25
ORDER_SYMBOL = "ES"                                 # traded root (full-size E-mini S&P 500, front month)
ES_TICK_VALUE = 12.50                               # $50/pt per ES contract
MAX_QUANTITY = 10                                   # hard cap per order (AddOn enforces the same)

@dataclass
class StrategyParams:
    timeframe_minutes: int = 1             # Sean 2026-10-19: 1-minute bars throughout (was 5)
    trigger: str = "momentum"
    trend_move_atr: float = 1.5
    er_min: float = 0.30
    chop_max: float = 55.0
    stop_atr_mult: float = 2.0
    target_rr: float = 2.0
    trigger_lookback: int = 30             # 1m bars (was 6 x 5m)
    tp_min_ticks: int = 8
    tp_max_ticks: int = 160
    # indicator settings (fitzbt / adaptive_trend defaults)
    atr_period: int = 70                   # 1m bars (was 14 x 5m)
    atr_long_period: int = 300             # 1m bars (was 60 x 5m)
    trend_lookback: int = 120              # 120 min on 1m (Sean 2026-10-19; was 30 x 5m = 150 min)
    ema_fast: int = 100                    # 1m bars (was 20 x 5m)
    ema_slow: int = 250                    # 1m bars (was 50 x 5m)
    r2_min: float = 0.35
    chop_period: int = 70                  # 1m bars (was 14 x 5m)
    er_period: int = 100                   # 1m bars (was 20 x 5m)
    range_lookback: int = 150              # 1m bars (was 30 x 5m)
    atr_range_max: float = 0.30
    atr_spike_max: float = 1.8
    use_vol_filter: bool = True
    # warm-up in bars of timeframe_minutes; 0 = auto (longest indicator). Sean 2026-10-19: 120 x 1m.
    # NOTE: atr_long(300), ema_slow(250), range_lookback(150) run on partial history until enough bars.
    warmup_bars: int = 120
    # Multi-setup + 5m-direction confirmation (Sean 2026-10-19). DEFAULT OFF = legacy behaviour unchanged.
    # Replay/backtest only: livedry refuses to start with it on. Definitions: fitzlive/multisetup.py.
    multi_setup: bool = False
    ms_channel_len: int = 30            # 1m bars per channel fit (sub-window inside the 120-bar lookback)
    ms_channel_move_atr: float = 1.5    # min |OLS move| in 1m ATRs for a directional channel
    ms_channel_r2: float = 0.5          # min R^2 of the channel fit
    ms_rearm_bars: int = 5              # NONE bars needed before a same-direction channel counts as a NEW setup
    ms_max_positions: Optional[int] = 2  # concurrent open+pending per account; None = unlimited; "free" = 1
    ms_htf_lookback: int = 24           # 5m bars (=120 min) for the 5m direction
    ms_htf_move_atr: float = 1.0        # min |OLS move| in 5m ATR(14)
    ms_htf_r2: float = 0.3
    ms_use_vol_filter: bool = False     # apply legacy chop/ER/range/spike filter to setups too
    # "default" = thresholds above. "free" (Sean 2026-10-19 18:06): 5m fit 12 bars, 1m channel 30, NO move/R2/
    # vol/EMA gates; direction = sign of OLS slope (0 = none); momentum trigger = bar direction only. See apply_ms_profile.
    ms_profile: str = "default"


@dataclass
class RiskConfig:
    quantity: int = 10                              # DEFAULT 10 ES (full setup: 20 MES)
    fee_round_trip_usd: float = 5.0
    slippage_ticks_per_side: float = 1.0
    entry_windows_ct: Tuple[Tuple[str, str], ...] = (("00:00", "15:45"), ("17:00", "23:59"))
    # Fixed protective stop, sent with every entry and placed by the AddOn as an OCO partner of the TP.
    stop_enabled: bool = True                       # DEFAULT ON (full setup: TP-only)
    stop_ticks: Optional[int] = 40                  # 40 ticks = 10.00 pts; used when stop_points is None
    stop_points: Optional[float] = None             # if set, overrides stop_ticks (points / 0.25 -> ticks)
    flatten_ct: str = "15:50"
    session_flatten: bool = True                    # DEFAULT ON (full setup: off)
    stale_data_seconds: int = 180
    ack_timeout_seconds: int = 30
    stuck_order_seconds: int = 120


@dataclass
class ExecConfig:
    mode: str = "DRY_RUN"                           # DRY_RUN | LIVE (LIVE = NT8 SIM orders via the AddOn queue)
    live_enabled: bool = False
    # AddOn queue root = %FITZLABS_ROOT%\data\nt8_queue_fitzlabs. The runner writes envelopes here and reads
    # ack/ results/ snapshots/ from here (same PC, no sync layer).
    queue_dir: str = str(ROOT / "data" / "nt8_queue_fitzlabs")
    approval_file: str = str(ROOT / "state" / "live_approval.json")
    approver: str = "Sean Fitzgerald"               # approval file must name exactly this approver
    outbox_file: str = str(ROOT / "state" / "dry_run_outbox.jsonl")
    not_before_ct: str = "2026-10-20T17:00"
    symbol: str = ORDER_SYMBOL
    # Sim-only name rule: account names must start with "Sim" (NT8 sim accounts: Sim101, SimXYZ...). The AddOn
    # also refuses any account whose provider is not Simulator. Set true only for a renamed sim account.
    allow_non_sim_account_names: bool = False


@dataclass
class FeedConfig:
    kind: str = "nt8_csv"
    path: str = str(ROOT / "data" / "es_1m_live.csv")   # written by FitzLabsBarWriter (NT8 indicator)
    instrument_root: str = "ES"                          # signals on ES bars; orders on ES. Never substitute MES.
    stale_after_s: int = 180


@dataclass
class AccountConfig:
    account: str
    enabled: bool = False
    strategy_id: str = ""
    params: StrategyParams = field(default_factory=StrategyParams)


@dataclass
class Config:
    accounts: List[AccountConfig] = field(default_factory=list)
    risk: RiskConfig = field(default_factory=RiskConfig)
    execution: ExecConfig = field(default_factory=ExecConfig)
    feed: FeedConfig = field(default_factory=FeedConfig)

    def account(self, name) -> Optional[AccountConfig]:
        return next((a for a in self.accounts if a.account == name), None)


def _pick(cls, d):
    names = {f.name for f in fields(cls)}
    unknown = sorted(set(d) - names)
    if unknown:
        raise ValueError(f"unknown {cls.__name__} keys: {unknown}")
    return d


def load(path) -> Config:
    """Unknown keys are an error (a typo must not silently fall back to a default)."""
    d = json.loads(Path(path).read_text(encoding="utf-8"))
    cfg = Config()
    d = {k: v for k, v in d.items() if not k.startswith("_")}
    if "risk" in d:
        r = dict(d["risk"])
        if "entry_windows_ct" in r: r["entry_windows_ct"] = tuple(tuple(x) for x in r["entry_windows_ct"])
        cfg.risk = replace(cfg.risk, **_pick(RiskConfig, r))
    if "execution" in d: cfg.execution = replace(cfg.execution, **_pick(ExecConfig, d["execution"]))
    if "feed" in d: cfg.feed = replace(cfg.feed, **_pick(FeedConfig, d["feed"]))
    for a in d.get("accounts", []):
        p = replace(StrategyParams(), **_pick(StrategyParams, a.get("params", {})))
        cfg.accounts.append(AccountConfig(a["account"], bool(a.get("enabled", False)), a.get("strategy_id", ""), p))
    unknown = sorted(set(d) - {"risk", "execution", "feed", "accounts"})
    if unknown: raise ValueError(f"unknown top-level keys: {unknown}")
    validate_config(cfg)
    return cfg


def validate_config(cfg: Config):
    from .stops import stop_ticks
    if not 1 <= int(cfg.risk.quantity) <= MAX_QUANTITY: raise ValueError(f"risk.quantity must be 1..{MAX_QUANTITY}")
    stop_ticks(cfg.risk)   # raises on a bad stop setting
    if any(a.params.multi_setup for a in cfg.accounts):
        raise ValueError("multi_setup is not supported by the single-machine runner")
    tfs = {a.params.timeframe_minutes for a in cfg.accounts}
    if len(tfs) > 1: raise ValueError("all accounts must use one timeframe")
    if len({a.account for a in cfg.accounts}) != len(cfg.accounts): raise ValueError("duplicate account")
    return True
