# Fitz Labs single-machine runner (one Windows PC, NinjaTrader 8, SIM account)

Built from `docs/SINGLE_MACHINE_RUNNER_SPEC.md`. It is self-contained: the `fitzsm` and `smbt` packages are trimmed
**copies** of `fitzlive` and `fitzbt`, and the live runner (`fitzlive/`) is not modified or imported. No Dell, Tailscale,
Syncthing, Jarvis, Command Center, observer, chat bridge, Telegram or systemd is involved.

**Status:** the Python side is unit-tested (see "Tests"). The two `.cs` files are **NOT compiled in NinjaTrader**.
They were only type-checked on Linux against hand-written NT8 stand-ins. Your first F5 is the real compile test.
SIM only.

## How it fits together
```
NT8 ES 1-minute chart + FitzLabsBarWriter ──► C:\FitzLabs\data\es_1m_live.csv  (closed bars, bar END in UTC)
                                                    │
                     python -m fitzsm run  ◄────────┘   adaptive-trend strategy (same as SimVQE06-10)
                                │  entry envelope: ES MARKET, qty, TP ticks, stop ticks
                                ▼
          C:\FitzLabs\data\nt8_queue_fitzlabs\*.json ──► FitzLabsStandaloneBridge (NT8 AddOn)
                                                         market entry → after fill: TP limit + stop-market (OCO)
          ack\ results\ snapshots\  ◄────────────────────  (runner reconciles from these; fails closed)
```
Signals come from **ES** 1-minute bars and orders go to **ES** (full-size E-mini, front month). MES is never used for
bars or orders: the AddOn rejects non-ES symbols and treats any non-ES position as a hard mismatch.

## Defaults (pending Sean's sign-off; all of them are in `config\fitzsm.json`)
| Setting | Default here | Full SimVQE06-10 setup |
|---|---|---|
| quantity | **10 ES** (full-size; $12.50/tick, $50/pt per contract; hard cap 10) | 20 MES |
| fixed stop | **ON, 40 ticks = 10.00 pts** from the entry fill (`stop_ticks` or `stop_points`) = **~$5,000 risk per trade at 10 ES** (10 pts x $50 x 10), before slippage and fees | none (TP-only) |
| session flatten | **ON, 15:50 CT** (one FLATTEN per account per day) | off |
| entry windows | 00:00-15:45 and 17:00-23:59 CT | same |
| approver | `Sean Fitzgerald` (change `execution.approver` to use a different name) | Sean only |
The 40-tick stop distance is a placeholder that hasn't been backtested. Sean should pick the real value.
At 10 ES, every point is $500. A typical TP of 8-160 ticks is worth $1,000-$20,000 per trade. Make sure the sim account
balance and NT8 margin settings allow 10 ES.

## From unzip to first SIM trade (clean Windows PC with NT8 + a Sim account)
1. **NinjaTrader 8**: install and log in. Connect a data feed that provides **real-time CME ES** (a sim
   account alone doesn't). Use a Sim account such as `Sim101` and make sure it's flat with no working orders.
   NT8 time zone (Tools > Options > General) can be anything; the bar writer converts to UTC.
2. **Folder**: unzip so this README is at `C:\FitzLabs\SETUP.md`. Then run `setx FITZLABS_ROOT C:\FitzLabs` in
   cmd. Close and restart NinjaTrader so the AddOn and the indicator see the variable.
3. **Python 3.12+** (64-bit, python.org, tick "Add to PATH"). In cmd:
   ```
   cd /d C:\FitzLabs
   py -3 -m venv .venv
   .venv\Scripts\pip install -r requirements.txt
   .venv\Scripts\python -m pytest -q tests
   ```
   All tests should pass.
4. **NinjaScript files** (only with the Sim account flat):
   - `ninjascript\AddOns\FitzLabsStandaloneBridge.cs` → `Documents\NinjaTrader 8\bin\Custom\AddOns\`
   - `ninjascript\Indicators\FitzLabsBarWriter.cs` → `Documents\NinjaTrader 8\bin\Custom\Indicators\`
   - Optional chart display: `FitzLabsChannel.cs` and `FitzLabsTrades.cs` from FitzLabs-NT8-Indicators.zip go into
     the same `Indicators` folder. FitzLabsTrades's folder setting should point to `C:\FitzLabs\data\nt8_queue_fitzlabs`.

   NT8 compiles automatically when files appear (or open NinjaScript Editor and press F5). Check the NinjaScript
   Output window (New > NinjaScript Output) for `[FITZSA] v fitzlabs-sa-20261020.1 queue=... held=True`.
   If the compile fails, NT8 shows the file and line. Remove the two files to roll back.
5. **Bar feed**: open a chart **ES (front month), 1 Minute**, with at least 2 days loaded, and add the indicator
   **FitzLabsBarWriter**. The Output window says `writing ES ... to C:\FitzLabs\data\es_1m_live.csv`.
   Check that the file grows by one row per minute while the market is open. The AddOn needs live ES prices: it refuses entries when ES Last is older than 15 s.
   The AddOn trades the front quarterly ES contract (Mar/Jun/Sep/Dec). During an expiry month it already picks the
   next contract, so use the same contract on the ES chart.
6. **Config**: `copy config\fitzsm.example.json config\fitzsm.json` and edit:
   - account name (must start with `Sim`; live accounts are always refused because the AddOn requires the Simulator provider)
   - `not_before_ct` (no entries before this CT time)
   - quantity, stop and flatten settings if needed

   Optional: list allowed accounts one per line in `C:\FitzLabs\data\nt8_queue_fitzlabs\fitzlabs_accounts.txt`.
   Then run `.venv\Scripts\python -m fitzsm --config config\fitzsm.json check`.
7. **Dry run first** (no orders): `run_fitzsm.cmd`. Leave it running for a while, then check
   `runs\current\decisions.jsonl` and `runs\current\summary.json`. The first ~120 bars are indicator warm-up.
8. **Approve and release** (this is what allows orders):
   - `copy config\live_approval.example.json state\live_approval.json` (create `state\` first). Set `accounts` and the
     approver/statement. `approved_by` must equal `execution.approver`.
   - Create `C:\FitzLabs\data\nt8_queue_fitzlabs\fitzlabs_execution_release.txt` containing exactly
     `fitzlabs-sa-20261020.1`. Without it the AddOn stays HELD and places nothing.
9. **Live SIM**: `run_fitzsm.cmd live`. On a signal:
   - the runner writes `FitzLabs_<account>-<time>-<n>.json`
   - the AddOn acks it, buys or sells ES at market, and places `FitzTP_...` and `FitzSL_...` as one OCO pair
   - the trade shows in NT8 Orders and Positions

   Ctrl+C stops the runner. Open positions keep their TP and stop in NT8.
10. **Manual flatten** at any time: `.venv\Scripts\python -m fitzsm --config config\fitzsm.json flatten Sim101 --live`
    (or use NT8's own Flatten button).
11. **Optional autostart**: Task Scheduler > Create Task > "At log on" > Action `C:\FitzLabs\run_fitzsm.cmd`, argument
    `live`, "Start in" `C:\FitzLabs`, and "If the task fails, restart every 1 minute". Turn off PC sleep. NT8 must be
    running with the ES chart open.

## Safety behaviour (what blocks a new entry)
- Breakers (`runs\current\breakers_status.json`; all auto-clear):
  - `stale_data`: newest ES bar more than 180 s old
  - `bridge_unreachable`: AddOn snapshot missing or older than 30 s
  - `position_mismatch`
  - `ack_timeout`
  - `stuck_order`
  - `session_window`
- Any unconfirmed order, an open position, or a missing `FitzTP_` or `FitzSL_` on an open trade also blocks entries.
- The AddOn refuses:
  - non-Simulator accounts and non-`Sim*` names
  - foreign working orders and non-ES positions
  - quantities above 10
  - envelopes older than 30 s
  - anything while HELD
- If NT8 rejects the stop, or price is already through it at fill time, the AddOn market-closes the position.
- At 15:50 CT, open positions are flattened once per day. No new entries 15:45-17:00 CT.

## Not included vs the full Dell setup
- No AI or agent monitoring
- No Telegram alerts
- No feed watchdog auto-reconnect (a dropped feed only blocks entries)
- No multi-account parameter sets beyond what you configure
- No Jarvis cross-check
- No Dell recorder or backups. The bar CSV and `runs\` are the only records, so back them up yourself.
- `multi_setup` isn't supported.

## Tests (Linux or Windows)
`python -m pytest -q tests` from this folder. The tests cover:
- the stop distance, price and hit logic
- DRY_RUN stop, TP and flatten exits (stop wins a same-bar tie; a gap through the stop fills at the open)
- the envelope and approver gates and the sim-only account rule
- the CSV feed (partial lines, gaps kept, MES rows refused, duplicates)
- LiveBook stop and TP checks, live 15:50 flatten once per day
- a DRY_RUN CLI smoke run
- a copy of the original LiveBook test suite

`ninjascript/typecheck/typecheck.sh` runs the Linux type check of the `.cs` files (stubs only).
