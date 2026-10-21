# Mikes-Donut-Algo

A minimal single-machine ES trading setup for NinjaTrader 8 **sim accounts**, built for a friend who owns a donut shop.

Everything runs on one Windows PC: a NinjaTrader indicator writes 1-minute ES bars to a local file, a small Python runner reads them and decides trades, and a NinjaTrader add-on places the orders on a Sim account.

**Defaults**
- 10 ES (full-size) per trade
- Fixed 40-tick stop (10 points, about $5,000 risk per trade at 10 ES)
- Flatten all positions at 3:50 PM Central

Sim accounts only. No API keys, passwords, or credentials are included.

Quick steps are in [INSTALL.md](INSTALL.md); full details, from unzip to first sim trade, are in [SETUP.md](SETUP.md). If you get stuck, see AI_PROMPT.txt.
