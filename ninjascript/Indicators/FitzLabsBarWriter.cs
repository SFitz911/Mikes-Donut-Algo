// =============================================================================
// FitzLabsBarWriter.cs — writes every CLOSED 1-minute ES bar of the chart it is on to a local CSV for the
// Fitz Labs single-machine runner (single_machine/fitzsm/feed_csv.py). Display: nothing (no plots).
//
// CSV (UTF-8, header once, one row per bar, appended in time order):
//   bar_end_utc_epoch,bar_end_utc,open,high,low,close,volume,instrument
// * bar_end: NT8 time-based bars are stamped with their END (close) time in the time zone set in
//   NT8 Tools > Options > General. This indicator converts that to UTC (Core.Globals.GeneralOptions.TimeZoneInfo),
//   so the file is time-zone independent: same convention as es_1m.sqlite (bar END, UTC epoch seconds).
// * Never invents or fills bars: a minute with no trades has no NT8 bar and therefore no row.
// * Historical bars loaded on the chart are written too (warm-up), then each new bar as it closes.
//   Rows are only appended when strictly newer than the newest row already in the file (reload-safe).
// * Refuses to write unless the chart is 1 Minute and the instrument root is InstrumentRoot (default ES).
// * Write safety: each row is ONE complete line written + flushed; the reader ignores an unterminated last line.
// NOT COMPILED: written and statically reviewed without NinjaTrader. First F5 is the compile test.
// =============================================================================
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Text;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript.Indicators
{
    public class FitzLabsBarWriter : Indicator
    {
        private const string Header = "bar_end_utc_epoch,bar_end_utc,open,high,low,close,volume,instrument";
        private static readonly object FileGate = new object();
        private static readonly Dictionary<string, long> LastWritten = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private bool _enabled;
        private string _path;
        private TimeZoneInfo _tz;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "FitzLabsBarWriter";
                Description = "Writes closed 1-minute ES bars (bar END, UTC) to a CSV for the Fitz Labs single-machine runner.";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = false;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = false;   // keep writing when the chart tab is not visible
                string root = Environment.GetEnvironmentVariable("FITZLABS_ROOT");
                OutputPath = Path.Combine(string.IsNullOrWhiteSpace(root) ? @"C:\FitzLabs" : root, "data", "es_1m_live.csv");
                InstrumentRoot = "ES";
            }
            else if (State == State.DataLoaded)
            {
                _enabled = false;
                if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 1)
                { Print("FitzLabsBarWriter: chart must be 1 Minute; writer OFF"); return; }
                string root = Instrument.MasterInstrument.Name ?? "";
                if (!string.Equals(root, InstrumentRoot, StringComparison.OrdinalIgnoreCase))
                { Print("FitzLabsBarWriter: instrument " + Instrument.FullName + " is not " + InstrumentRoot + "; writer OFF (never substitute MES for ES)"); return; }
                try
                {
                    _tz = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;
                    _path = Path.GetFullPath(OutputPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    lock (FileGate)
                    {
                        if (!LastWritten.ContainsKey(_path)) LastWritten[_path] = ReadLastEpoch(_path);
                    }
                    _enabled = true;
                    Print("FitzLabsBarWriter: writing " + Instrument.FullName + " 1m bars to " + _path + " (NT8 tz " + _tz.Id + " -> UTC)");
                }
                catch (Exception ex) { Print("FitzLabsBarWriter: setup failed, writer OFF: " + ex.Message); }
            }
        }

        protected override void OnBarUpdate()
        {
            if (!_enabled || BarsInProgress != 0 || CurrentBar < 0) return;
            DateTime local = DateTime.SpecifyKind(Time[0], DateTimeKind.Unspecified);
            DateTime utc;
            try { utc = TimeZoneInfo.ConvertTimeToUtc(local, _tz); }
            catch (ArgumentException) { return; }   // invalid local time (DST gap) - skip, never guess
            long epoch = (long)Math.Round((utc - Epoch).TotalSeconds);
            if (epoch % 60 != 0) return;                          // not a minute-boundary bar end
            if (utc > DateTime.UtcNow.AddSeconds(1)) return;     // bar not closed yet (safety)
            string line = epoch.ToString(CultureInfo.InvariantCulture) + ","
                + utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + ","
                + P(Open[0]) + "," + P(High[0]) + "," + P(Low[0]) + "," + P(Close[0]) + ","
                + Volume[0].ToString("0.####", CultureInfo.InvariantCulture) + "," + Instrument.FullName.Replace(",", " ") + "\n";
            lock (FileGate)
            {
                long last;
                if (LastWritten.TryGetValue(_path, out last) && epoch <= last) return;   // duplicate / older
                try
                {
                    bool needHeader = !File.Exists(_path) || new FileInfo(_path).Length == 0;
                    // A torn last line (crash mid-write) is terminated first so this row is never glued onto it;
                    // the reader rejects the torn row as a bad row.
                    string prefix = needHeader ? Header + "\n" : (EndsWithNewline(_path) ? "" : "\n");
                    byte[] bytes = new UTF8Encoding(false).GetBytes(prefix + line);
                    using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
                    {
                        fs.Write(bytes, 0, bytes.Length);
                        fs.Flush(true);
                    }
                    LastWritten[_path] = epoch;
                }
                catch (IOException ex) { Print("FitzLabsBarWriter: write failed (bar " + epoch + " not written): " + ex.Message); }
            }
        }

        private static bool EndsWithNewline(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length == 0) return true;
                fs.Seek(-1, SeekOrigin.End);
                return fs.ReadByte() == '\n';
            }
        }

        private static string P(double v) { return v.ToString("0.00##", CultureInfo.InvariantCulture); }

        // Newest bar_end_utc_epoch already in the file (0 if none). Reads only the tail.
        private static long ReadLastEpoch(string path)
        {
            if (!File.Exists(path)) return 0;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long len = fs.Length; if (len == 0) return 0;
                int n = (int)Math.Min(len, 4096);
                fs.Seek(len - n, SeekOrigin.Begin);
                var buf = new byte[n]; int read = 0;
                while (read < n) { int r = fs.Read(buf, read, n - read); if (r <= 0) break; read += r; }
                string[] lines = Encoding.UTF8.GetString(buf, 0, read).Split('\n');
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string l = lines[i].Trim();
                    if (l.Length == 0) continue;
                    long v;
                    int comma = l.IndexOf(',');
                    if (comma > 0 && long.TryParse(l.Substring(0, comma), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return v;
                }
            }
            return 0;
        }

        #region Properties
        [Display(Name = "Output CSV path", Order = 1, GroupName = "Fitz Labs")]
        public string OutputPath { get; set; }

        [Display(Name = "Instrument root (must match chart)", Order = 2, GroupName = "Fitz Labs")]
        public string InstrumentRoot { get; set; }
        #endregion
    }
}
