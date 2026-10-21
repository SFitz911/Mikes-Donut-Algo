// FitzLabsTrades - READ-ONLY overlay of Fitz Labs entries/exits.
// Source: FitzLabsBridge result files under <QueueRoot>\results\ (local on fitzwin11):
//   <orderName>_<execId>.fill.<ts>.json  {name, execution_id, account, instrument, price, quantity, market_position, time(UTC)}
//   <order_id>.trade.json                {order_id, account, side, tp_ticks, filled, avg_fill, tp_price, status, updated_at}
// Entry order = "FitzLabs_<id>", TP = "FitzTP_<id>", close = "FitzCL_<flatten envelope id>" (account-wide, paired to the open entry).
// Never writes files, never touches orders/accounts.
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
	public class FitzLabsTrades : Indicator
	{
		private class FlFill { public string Name, Account, Id, Mp; public double Price; public int Qty; public DateTime TimeUtc; }
		private class FlLeg { public string Name, Account, Mp; public double Avg; public int Qty; public DateTime FirstUtc, LastUtc; }
		private class FlTrade { public string OrderId, Account, Side, Status; public double AvgFill, TpPrice; public int Filled; }

		private DispatcherTimer timer;
		private readonly HashSet<string> myTags = new HashSet<string>();
		private string lastSignature = "";

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Read-only Fitz Labs entry/exit markers from FitzLabsBridge result files.";
				Name = "FitzLabsTrades";
				Calculate = Calculate.OnBarClose;
				IsOverlay = true;
				DisplayInDataBox = false;
				DrawOnPricePanel = true;
				PaintPriceMarkers = false;
				IsSuspendedWhileInactive = false;
				QueueRoot = @"E:\DATA_1TB\dev\fitz-labs-trading\data\nt8_queue_fitzlabs";
				AccountFilter = "ALL";
				RefreshSeconds = 30;
				PointValue = 5.0;
				FeePerContractRT = 0.0;
				ShowLabels = true;
				BuyBrush = Brushes.LimeGreen;
				SellBrush = Brushes.OrangeRed;
				ExitBrush = Brushes.Gold;
				LabelBrush = Brushes.White;
			}
			else if (State == State.Historical)
			{
				if (ChartControl != null)
				{
					ChartControl.Dispatcher.InvokeAsync(() =>
					{
						timer = new DispatcherTimer(DispatcherPriority.Background, ChartControl.Dispatcher);
						timer.Interval = TimeSpan.FromSeconds(Math.Max(30, Math.Min(60, RefreshSeconds)));
						timer.Tick += OnTimerTick;
						timer.Start();
					});
				}
			}
			else if (State == State.Terminated)
			{
				if (timer != null)
				{
					DispatcherTimer t = timer; timer = null;
					try { if (ChartControl != null) ChartControl.Dispatcher.InvokeAsync(() => { t.Stop(); t.Tick -= OnTimerTick; }); else t.Stop(); } catch { }
				}
			}
		}

		protected override void OnBarUpdate()
		{
			// Draw once when realtime begins (bars exist so marker times can be anchored).
			if (lastSignature == "" && CurrentBar > 0 && (State == State.Realtime || CurrentBar >= Count - 2))
				Redraw(false);
		}

		private void OnTimerTick(object sender, EventArgs e)
		{
			try { TriggerCustomEvent(o => Redraw(false), null); } catch { }
		}

		// ---------- parsing (manual regex, no extra assemblies) ----------
		private static string JStr(string j, string key)
		{
			Match m = Regex.Match(j, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
			return m.Success ? m.Groups[1].Value : null;
		}
		private static double JNum(string j, string key, double dflt)
		{
			Match m = Regex.Match(j, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?[0-9.eE+\\-]+)");
			double v;
			if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
			return dflt;
		}
		private static string SafeRead(string path)
		{
			try
			{
				using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				using (StreamReader sr = new StreamReader(fs)) return sr.ReadToEnd();
			}
			catch { return null; }
		}
		private bool AccountOk(string acct)
		{
			if (string.IsNullOrEmpty(acct)) return false;
			if (!Regex.IsMatch(acct, "^SimVQE(0[6-9]|10)$")) return false;
			if (string.IsNullOrWhiteSpace(AccountFilter) || AccountFilter.Trim().ToUpperInvariant() == "ALL") return true;
			foreach (string a in AccountFilter.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
				if (string.Equals(a.Trim(), acct, StringComparison.OrdinalIgnoreCase)) return true;
			return false;
		}
		private DateTime ToChartTime(DateTime utc)
		{
			try { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Core.Globals.GeneralOptions.TimeZoneInfo); }
			catch { return utc.ToLocalTime(); }
		}

		// ---------- main ----------
		private void Redraw(bool force)
		{
			try
			{
				if (CurrentBar < 1) return;
				string dir = Path.Combine(QueueRoot ?? "", "results");
				if (!Directory.Exists(dir)) return;

				string[] fillFiles = Directory.GetFiles(dir, "*.fill.*.json");
				string[] tradeFiles = Directory.GetFiles(dir, "*.trade.json");
				string sig = fillFiles.Length + "|" + tradeFiles.Length + "|" + Directory.GetLastWriteTimeUtc(dir).Ticks + "|" + CurrentBar / 60;
				if (!force && sig == lastSignature) return;
				lastSignature = sig;

				// fills, de-duplicated by execution_id
				Dictionary<string, FlFill> fills = new Dictionary<string, FlFill>();
				foreach (string f in fillFiles)
				{
					string j = SafeRead(f); if (j == null) continue;
					string name = JStr(j, "name"), acct = JStr(j, "account"), t = JStr(j, "time");
					if (name == null || !AccountOk(acct)) continue;
					double price = JNum(j, "price", double.NaN); int qty = (int)JNum(j, "quantity", 0);
					DateTime ts;
					if (double.IsNaN(price) || qty <= 0 || t == null || !DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out ts)) continue;
					string id = JStr(j, "execution_id"); if (string.IsNullOrEmpty(id)) id = Path.GetFileName(f);
					if (!fills.ContainsKey(id)) fills[id] = new FlFill { Name = name, Account = acct, Id = id, Price = price, Qty = qty, TimeUtc = ts, Mp = JStr(j, "market_position") ?? "" };
				}

				// aggregate per order name
				Dictionary<string, FlLeg> legs = new Dictionary<string, FlLeg>();
				foreach (FlFill x in fills.Values.OrderBy(v => v.TimeUtc))
				{
					FlLeg l;
					if (!legs.TryGetValue(x.Name, out l)) { l = new FlLeg { Name = x.Name, Account = x.Account, FirstUtc = x.TimeUtc, Mp = x.Mp }; legs[x.Name] = l; }
					l.Avg = (l.Avg * l.Qty + x.Price * x.Qty) / (l.Qty + x.Qty);
					l.Qty += x.Qty; l.LastUtc = x.TimeUtc;
				}

				// trade records (side, tp, status, fallback entry price)
				Dictionary<string, FlTrade> trades = new Dictionary<string, FlTrade>();
				foreach (string f in tradeFiles)
				{
					string j = SafeRead(f); if (j == null) continue;
					string oid = JStr(j, "order_id"), acct = JStr(j, "account");
					if (oid == null || !AccountOk(acct)) continue;
					trades[oid] = new FlTrade { OrderId = oid, Account = acct, Side = (JStr(j, "side") ?? "").ToUpperInvariant(), Status = JStr(j, "status") ?? "",
						AvgFill = JNum(j, "avg_fill", 0), TpPrice = JNum(j, "tp_price", 0), Filled = (int)JNum(j, "filled", 0) };
				}

				ClearMine();
				SimpleFont font = new SimpleFont("Arial", 10);
				double off = 4 * TickSize;
				HashSet<string> usedExits = new HashSet<string>();

				foreach (FlLeg entry in legs.Values.Where(v => v.Name.StartsWith("FitzLabs_")).OrderBy(v => v.FirstUtc))
				{
					FlTrade tr; trades.TryGetValue(entry.Name, out tr);
					bool isBuy = tr != null && tr.Side.Length > 0 ? tr.Side == "BUY" : entry.Mp == "Long";
					DateTime et = ToChartTime(entry.FirstUtc);
					if (et < Time[CurrentBar]) continue; // before first chart bar
					string acctShort = entry.Account.Replace("SimVQE", ""); int acctIdx; int.TryParse(acctShort, out acctIdx); double stk = Math.Max(0, acctIdx - 6) * 5 * off; // stack labels per account
					string tag = "FLT_E_" + entry.Name;
					if (isBuy) Track(Draw.ArrowUp(this, tag, false, et, entry.Avg - off - stk, BuyBrush));
					else Track(Draw.ArrowDown(this, tag, false, et, entry.Avg + off + stk, SellBrush));
					if (ShowLabels)
						Track(Draw.Text(this, tag + "_t", false, "Acct " + acctShort + (isBuy ? " BUY LONG " : " SELL SHORT ") + entry.Qty + " @ " + entry.Avg.ToString("0.00", CultureInfo.InvariantCulture),
							et, isBuy ? entry.Avg - 3 * off - stk : entry.Avg + 3 * off + stk, 0, LabelBrush, font, System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0));

					// exits: TP for this entry, or any close order referencing it
					List<FlLeg> exits = legs.Values.Where(v => v.Name == "FitzTP_" + entry.Name.Substring("FitzLabs_".Length)).ToList();
					// fallback: account-wide close after entry, not yet used
					if (exits.Count == 0)
						exits = legs.Values.Where(v => v.Name.StartsWith("FitzCL_") && v.Account == entry.Account && v.FirstUtc >= entry.FirstUtc && !usedExits.Contains(v.Name)).Take(1).ToList();

					foreach (FlLeg x in exits)
					{
						usedExits.Add(x.Name);
						double dirSign = isBuy ? 1.0 : -1.0;
						int q = Math.Min(x.Qty, entry.Qty);
						double pnl = (x.Avg - entry.Avg) * dirSign * q * PointValue - FeePerContractRT * q;
						DateTime xt = ToChartTime(x.LastUtc);
						string xtag = "FLT_X_" + x.Name;
						Track(Draw.Diamond(this, xtag, false, xt, isBuy ? x.Avg + off + stk : x.Avg - off - stk, ExitBrush));
						if (ShowLabels)
							Track(Draw.Text(this, xtag + "_t", false, "Acct " + acctShort + (isBuy ? " CLOSE LONG " : " CLOSE SHORT ") + (x.Name.StartsWith("FitzTP_") ? "at TP " : "flatten ") + q + " @ " + x.Avg.ToString("0.00", CultureInfo.InvariantCulture)
								+ "  " + (pnl >= 0 ? "+$" : "-$") + Math.Abs(pnl).ToString("N0", CultureInfo.InvariantCulture),
								xt, isBuy ? x.Avg + 3 * off + stk : x.Avg - 3 * off - stk, 0, pnl >= 0 ? Brushes.LimeGreen : Brushes.OrangeRed, font,
								System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0));
					}
					if (exits.Count == 0 && ShowLabels && tr != null && tr.TpPrice > 0 && tr.Status == "OPEN")
						Track(Draw.Text(this, tag + "_open", false, "Acct " + acctShort + (isBuy ? " LONG " : " SHORT ") + entry.Qty + " open, target " + tr.TpPrice.ToString("0.00", CultureInfo.InvariantCulture),
							et, isBuy ? entry.Avg - 4.3 * off - stk : entry.Avg + 4.3 * off + stk, 0, ExitBrush, font, System.Windows.TextAlignment.Center, Brushes.Transparent, Brushes.Transparent, 0));
				}
				ForceRefresh();
			}
			catch (Exception ex)
			{
				Print("FitzLabsTrades: refresh error: " + ex.Message);
			}
		}

		private void Track(DrawingTool d) { if (d != null) myTags.Add(d.Tag); }
		private void ClearMine()
		{
			foreach (string t in myTags.ToList()) { try { RemoveDrawObject(t); } catch { } }
			myTags.Clear();
		}

		#region Properties
		[NinjaScriptProperty, Display(Name = "Queue root", Order = 1, GroupName = "Fitz Labs")]
		public string QueueRoot { get; set; }
		[NinjaScriptProperty, Display(Name = "Accounts (ALL or e.g. SimVQE06,SimVQE08)", Order = 2, GroupName = "Fitz Labs")]
		public string AccountFilter { get; set; }
		[NinjaScriptProperty, Range(30, 60), Display(Name = "Refresh seconds (30-60)", Order = 3, GroupName = "Fitz Labs")]
		public int RefreshSeconds { get; set; }
		[NinjaScriptProperty, Range(0.01, double.MaxValue), Display(Name = "$ per point (MES = 5)", Order = 4, GroupName = "Fitz Labs")]
		public double PointValue { get; set; }
		[NinjaScriptProperty, Range(0, double.MaxValue), Display(Name = "Fee per contract round trip $", Order = 5, GroupName = "Fitz Labs")]
		public double FeePerContractRT { get; set; }
		[NinjaScriptProperty, Display(Name = "Show labels", Order = 6, GroupName = "Fitz Labs")]
		public bool ShowLabels { get; set; }

		[XmlIgnore, Display(Name = "Buy entry", Order = 10, GroupName = "Colors")]
		public Brush BuyBrush { get; set; }
		[Browsable(false)] public string BuyBrushSerialize { get { return Serialize.BrushToString(BuyBrush); } set { BuyBrush = Serialize.StringToBrush(value); } }
		[XmlIgnore, Display(Name = "Sell entry", Order = 11, GroupName = "Colors")]
		public Brush SellBrush { get; set; }
		[Browsable(false)] public string SellBrushSerialize { get { return Serialize.BrushToString(SellBrush); } set { SellBrush = Serialize.StringToBrush(value); } }
		[XmlIgnore, Display(Name = "Exit / TP", Order = 12, GroupName = "Colors")]
		public Brush ExitBrush { get; set; }
		[Browsable(false)] public string ExitBrushSerialize { get { return Serialize.BrushToString(ExitBrush); } set { ExitBrush = Serialize.StringToBrush(value); } }
		[XmlIgnore, Display(Name = "Entry label", Order = 13, GroupName = "Colors")]
		public Brush LabelBrush { get; set; }
		[Browsable(false)] public string LabelBrushSerialize { get { return Serialize.BrushToString(LabelBrush); } set { LabelBrush = Serialize.StringToBrush(value); } }
		#endregion
	}
}
