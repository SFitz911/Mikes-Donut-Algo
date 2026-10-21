# Quick install

Short version. Full details are in [SETUP.md](SETUP.md).

1. **Clone the repo** (or download the ZIP from GitHub and unzip it):
   `git clone https://github.com/SFitz911/Mikes-Donut-Algo.git`
2. **Copy the indicators** into `Documents\NinjaTrader 8\bin\Custom\Indicators`:
   - `ninjascript\Indicators\FitzLabsChannel.cs`
   - `ninjascript\Indicators\FitzLabsTrades.cs`
   - `ninjascript\Indicators\FitzLabsBarWriter.cs` (the bar writer)
3. **Copy the bridge add-on** `ninjascript\AddOns\FitzLabsStandaloneBridge.cs` into `Documents\NinjaTrader 8\bin\Custom\AddOns`.
4. **Compile:** in NinjaTrader 8 open New > NinjaScript Editor and press F5. Fix any errors before going on (see AI_PROMPT.txt if stuck).
5. **Create sim accounts** in NinjaTrader (for example `SimVQE11` through `SimVQE20`). Names must start with `Sim`; only Simulator accounts are allowed.
6. **Connect NinjaTrader** to a live ES data feed, open a 1-minute ES chart (same contract month the add-on trades) and add `FitzLabsBarWriter`, then `FitzLabsChannel` and `FitzLabsTrades`.
7. **Set up Python** (3.11+): `python -m venv .venv`, `.venv\Scripts\activate`, `pip install -r requirements.txt`.
8. **Configure:** copy `config\fitzsm.example.json` to `config\fitzsm.json` and put in your sim account names.
9. **Check, then run:** `run_fitzsm.cmd check`, then `run_fitzsm.cmd` (dry run). Only after a clean dry run, follow SETUP.md to go live on sim.

Defaults: 10 ES per trade, 40-tick stop (about $5,000 risk per trade), flatten at 3:50 PM Central. Sim accounts only.

Note: FitzLabsTrades reads trade results from a folder path set for the original computer, so it shows no trades until that path is changed. FitzLabsChannel is visual only.
