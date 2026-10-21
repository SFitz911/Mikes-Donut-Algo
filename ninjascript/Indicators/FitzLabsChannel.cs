// FitzLabsChannel - display mirror of the Fitz Labs LIVE multisetup profile "free" (fitz-labs-trading main @813c98f,
// fitzlive/multisetup.py + config MS_PROFILES). READ-ONLY indicator: no orders, no add-on calls, no file I/O.
// Use on an ES 1-minute chart, Calculate = OnBarClose (runner decides on closed 1m bars; bar time = bar END).
//
// 1m channel: sign of the OLS slope of the last 30 1m closes (no move/R^2 thresholds). As in the runner it reads
//   NONE until 30 closes exist AND the 1m Wilder ATR(70) is seeded (70 bars); ATR is used only for the move number.
// 5m direction: 5m bars built from these 1m bars, bucket = ceil(bar end, 5 min); a bucket closes on its :x0/:x5 bar
//   or when a later bucket starts (missing minutes -> built from what exists). Sign of OLS slope over last 12 5m
//   closes; exactly 0 = NONE. Updated only once 12 5m closes exist AND the 5m Wilder ATR(14) is seeded (14 5m bars),
//   otherwise it keeps its previous value (initially NONE) - same as FiveMinTrend.
// Entry needs both to agree (plus a 1m bar closing in that direction, not drawn). Background = both agree.
// Bands around the 30-bar line = +/- max |residual| over the 30 bars (display choice; the runner has no bands).
// Seeding: runner warm-up starts at its --warmup-from (today 12:00 CT; first bar end 12:10). Set SeedFrom to match
//   the ATR-based move numbers / 5m bucket history exactly. Slope SIGNS depend only on closes.
// Legacy (pre-multisetup) regime plots EMA100/EMA250/LR120/NeedUp/NeedDn: ShowLegacy (default off).
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
	public class FitzLabsChannel : Indicator
	{
		private List<double> c;
		private double? atr, prevClose, emaF, emaS; private List<double> atrSeed;
		private int count;
		private DateTime fKey = DateTime.MinValue; private double[] fCur; private double? fPrev, fAtr;
		private List<double> fSeed, fCl; private string fDir = "NONE"; private double fMove, fSlope;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Fitz Labs live multisetup 'free' profile: 30-bar 1m slope + 12-bar 5m slope (display only)";
				Name = "FitzLabsChannel"; Calculate = Calculate.OnBarClose; IsOverlay = true;
				DisplayInDataBox = true; DrawOnPricePanel = true; PaintPriceMarkers = false; IsSuspendedWhileInactive = false;
				ChLen = 30; HtfLookback = 12; AtrPeriod = 70; HtfAtrPeriod = 14; TrendLookback = 120; EmaFast = 100; EmaSlow = 250; TrendMoveAtr = 1.5;
				SeedFrom = new DateTime(2000, 1, 1); ShowBackground = true; ShowInfo = true; ShowLegacy = false;
				AddPlot(new Stroke(Brushes.Gold, 2), PlotStyle.Line, "Ch30Mid");
				AddPlot(new Stroke(Brushes.Gold, DashStyleHelper.Dot, 1), PlotStyle.Line, "Ch30Upper");
				AddPlot(new Stroke(Brushes.Gold, DashStyleHelper.Dot, 1), PlotStyle.Line, "Ch30Lower");
				AddPlot(new Stroke(Brushes.Gray, 3), PlotStyle.Dot, "Dir5m");
				AddPlot(Brushes.DodgerBlue, "EMA100"); AddPlot(Brushes.Orange, "EMA250"); AddPlot(Brushes.White, "LR120");
				AddPlot(new Stroke(Brushes.LimeGreen, DashStyleHelper.Dash, 1), PlotStyle.Line, "NeedUp");
				AddPlot(new Stroke(Brushes.Red, DashStyleHelper.Dash, 1), PlotStyle.Line, "NeedDn");
			}
			else if (State == State.DataLoaded) Reset();
		}

		private void Reset()
		{
			c = new List<double>(); atr = prevClose = emaF = emaS = null; atrSeed = new List<double>(); count = 0;
			fKey = DateTime.MinValue; fCur = null; fPrev = fAtr = null; fSeed = new List<double>(); fCl = new List<double>();
			fDir = "NONE"; fMove = 0; fSlope = 0;
		}

		private static double? Wilder(double? v, List<double> seed, int n, double x)
		{
			if (v == null) { seed.Add(x); if (seed.Count == n) { double s = 0; foreach (var q in seed) s += q; return s / n; } return null; }
			return (v.Value * (n - 1) + x) / n;
		}
		private static double TrueRange(double hi, double lo, double? pc)
		{ return pc == null ? hi - lo : Math.Max(hi - lo, Math.Max(Math.Abs(hi - pc.Value), Math.Abs(lo - pc.Value))); }
		private static void LinReg(List<double> v, int n, out double slope, out double icpt)
		{
			int st = v.Count - n; slope = 0; icpt = 0;
			if (n < 3) return;
			double mx = (n - 1) / 2.0, my = 0; for (int i = 0; i < n; i++) my += v[st + i]; my /= n;
			double sxx = 0, sxy = 0;
			for (int i = 0; i < n; i++) { double dx = i - mx; sxx += dx * dx; sxy += dx * (v[st + i] - my); }
			slope = sxy / sxx; icpt = my - slope * mx;
		}
		private static Brush DirBrush(string d) { return d == "UP" ? Brushes.LimeGreen : d == "DOWN" ? Brushes.Red : Brushes.Gray; }

		private void Emit5m()
		{
			fAtr = Wilder(fAtr, fSeed, HtfAtrPeriod, TrueRange(fCur[1], fCur[2], fPrev)); fPrev = fCur[3]; fCl.Add(fCur[3]); fCur = null;
			if (fAtr != null && fAtr.Value != 0 && fCl.Count >= HtfLookback)
			{
				double s, b; LinReg(fCl, HtfLookback, out s, out b);
				fSlope = s; fMove = s * (HtfLookback - 1) / fAtr.Value;
				fDir = s > 0 ? "UP" : s < 0 ? "DOWN" : "NONE";
			}
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0) return;
			DateTime t = Time[0];
			if (t < SeedFrom) return;
			double bo = Open[0], bh = High[0], bl = Low[0], bc = Close[0];
			atr = Wilder(atr, atrSeed, AtrPeriod, TrueRange(bh, bl, prevClose)); prevClose = bc;
			emaF = emaF == null ? bc : emaF + 2.0 / (EmaFast + 1) * (bc - emaF.Value);
			emaS = emaS == null ? bc : emaS + 2.0 / (EmaSlow + 1) * (bc - emaS.Value);
			c.Add(bc); count++;
			long b5 = TimeSpan.FromMinutes(5).Ticks;
			DateTime k = new DateTime(((t.Ticks + b5 - 1) / b5) * b5);
			if (fKey != DateTime.MinValue && k != fKey && fCur != null) Emit5m();
			if (fCur == null || k != fKey) { fKey = k; fCur = new[] { bo, bh, bl, bc }; }
			else { fCur[1] = Math.Max(fCur[1], bh); fCur[2] = Math.Min(fCur[2], bl); fCur[3] = bc; }
			if (t == k) Emit5m();
			if (c.Count > 2000) c.RemoveRange(0, 1000);
			if (fCl.Count > 500) fCl.RemoveRange(0, 250);

			Values[3][0] = bl - 2 * TickSize; PlotBrushes[3][0] = DirBrush(fDir);

			string ch = "NONE"; double s3 = 0, i3 = 0, m3 = 0, res = 0, mid = 0;
			if (c.Count >= ChLen && atr != null && atr.Value != 0)
			{
				LinReg(c, ChLen, out s3, out i3); m3 = s3 * (ChLen - 1) / atr.Value;
				ch = s3 > 0 ? "UP" : s3 < 0 ? "DOWN" : "NONE";
				int st = c.Count - ChLen;
				for (int i = 0; i < ChLen; i++) res = Math.Max(res, Math.Abs(c[st + i] - (i3 + s3 * i)));
				mid = i3 + s3 * (ChLen - 1);
				Brush cb = DirBrush(ch);
				Values[0][0] = mid; Values[1][0] = mid + res; Values[2][0] = mid - res;
				PlotBrushes[0][0] = cb; PlotBrushes[1][0] = cb; PlotBrushes[2][0] = cb;
			}
			string agree = (ch != "NONE" && ch == fDir) ? ch : "NONE";
			if (ShowBackground)
				BackBrush = agree == "UP" ? Brushes.DarkGreen : agree == "DOWN" ? Brushes.DarkRed : null;

			if (ShowLegacy && c.Count >= TrendLookback && atr != null)
			{
				double n = TrendLookback, sl, ic; LinReg(c, TrendLookback, out sl, out ic);
				double a = atr.Value, mx = (n - 1) / 2.0, sxx = n * (n * n - 1) / 12.0, w = (n - 1 - mx) / sxx;
				Values[4][0] = emaF.Value; Values[5][0] = emaS.Value; Values[6][0] = ic + sl * (n - 1);
				Values[7][0] = bc + (TrendMoveAtr * a / (n - 1) - sl) / w; Values[8][0] = bc + (-TrendMoveAtr * a / (n - 1) - sl) / w;
			}

			if (ShowInfo && FlIsLastBar())
			{
				if (ch != "NONE")
				{
					Brush cb = DirBrush(ch);
					Draw.Line(this, "FL_ch_mid", false, ChLen - 1, i3, 0, mid, cb, DashStyleHelper.Solid, 2);
					Draw.Line(this, "FL_ch_up", false, ChLen - 1, i3 + res, 0, mid + res, cb, DashStyleHelper.Dash, 1);
					Draw.Line(this, "FL_ch_dn", false, ChLen - 1, i3 - res, 0, mid - res, cb, DashStyleHelper.Dash, 1);
				}
				Draw.TextFixed(this, "FL_info", string.Format(
					"Fitz Labs (free) @ {0:HH:mm}  close {1}\n1m 30-bar slope {2:+0.0000;-0.0000;0} pt/bar -> {3}  (move {4:F3} ATR70)\n5m 12-bar slope {5:+0.0000;-0.0000;0} pt/bar -> {6}  (move {7:F3} ATR14-5m)\nAgree: {8}",
					t, bc, s3, ch, m3, fSlope, fDir, fMove, agree == "NONE" ? "no (entries blocked)" : "BOTH " + agree),
					TextPosition.TopLeft, Brushes.White, new SimpleFont("Consolas", 11), Brushes.Transparent, Brushes.Black, 60);
			}
		}

		private bool FlIsLastBar() { return State == State.Realtime || CurrentBar >= Bars.Count - 2; }

		#region Properties
		[Range(3, int.MaxValue)][Display(Name = "ChannelLen (1m)", GroupName = "Free profile", Order = 1)] public int ChLen { get; set; }
		[Range(3, int.MaxValue)][Display(Name = "Htf5mLookback", GroupName = "Free profile", Order = 2)] public int HtfLookback { get; set; }
		[Range(1, int.MaxValue)][Display(Name = "AtrPeriod (1m)", GroupName = "Free profile", Order = 3)] public int AtrPeriod { get; set; }
		[Range(1, int.MaxValue)][Display(Name = "Htf5mAtrPeriod", GroupName = "Free profile", Order = 4)] public int HtfAtrPeriod { get; set; }
		[Display(Name = "SeedFrom (bar end; runner today: 10/19/2026 12:10)", GroupName = "Display", Order = 1)] public DateTime SeedFrom { get; set; }
		[Display(Name = "ShowBackground (both agree)", GroupName = "Display", Order = 2)] public bool ShowBackground { get; set; }
		[Display(Name = "ShowInfo", GroupName = "Display", Order = 3)] public bool ShowInfo { get; set; }
		[Display(Name = "ShowLegacy (EMA/LR120/NeedUp/NeedDn)", GroupName = "Legacy", Order = 1)] public bool ShowLegacy { get; set; }
		[Range(3, int.MaxValue)][Display(Name = "TrendLookback", GroupName = "Legacy", Order = 2)] public int TrendLookback { get; set; }
		[Range(1, int.MaxValue)][Display(Name = "EmaFast", GroupName = "Legacy", Order = 3)] public int EmaFast { get; set; }
		[Range(1, int.MaxValue)][Display(Name = "EmaSlow", GroupName = "Legacy", Order = 4)] public int EmaSlow { get; set; }
		[Display(Name = "TrendMoveAtr", GroupName = "Legacy", Order = 5)] public double TrendMoveAtr { get; set; }
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private FitzLabsChannel[] cacheFitzLabsChannel;
		public FitzLabsChannel FitzLabsChannel()
		{
			return FitzLabsChannel(Input);
		}

		public FitzLabsChannel FitzLabsChannel(ISeries<double> input)
		{
			if (cacheFitzLabsChannel != null)
				for (int idx = 0; idx < cacheFitzLabsChannel.Length; idx++)
					if (cacheFitzLabsChannel[idx] != null &&  cacheFitzLabsChannel[idx].EqualsInput(input))
						return cacheFitzLabsChannel[idx];
			return CacheIndicator<FitzLabsChannel>(new FitzLabsChannel(), input, ref cacheFitzLabsChannel);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.FitzLabsChannel FitzLabsChannel()
		{
			return indicator.FitzLabsChannel(Input);
		}

		public Indicators.FitzLabsChannel FitzLabsChannel(ISeries<double> input )
		{
			return indicator.FitzLabsChannel(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.FitzLabsChannel FitzLabsChannel()
		{
			return indicator.FitzLabsChannel(Input);
		}

		public Indicators.FitzLabsChannel FitzLabsChannel(ISeries<double> input )
		{
			return indicator.FitzLabsChannel(input);
		}
	}
}

#endregion
