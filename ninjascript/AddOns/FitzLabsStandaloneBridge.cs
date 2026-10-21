// =============================================================================
// FitzLabsStandaloneBridge.cs — Fitz Labs single-machine SIM execution AddOn (NT8 file queue).
//
// Derived from fitz-labs-trading deploy/nt8_addon/FitzLabsBridge.cs (v fitzlabs-mes-20261019.1) for a clean
// NinjaTrader 8 install with NO Jarvis add-ons. Everything it needs is in THIS file:
//   * the Jarvis helper code it used (BridgeHelpers JSON/sanitise, SymbolResolver, PollGuard, BridgeLogger,
//     RiskBlock) is vendored below as FitzSAJson / FitzSASymbolResolver / FitzSAPollGuard / FitzSALog /
//     FitzSARiskBlock, inside namespace NinjaTrader.NinjaScript.AddOns.FitzLabsStandalone, so it cannot clash
//     with Jarvis copies (or with FitzLabsBridge.cs) if both ever end up in the same Custom project.
//
// Rules:
//   * queue root  %FITZLABS_ROOT%\data\nt8_queue_fitzlabs\  (default C:\FitzLabs\data\nt8_queue_fitzlabs\)
//   * client_id FITZ_LABS, order_id FitzLabs_*, schema 1.1, paper_only, envelope max age 30 s
//   * accounts: Provider == Simulator AND name starts with "Sim" (unless <queue>\fitzlabs_allow_non_sim_names.txt
//     exists — still Simulator provider only). If <queue>\fitzlabs_accounts.txt exists, ONLY the accounts listed
//     there (one per line) are traded/snapshotted.
//   * ES only (full-size, front month; root lookup -> quarterly candidates), MARKET entry, quantity 1..10. Exits after a confirmed fill, sized to the filled qty:
//       FitzTP_<id> limit at avg_fill +/- take_profit_ticks, and (when stop_loss_ticks is given)
//       FitzSL_<id> stop-market at avg_fill -/+ stop_loss_ticks, both in one OCO group (GTC).
//     A stop that NT8 rejects, or a fill already beyond the stop, triggers an immediate market close (FitzCL_).
//   * FLATTEN(ACCOUNT): cancels Fitz orders, market-closes ES positions; refuses foreign orders/non-ES positions.
//   * HELD (no NT8 order calls at all) unless <queue>\fitzlabs_execution_release.txt contains exactly ADDON_VERSION.
// NOT COMPILED: written and statically reviewed without NinjaTrader. First F5 is the compile test.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Code;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript.AddOns.FitzLabsStandalone
{
    // ------------------------------------------------------------------ vendored helpers (from Jarvis add-ons)
    internal static class FitzSALog
    {
        internal static void Log(string message)
        {
            try { Output.Process(message, PrintTo.OutputTab1); } catch { }
            System.Diagnostics.Trace.WriteLine(message);
        }
    }

    internal sealed class FitzSARiskBlock
    {
        public int? StopLossTicks, TakeProfitTicks, TrailingStopTicks, TrailingStopActivationTicks;
        public double? StopLossPrice, TakeProfitPrice;
    }

    internal static class FitzSAJson
    {
        internal static string SanitizeFilename(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "") sb.Append((char.IsLetterOrDigit(c) || c == '-' || c == '_') ? c : '_');
            return sb.ToString();
        }

        internal static string ReadString(string json, string key)
        {
            string raw = ReadRawValue(json, key).Trim();
            if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase)) return "";
            if (raw.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"') return Unescape(raw.Substring(1, raw.Length - 2));
            return "";
        }

        // Single-pass unescape (chained Replace calls are order-dependent).
        internal static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length); int i = 0;
            while (i < s.Length)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    switch (s[i + 1])
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append('\\'); sb.Append(s[i + 1]); break;
                    }
                    i += 2;
                }
                else { sb.Append(s[i]); i++; }
            }
            return sb.ToString();
        }

        internal static int ReadInt(string json, string key)
        {
            int v; return int.TryParse(ReadRawValue(json, key).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        internal static bool ReadBool(string json, string key)
        {
            return ReadRawValue(json, key).Trim().ToLowerInvariant() == "true";
        }

        internal static double? ReadNullableDouble(string json, string key)
        {
            string raw = ReadRawValue(json, key).Trim();
            if (raw == "null" || raw == "") return null;
            double v; return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : (double?)null;
        }

        internal static int? ReadNullableInt(string json, string key)
        {
            string raw = ReadRawValue(json, key).Trim();
            if (raw == "null" || raw == "") return null;
            int v; return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : (int?)null;
        }

        // Nested "risk" object; null when absent or null.
        internal static FitzSARiskBlock ReadRiskBlock(string json)
        {
            const string search = "\"risk\"";
            int ki = json.IndexOf(search, StringComparison.Ordinal);
            if (ki < 0) return null;
            int colon = json.IndexOf(':', ki + search.Length);
            if (colon < 0) return null;
            int start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\r' || json[start] == '\n')) start++;
            if (start >= json.Length || json[start] != '{') return null;
            int depth = 0, end = start;
            for (; end < json.Length; end++)
            {
                if (json[end] == '{') depth++;
                else if (json[end] == '}') { depth--; if (depth == 0) { end++; break; } }
            }
            string block = json.Substring(start, end - start);
            return new FitzSARiskBlock
            {
                StopLossTicks = ReadNullableInt(block, "stop_loss_ticks"),
                TakeProfitTicks = ReadNullableInt(block, "take_profit_ticks"),
                TrailingStopTicks = ReadNullableInt(block, "trailing_stop_ticks"),
                TrailingStopActivationTicks = ReadNullableInt(block, "trailing_stop_activation_ticks"),
                StopLossPrice = ReadNullableDouble(block, "stop_loss_price"),
                TakeProfitPrice = ReadNullableDouble(block, "take_profit_price"),
            };
        }

        // Machine-generated JSON (Python json.dumps): value runs to the next ',', newline or '}'.
        internal static string ReadRawValue(string json, string key)
        {
            string search = "\"" + key + "\"";
            int ki = json.IndexOf(search, StringComparison.Ordinal);
            if (ki < 0) return "";
            int colon = json.IndexOf(':', ki + search.Length);
            if (colon < 0) return "";
            int start = colon + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\r' || json[start] == '\n')) start++;
            if (start >= json.Length) return "";
            int end = start;
            while (end < json.Length && json[end] != ',' && json[end] != '\n' && json[end] != '}') end++;
            return json.Substring(start, end - start).Trim();
        }

        internal static string Quote(string s) { return "\"" + Escape(s ?? "") + "\""; }

        internal static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        internal static string Num(double d) { return d.ToString("R", CultureInfo.InvariantCulture); }
    }

    // Non-reentrant, non-blocking run guard: an overlapping timer tick is SKIPPED, never queued.
    internal sealed class FitzSAPollGuard
    {
        private int _inFlight;
        private long _runs, _failures;
        public bool TryEnter()
        {
            if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) return false;
            Interlocked.Increment(ref _runs);
            return true;
        }
        public void Exit(bool failed)
        {
            if (failed) Interlocked.Increment(ref _failures);
            Volatile.Write(ref _inFlight, 0);
        }
        public long Runs { get { return Interlocked.Read(ref _runs); } }
        public long Failures { get { return Interlocked.Read(ref _failures); } }
    }

    // Root -> active contract (from Jarvis SymbolResolver; allowed roots reduced to ES).
    internal static class FitzSASymbolResolver
    {
        internal static string ExtractRootSymbol(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return symbol ?? "";
            int space = symbol.IndexOf(' ');
            return space > 0 ? symbol.Substring(0, space).Trim() : symbol.Trim();
        }

        internal static bool TryResolveInstrument(string requestedSymbol, out Instrument instrument, out string rootSymbol, out string resolvedSymbol, out string reason)
        {
            instrument = null; rootSymbol = ""; resolvedSymbol = ""; reason = "";
            string requested = (requestedSymbol ?? "").Trim();
            if (requested.Length == 0) { reason = "symbol is empty."; return false; }
            rootSymbol = ExtractRootSymbol(requested).ToUpperInvariant();
            if (rootSymbol != FitzSAConfig.SYMBOL_ROOT) { reason = "root '" + rootSymbol + "' is not allowed."; return false; }
            if (!string.Equals(requested, rootSymbol, StringComparison.OrdinalIgnoreCase))
            {
                instrument = Instrument.GetInstrument(requested);
                if (instrument == null) { reason = "contract " + requested + " not found in NT8."; return false; }
                resolvedSymbol = instrument.FullName ?? requested;
                if (ExtractRootSymbol(resolvedSymbol).ToUpperInvariant() != rootSymbol) { instrument = null; reason = "resolved root mismatch"; return false; }
                return true;
            }
            var tried = new List<string>();
            foreach (string candidate in BuildQuarterlyCandidates(rootSymbol, DateTime.UtcNow, 8))
            {
                tried.Add(candidate);
                instrument = Instrument.GetInstrument(candidate);
                if (instrument == null) continue;
                resolvedSymbol = instrument.FullName ?? candidate;
                if (ExtractRootSymbol(resolvedSymbol).ToUpperInvariant() == rootSymbol) return true;
                instrument = null; resolvedSymbol = "";
            }
            Instrument rootLookup = Instrument.GetInstrument(rootSymbol);
            if (rootLookup != null && ExtractRootSymbol(rootLookup.FullName ?? "").ToUpperInvariant() == rootSymbol)
            {
                instrument = rootLookup; resolvedSymbol = rootLookup.FullName; return true;
            }
            reason = "Unable to resolve " + rootSymbol + " to an NT8 contract. Tried: " + string.Join(", ", tried) + ". Open an ES chart.";
            return false;
        }

        // Front quarterly contract first (Mar/Jun/Sep/Dec), like the Jarvis resolver (previous quarter skipped).
        internal static IEnumerable<string> BuildQuarterlyCandidates(string rootSymbol, DateTime utcNow, int horizonQuarters)
        {
            int[] qm = { 3, 6, 9, 12 };
            int startQi = 0, startYear = utcNow.Year; bool found = false;
            for (int i = 0; i < 4; i++) if (qm[i] > utcNow.Month) { startQi = i; found = true; break; }
            if (!found) { startQi = 0; startYear = utcNow.Year + 1; }
            for (int offset = 0; offset < horizonQuarters; offset++)
            {
                int total = startQi + offset;
                int month = qm[total % 4], year = startYear + total / 4;
                yield return rootSymbol + " " + month.ToString("00", CultureInfo.InvariantCulture) + "-" + (year % 100).ToString("00", CultureInfo.InvariantCulture);
            }
        }
    }

    // ------------------------------------------------------------------ configuration / gates
    internal static class FitzSAConfig
    {
        public const string ADDON_VERSION   = "fitzlabs-sa-20261020.1";
        public const string CLIENT_ID       = "FITZ_LABS";
        public const string ORDER_PREFIX    = "FitzLabs_";
        public const string TARGET_PREFIX   = "FitzTP_";
        public const string STOP_PREFIX     = "FitzSL_";
        public const string CLOSE_PREFIX    = "FitzCL_";
        public const string OCO_PREFIX      = "FitzOCO_";
        public const string ROOT_ENV        = "FITZLABS_ROOT";
        public const string DEFAULT_ROOT    = @"C:\FitzLabs";
        public const string QUEUE_SUBDIR    = "data\\nt8_queue_fitzlabs";
        public const string RELEASE_FILE    = "fitzlabs_execution_release.txt";
        public const string ACCOUNTS_FILE   = "fitzlabs_accounts.txt";
        public const string ALLOW_ANY_NAME_FILE = "fitzlabs_allow_non_sim_names.txt";
        public const string EVIDENCE_TIER   = "FITZ_LABS_SIM";
        public const string SYMBOL_ROOT     = "ES";
        public const int    MAX_QUANTITY    = 10;
        public const int    MAX_TICKS       = 2000;
        public const int    MAX_ENVELOPE_AGE_S = 30;
        public const int    MAX_LAST_AGE_S  = 15;
        public const int    POLL_MS         = 1000;
        public const int    SNAPSHOT_MS     = 3000;
        public const int    EXIT_REPAIR_RETRY_S = 60;

        // Set once by the runtime at Start() from the queue folder.
        internal static volatile bool AllowNonSimNames;
        internal static HashSet<string> AccountAllowList;   // null = any name passing the Sim rule

        internal static bool IsAllowedAccountName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 44) return false;
            foreach (char c in name) if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) return false;
            if (!AllowNonSimNames && !(name.StartsWith("Sim", StringComparison.Ordinal) && name.Length > 3)) return false;
            var list = AccountAllowList;
            return list == null || list.Contains(name);
        }

        internal static bool IsFitzOrderName(string name)
        {
            return name != null && (name.StartsWith(ORDER_PREFIX, StringComparison.Ordinal) || name.StartsWith(TARGET_PREFIX, StringComparison.Ordinal)
                || name.StartsWith(STOP_PREFIX, StringComparison.Ordinal) || name.StartsWith(CLOSE_PREFIX, StringComparison.Ordinal));
        }
    }

    internal sealed class FitzSAEnvelope
    {
        public string SchemaVersion = "", BridgeStatus = "", ClientId = "", OrderId = "", Account = "", Symbol = "";
        public string Side = "", FlattenScope = "", EntryType = "", RiskStatus = "", VerifierStatus = "", QueuedAt = "";
        public string StrategyId = "", ExperimentId = "", EvidenceTier = "";
        public int? Quantity; public bool PaperOnly; public FitzSARiskBlock Risk; public bool HasRiskObject;
    }

    // Pure parsing + gates. No I/O, no NT8 calls.
    internal static class FitzSAValidator
    {
        internal static FitzSAEnvelope Parse(string json)
        {
            var e = new FitzSAEnvelope();
            e.SchemaVersion  = FitzSAJson.ReadString(json, "schema_version");
            e.BridgeStatus   = FitzSAJson.ReadString(json, "bridge_status");
            e.ClientId       = FitzSAJson.ReadString(json, "client_id");
            e.OrderId        = FitzSAJson.ReadString(json, "order_id");
            e.Account        = FitzSAJson.ReadString(json, "account");
            e.Symbol         = FitzSAJson.ReadString(json, "symbol");
            e.Side           = FitzSAJson.ReadString(json, "side");
            e.Quantity       = FitzSAJson.ReadNullableInt(json, "quantity");
            e.FlattenScope   = FitzSAJson.ReadString(json, "flatten_scope");
            e.EntryType      = FitzSAJson.ReadString(json, "entry_type");
            e.RiskStatus     = FitzSAJson.ReadString(json, "risk_status");
            e.PaperOnly      = FitzSAJson.ReadBool(json, "paper_only");
            e.VerifierStatus = FitzSAJson.ReadString(json, "verifier_status");
            e.QueuedAt       = FitzSAJson.ReadString(json, "queued_at");
            e.StrategyId     = FitzSAJson.ReadString(json, "strategy_id");
            e.ExperimentId   = FitzSAJson.ReadString(json, "experiment_id");
            e.EvidenceTier   = FitzSAJson.ReadString(json, "evidence_tier");
            e.Risk           = FitzSAJson.ReadRiskBlock(json);
            e.HasRiskObject  = e.Risk != null;
            return e;
        }

        // "" when every gate passes, else the reject reason.
        internal static string Validate(FitzSAEnvelope e, DateTime nowUtc)
        {
            if (e == null) return "Unparseable envelope.";
            if (e.ClientId != FitzSAConfig.CLIENT_ID) return "client_id must be FITZ_LABS.";
            if (string.IsNullOrEmpty(e.OrderId) || e.OrderId.Length > 64
                || !System.Text.RegularExpressions.Regex.IsMatch(e.OrderId, @"\AFitzLabs_[A-Za-z0-9][A-Za-z0-9_-]{0,54}\z"))
                return "order_id must match FitzLabs_[A-Za-z0-9_-] (max 64).";
            if (e.SchemaVersion != "1.1") return "schema_version must be 1.1.";
            if (e.BridgeStatus != "QUEUED") return "bridge_status must be QUEUED.";
            if (!e.PaperOnly) return "paper_only must be true.";
            if (e.VerifierStatus != "APPROVED") return "verifier_status must be APPROVED.";
            if (string.Equals(e.RiskStatus, "RED", StringComparison.OrdinalIgnoreCase)) return "risk_status RED.";
            if (!FitzSAConfig.IsAllowedAccountName(e.Account)) return "account name not allowed (Sim* rule / fitzlabs_accounts.txt).";
            if (e.EvidenceTier != FitzSAConfig.EVIDENCE_TIER) return "evidence_tier must be FITZ_LABS_SIM.";
            DateTimeOffset queued;
            if (!System.Text.RegularExpressions.Regex.IsMatch(e.QueuedAt ?? "", @"(Z|[+-]\d{2}:\d{2})$")
                || !DateTimeOffset.TryParse(e.QueuedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out queued))
                return "queued_at with explicit offset required.";
            double age = (new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)) - queued).TotalSeconds;
            if (age < -5 || age > FitzSAConfig.MAX_ENVELOPE_AGE_S) return "stale or future envelope (max age 30 s).";

            if (e.Side == "BUY" || e.Side == "SELL")
            {
                if (string.IsNullOrWhiteSpace(e.StrategyId) || string.IsNullOrWhiteSpace(e.ExperimentId)) return "strategy_id and experiment_id required.";
                if (FitzSASymbolResolver.ExtractRootSymbol(e.Symbol ?? "").ToUpperInvariant() != FitzSAConfig.SYMBOL_ROOT) return "symbol must be ES.";
                if (!e.Quantity.HasValue || e.Quantity.Value < 1 || e.Quantity.Value > FitzSAConfig.MAX_QUANTITY) return "quantity must be 1..10.";
                if (!string.IsNullOrEmpty(e.FlattenScope)) return "flatten_scope must be null for BUY/SELL.";
                if (e.EntryType != "MARKET") return "entry_type must be MARKET.";
                if (e.Risk == null || !e.Risk.TakeProfitTicks.HasValue || e.Risk.TakeProfitTicks.Value < 1 || e.Risk.TakeProfitTicks.Value > FitzSAConfig.MAX_TICKS)
                    return "risk.take_profit_ticks 1..2000 required.";
                if (e.Risk.StopLossTicks.HasValue && (e.Risk.StopLossTicks.Value < 1 || e.Risk.StopLossTicks.Value > FitzSAConfig.MAX_TICKS))
                    return "risk.stop_loss_ticks must be 1..2000 or null.";
                if (e.Risk.StopLossPrice.HasValue || e.Risk.TakeProfitPrice.HasValue || e.Risk.TrailingStopTicks.HasValue || e.Risk.TrailingStopActivationTicks.HasValue)
                    return "price-based / trailing legs are not supported (ticks only).";
                return "";
            }
            if (e.Side == "FLATTEN")
            {
                if (e.FlattenScope != "ACCOUNT") return "FLATTEN requires flatten_scope ACCOUNT.";
                if (!string.IsNullOrEmpty(e.Symbol) || e.Quantity.HasValue || e.HasRiskObject) return "FLATTEN must have no symbol, quantity or risk.";
                return "";
            }
            return "side must be BUY, SELL or FLATTEN.";
        }
    }

    // ------------------------------------------------------------------ AddOn host
    public class FitzLabsStandaloneBridge : AddOnBase
    {
        private static readonly object Gate = new object();
        private static FitzSARuntime Runtime;
        private static int Hosts;
        private bool _acquired;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "FitzLabsStandaloneBridge";
                Description = "Fitz Labs single-machine SIM lane (own queue, Sim accounts, ES, TP + fixed stop OCO). Held until release file.";
            }
            else if (State == State.Active)
            {
                lock (Gate)
                {
                    if (_acquired) return;
                    try
                    {
                        if (Runtime == null) { var r = new FitzSARuntime(); r.Start(); Runtime = r; }
                        Hosts++; _acquired = true;
                    }
                    catch (Exception ex) { FitzSALog.Log("[FITZSA] start failed (lane stays off): " + ex.Message); }
                }
            }
            else if (State == State.Terminated)
            {
                lock (Gate)
                {
                    if (!_acquired) return;
                    _acquired = false; Hosts--;
                    if (Hosts <= 0 && Runtime != null) { try { Runtime.Stop(); } catch { } Runtime = null; Hosts = 0; }
                }
            }
        }
    }

    internal sealed class FitzSARuntime
    {
        private string _root, _queue, _processed, _rejected, _ack, _results, _snapshots;
        private Timer _poll, _snap;
        private volatile bool _running;
        private readonly FitzSAPollGuard _pollGuard = new FitzSAPollGuard();
        private readonly FitzSAPollGuard _snapGuard = new FitzSAPollGuard();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<Account> _subscribed = new HashSet<Account>();
        private readonly object _lock = new object();
        private readonly Dictionary<string, FitzTrade> _trades = new Dictionary<string, FitzTrade>(StringComparer.Ordinal);

        private sealed class FitzTrade
        {
            public string OrderId, Account, Side, Instrument; public int TpTicks, SlTicks, Filled; public double AvgFill, TpPrice, SlPrice;
            public string Status = "SUBMITTED";   // SUBMITTED|OPEN|TP_FILLED|SL_FILLED|CLOSED|ENTRY_FAILED|UNCERTAIN
            public int OcoSeq;
            public DateTime LastRepairUtc = DateTime.MinValue;
            public string Id { get { return OrderId.Substring(FitzSAConfig.ORDER_PREFIX.Length); } }
        }

        internal void Start()
        {
            _root = Environment.GetEnvironmentVariable(FitzSAConfig.ROOT_ENV);
            if (string.IsNullOrWhiteSpace(_root)) _root = FitzSAConfig.DEFAULT_ROOT;
            _queue = Path.GetFullPath(Path.Combine(_root, FitzSAConfig.QUEUE_SUBDIR));
            // Optional safety if a Jarvis install exists on this PC: never share its queue.
            string jroot = Environment.GetEnvironmentVariable("JARVIS_REPO_ROOT");
            if (!string.IsNullOrWhiteSpace(jroot))
            {
                string j = Path.GetFullPath(Path.Combine(jroot, "data\\nt8_queue")).TrimEnd('\\') + "\\", q = _queue.TrimEnd('\\') + "\\";
                if (q.StartsWith(j, StringComparison.OrdinalIgnoreCase) || j.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Fitz Labs queue overlaps the Jarvis queue: " + _queue);
            }
            _processed = Path.Combine(_queue, "processed"); _rejected = Path.Combine(_queue, "rejected");
            _ack = Path.Combine(_queue, "ack"); _results = Path.Combine(_queue, "results"); _snapshots = Path.Combine(_queue, "snapshots");
            foreach (var d in new[] { _queue, _processed, _rejected, _ack, _results, _snapshots }) Directory.CreateDirectory(d);
            FitzSAConfig.AllowNonSimNames = File.Exists(Path.Combine(_queue, FitzSAConfig.ALLOW_ANY_NAME_FILE));
            string af = Path.Combine(_queue, FitzSAConfig.ACCOUNTS_FILE);
            if (File.Exists(af))
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (string line in File.ReadAllLines(af)) { string n = line.Trim(); if (n.Length > 0 && !n.StartsWith("#", StringComparison.Ordinal)) set.Add(n); }
                FitzSAConfig.AccountAllowList = set;
            }
            else FitzSAConfig.AccountAllowList = null;
            foreach (string f in Directory.GetFiles(_ack))
                if (f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".claim", StringComparison.OrdinalIgnoreCase))
                    _seen.Add(Path.GetFileNameWithoutExtension(f));
            LoadTrades();
            _running = true;
            _poll = new Timer(_ => Tick(), null, 2000, FitzSAConfig.POLL_MS);
            _snap = new Timer(_ => SnapshotTick(), null, 3000, FitzSAConfig.SNAPSHOT_MS);
            FitzSALog.Log("[FITZSA] v" + FitzSAConfig.ADDON_VERSION + " queue=" + _queue + " held=" + Held()
                + " accounts=" + (FitzSAConfig.AccountAllowList == null ? "any Sim*" : string.Join(",", FitzSAConfig.AccountAllowList)));
        }

        internal void Stop()
        {
            _running = false;
            try { if (_poll != null) _poll.Dispose(); } catch { }
            try { if (_snap != null) _snap.Dispose(); } catch { }
            lock (_lock)
            {
                foreach (Account a in _subscribed)
                {
                    try { a.OrderUpdate -= OnOrderUpdate; a.ExecutionUpdate -= OnExecutionUpdate; } catch { }
                }
                _subscribed.Clear();
            }
        }

        private bool Held()
        {
            try { return File.ReadAllText(Path.Combine(_queue, FitzSAConfig.RELEASE_FILE)).Trim() != FitzSAConfig.ADDON_VERSION; }
            catch { return true; }
        }

        private static bool IsTradableAccount(Account a)
        {
            return a != null && a.Provider == Provider.Simulator && FitzSAConfig.IsAllowedAccountName(a.Name);
        }

        // ---------------------------------------------------------------- polling
        private void Tick()
        {
            if (!_running || !_pollGuard.TryEnter()) return;
            bool failed = true;
            try
            {
                string[] files = Directory.GetFiles(_queue, "*.json", SearchOption.TopDirectoryOnly);
                bool held = Held();
                var app = System.Windows.Application.Current;
                if (app == null) { failed = false; return; }
                app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                {
                    try
                    {
                        if (!_running) return;
                        Subscribe();
                        if (held) return;           // HELD: envelopes stay in the queue untouched, no NT8 order calls
                        RepairExits();
                        foreach (string f in files.OrderBy(x => x, StringComparer.Ordinal)) ProcessFile(f);
                    }
                    catch (Exception ex) { FitzSALog.Log("[FITZSA] dispatcher error: " + ex.Message); }
                }));
                failed = false;
            }
            catch (Exception ex) { FitzSALog.Log("[FITZSA] poll error: " + ex.Message); }
            finally { _pollGuard.Exit(failed); }
        }

        private void Subscribe()
        {
            lock (_lock)
                foreach (Account a in Account.All)
                {
                    if (!IsTradableAccount(a) || _subscribed.Contains(a)) continue;
                    a.OrderUpdate += OnOrderUpdate; a.ExecutionUpdate += OnExecutionUpdate; _subscribed.Add(a);
                }
        }

        private void ProcessFile(string path)
        {
            string raw;
            try { raw = File.ReadAllText(path, Encoding.UTF8); } catch (IOException) { return; }
            string t = raw.TrimStart();
            if (t.Length < 20 || !t.StartsWith("{", StringComparison.Ordinal) || !raw.TrimEnd().EndsWith("}", StringComparison.Ordinal)) return; // partial
            FitzSAEnvelope env = null;
            try { env = FitzSAValidator.Parse(raw); } catch { }
            string id = env == null ? "" : env.OrderId;
            string safeId = FitzSAJson.SanitizeFilename(string.IsNullOrEmpty(id) ? Path.GetFileNameWithoutExtension(path) : id);
            if (env != null && !string.IsNullOrEmpty(id))
            {
                lock (_lock)
                {
                    if (_seen.Contains(safeId)) { Ack(env, safeId, "DUPLICATE", "order_id already processed; not resubmitted", path, true); Move(path, _processed); return; }
                    _seen.Add(safeId);
                }
            }
            string reason = FitzSAValidator.Validate(env, DateTime.UtcNow);
            if (reason.Length != 0) { Ack(env, safeId, "REJECTED", reason, path, false); Move(path, _rejected); return; }
            // Durable claim before any NT8 call; never replayed after a restart.
            try { using (var fs = new FileStream(Path.Combine(_ack, safeId + ".claim"), FileMode.CreateNew)) { byte[] b = Encoding.UTF8.GetBytes(raw); fs.Write(b, 0, b.Length); fs.Flush(true); } }
            catch (IOException) { Ack(env, safeId, "DUPLICATE", "durable claim exists; not resubmitted", path, true); Move(path, _processed); return; }
            Move(path, _processed);
            try
            {
                if (env.Side == "FLATTEN") Flatten(env.Account, env.OrderId, safeId, env, path);
                else Enter(env, safeId, path);
            }
            catch (Exception ex) { Ack(env, safeId, "REJECTED", "FAILED_SAFE: " + ex.Message, path, false); }
        }

        private Account ResolveAccount(string name)
        {
            Account found = null; int n = 0;
            foreach (Account a in Account.All) if (a != null && string.Equals(a.Name, name, StringComparison.Ordinal)) { found = a; n++; }
            if (n != 1) throw new InvalidOperationException("missing or ambiguous account " + name);
            if (found.Provider != Provider.Simulator) throw new InvalidOperationException("Simulator provider required (live accounts are never traded)");
            if (!FitzSAConfig.IsAllowedAccountName(found.Name)) throw new InvalidOperationException("account name not allowed");
            if (found.ConnectionStatus != ConnectionStatus.Connected) throw new InvalidOperationException("account disconnected");
            return found;
        }

        private static bool IsLive(Order o)
        {
            // Fail closed: anything not terminal (including Unknown / Initialized / CancelPending) counts as working.
            return o != null && o.OrderState != OrderState.Filled && o.OrderState != OrderState.Cancelled && o.OrderState != OrderState.Rejected;
        }

        private void Enter(FitzSAEnvelope env, string safeId, string path)
        {
            Account acct = ResolveAccount(env.Account);
            lock (acct.Positions) foreach (Position p in acct.Positions) if (p != null && p.Quantity != 0) throw new InvalidOperationException("account not flat");
            lock (acct.Orders) foreach (Order o in acct.Orders) if (IsLive(o)) throw new InvalidOperationException("account has a working order (" + o.Name + ")");
            Instrument instr; string rootSym, resolved, why;
            if (!FitzSASymbolResolver.TryResolveInstrument(env.Symbol, out instr, out rootSym, out resolved, out why) || instr == null)
                throw new InvalidOperationException("symbol resolve failed: " + why);
            var last = instr.MarketData == null ? null : instr.MarketData.Last;
            if (last == null || (DateTime.UtcNow - last.Time.ToUniversalTime()).TotalSeconds > FitzSAConfig.MAX_LAST_AGE_S)
                throw new InvalidOperationException("ES Last stale/unavailable (connect a data feed and open an ES chart)");
            if (FitzSAValidator.Validate(env, DateTime.UtcNow).Length != 0) throw new InvalidOperationException("envelope expired before submit");
            var tr = new FitzTrade { OrderId = env.OrderId, Account = env.Account, Side = env.Side, Instrument = instr.FullName,
                                     TpTicks = env.Risk.TakeProfitTicks.Value, SlTicks = env.Risk.StopLossTicks.HasValue ? env.Risk.StopLossTicks.Value : 0 };
            lock (_lock) _trades[env.OrderId] = tr;
            SaveTrade(tr);
            Order order = acct.CreateOrder(instr, env.Side == "BUY" ? OrderAction.Buy : OrderAction.Sell /* same native action as the tested FitzLabsBridge lane */, OrderType.Market,
                TimeInForce.Day, env.Quantity.Value, 0, 0, "", env.OrderId, null);
            acct.Submit(new[] { order });
            Ack(env, safeId, "SUBMITTED", "MARKET " + env.Side + " " + env.Quantity.Value + " " + instr.FullName + " TP " + tr.TpTicks + " ticks, "
                + (tr.SlTicks > 0 ? "stop " + tr.SlTicks + " ticks (OCO)" : "no stop"), path, false);
        }

        // Cancels Fitz orders and market-closes ES positions on one account. Used by FLATTEN envelopes and by the
        // stop-safety path (envelope == null).
        private int Flatten(string accountName, string orderId, string safeId, FitzSAEnvelope env, string path)
        {
            Account acct = ResolveAccount(accountName);
            var cancel = new List<Order>();
            lock (acct.Orders)
                foreach (Order o in acct.Orders)
                    if (IsLive(o))
                    {
                        if (!FitzSAConfig.IsFitzOrderName(o.Name)) throw new InvalidOperationException("foreign working order " + o.Name + " on account; refusing to touch it");
                        if (o.Name.StartsWith(FitzSAConfig.CLOSE_PREFIX, StringComparison.Ordinal)) continue;   // a close already in flight
                        cancel.Add(o);
                    }
            if (cancel.Count > 0) acct.Cancel(cancel);
            int closes = 0;
            var positions = new List<Position>();
            lock (acct.Positions) foreach (Position p in acct.Positions) if (p != null && p.Quantity != 0) positions.Add(p);
            foreach (Position p in positions)
            {
                if (FitzSASymbolResolver.ExtractRootSymbol(p.Instrument.FullName).ToUpperInvariant() != FitzSAConfig.SYMBOL_ROOT)
                    throw new InvalidOperationException("non-ES position on account; refusing");
                OrderAction a = p.MarketPosition == MarketPosition.Long ? OrderAction.Sell : OrderAction.BuyToCover;
                string name = FitzSAConfig.CLOSE_PREFIX + orderId.Substring(FitzSAConfig.ORDER_PREFIX.Length) + (closes > 0 ? "_" + closes : "");
                Order close = acct.CreateOrder(p.Instrument, a, OrderType.Market, TimeInForce.Day, p.Quantity, 0, 0, "", name, null);
                acct.Submit(new[] { close }); closes++;
            }
            lock (_lock) foreach (var tr in _trades.Values) if (tr.Account == accountName && tr.Status == "OPEN") { tr.Status = "CLOSED"; SaveTrade(tr); }
            if (env != null) Ack(env, safeId, "SUBMITTED", "FLATTEN: cancelled " + cancel.Count + " Fitz order(s), " + closes + " market close(s)", path, false);
            return closes;
        }

        // ---------------------------------------------------------------- NT8 callbacks (Fitz names only)
        private void OnOrderUpdate(object sender, OrderEventArgs e)
        {
            try
            {
                if (e == null || e.Order == null || !FitzSAConfig.IsFitzOrderName(e.Order.Name)) return;
                Result(e.Order.Name, "order", "{\"name\":" + FitzSAJson.Quote(e.Order.Name) + ",\"state\":" + FitzSAJson.Quote(e.OrderState.ToString())
                    + ",\"filled\":" + e.Filled + ",\"avg_fill\":" + FitzSAJson.Num(e.AverageFillPrice)
                    + ",\"account\":" + FitzSAJson.Quote(e.Order.Account == null ? "" : e.Order.Account.Name)
                    + ",\"error\":" + FitzSAJson.Quote(e.Error.ToString()) + ",\"comment\":" + FitzSAJson.Quote(e.Comment ?? "")
                    + ",\"time\":" + FitzSAJson.Quote(DateTime.UtcNow.ToString("o")) + "}");
                string name = e.Order.Name;
                if (name.StartsWith(FitzSAConfig.TARGET_PREFIX, StringComparison.Ordinal) || name.StartsWith(FitzSAConfig.STOP_PREFIX, StringComparison.Ordinal))
                {
                    bool isStop = name.StartsWith(FitzSAConfig.STOP_PREFIX, StringComparison.Ordinal);
                    string id = FitzSAConfig.ORDER_PREFIX + StripSeq(name.Substring((isStop ? FitzSAConfig.STOP_PREFIX : FitzSAConfig.TARGET_PREFIX).Length));
                    FitzTrade t; lock (_lock) _trades.TryGetValue(id, out t);
                    if (t == null) return;
                    if (e.OrderState == OrderState.Filled) { t.Status = isStop ? "SL_FILLED" : "TP_FILLED"; SaveTrade(t); }
                    else if (isStop && e.OrderState == OrderState.Rejected && t.Status == "OPEN")
                    {
                        // A protective stop NT8 refused (e.g. price already through it): close the position now.
                        Result(t.OrderId, "safety", "{\"event\":\"stop_rejected_flatten\",\"error\":" + FitzSAJson.Quote(e.Error.ToString()) + ",\"time\":" + FitzSAJson.Quote(DateTime.UtcNow.ToString("o")) + "}");
                        try { Flatten(t.Account, t.OrderId, null, null, null); } catch (Exception ex) { FitzSALog.Log("[FITZSA] stop-reject flatten failed: " + ex.Message); }
                    }
                    return;
                }
                if (!name.StartsWith(FitzSAConfig.ORDER_PREFIX, StringComparison.Ordinal)) return;
                FitzTrade tr; lock (_lock) _trades.TryGetValue(name, out tr);
                if (tr == null) return;
                bool terminal = e.OrderState == OrderState.Filled || e.OrderState == OrderState.Cancelled || e.OrderState == OrderState.Rejected;
                if (!terminal) return;
                if (e.Filled <= 0) { tr.Status = "ENTRY_FAILED"; SaveTrade(tr); return; }
                if (tr.Status != "SUBMITTED") return;
                tr.Filled = e.Filled; tr.AvgFill = e.AverageFillPrice;
                MasterInstrument mi = e.Order.Instrument.MasterInstrument;
                double tick = mi.TickSize;
                int d = tr.Side == "BUY" ? 1 : -1;
                tr.TpPrice = mi.RoundToTickSize(tr.AvgFill + d * tr.TpTicks * tick);
                tr.SlPrice = tr.SlTicks > 0 ? mi.RoundToTickSize(tr.AvgFill - d * tr.SlTicks * tick) : 0;
                tr.Status = "OPEN"; SaveTrade(tr);
                PlaceExits(e.Order.Account, e.Order.Instrument, tr);
            }
            catch (Exception ex) { FitzSALog.Log("[FITZSA] order callback error: " + ex.Message); }
        }

        // "<id>" or "<id>~<n>" (repair generation) -> "<id>"
        private static string StripSeq(string s)
        {
            int i = s.LastIndexOf('~');
            return i > 0 ? s.Substring(0, i) : s;
        }

        private void OnExecutionUpdate(object sender, ExecutionEventArgs e)
        {
            try
            {
                if (e == null || e.Execution == null || e.Execution.Order == null || !FitzSAConfig.IsFitzOrderName(e.Execution.Order.Name)) return;
                var x = e.Execution;
                Result(x.Order.Name + "_" + FitzSAJson.SanitizeFilename(x.ExecutionId ?? Guid.NewGuid().ToString("N")), "fill",
                    "{\"name\":" + FitzSAJson.Quote(x.Order.Name) + ",\"execution_id\":" + FitzSAJson.Quote(x.ExecutionId ?? "")
                    + ",\"account\":" + FitzSAJson.Quote(x.Account == null ? "" : x.Account.Name)
                    + ",\"instrument\":" + FitzSAJson.Quote(x.Instrument == null ? "" : x.Instrument.FullName)
                    + ",\"price\":" + FitzSAJson.Num(x.Price) + ",\"quantity\":" + x.Quantity
                    + ",\"market_position\":" + FitzSAJson.Quote(x.MarketPosition.ToString())
                    + ",\"time\":" + FitzSAJson.Quote(x.Time.ToUniversalTime().ToString("o")) + "}");
            }
            catch (Exception ex) { FitzSALog.Log("[FITZSA] execution callback error: " + ex.Message); }
        }

        // TP limit + (optional) stop-market, one OCO group, GTC, sized to the confirmed filled quantity.
        private void PlaceExits(Account acct, Instrument instr, FitzTrade tr)
        {
            if (acct == null || instr == null || Held()) return;
            tr.LastRepairUtc = DateTime.UtcNow;
            OrderAction a = tr.Side == "BUY" ? OrderAction.Sell : OrderAction.BuyToCover;
            if (tr.SlTicks > 0)
            {
                // Fill already at/through the stop (fast market): no valid stop price exists -> close now.
                var last = instr.MarketData == null ? null : instr.MarketData.Last;
                if (last != null && (tr.Side == "BUY" ? last.Price <= tr.SlPrice : last.Price >= tr.SlPrice))
                {
                    Result(tr.OrderId, "safety", "{\"event\":\"price_through_stop_flatten\",\"last\":" + FitzSAJson.Num(last.Price) + ",\"stop\":" + FitzSAJson.Num(tr.SlPrice) + "}");
                    Flatten(tr.Account, tr.OrderId, null, null, null);
                    return;
                }
            }
            tr.OcoSeq++;
            string suffix = tr.Id + (tr.OcoSeq > 1 ? "~" + tr.OcoSeq : "");
            string oco = tr.SlTicks > 0 ? FitzSAConfig.OCO_PREFIX + suffix + "_" + DateTime.UtcNow.ToString("HHmmssfff", CultureInfo.InvariantCulture) : "";
            var orders = new List<Order>();
            orders.Add(acct.CreateOrder(instr, a, OrderType.Limit, TimeInForce.Gtc, tr.Filled, tr.TpPrice, 0, oco, FitzSAConfig.TARGET_PREFIX + suffix, null));
            if (tr.SlTicks > 0)
                orders.Add(acct.CreateOrder(instr, a, OrderType.StopMarket, TimeInForce.Gtc, tr.Filled, 0, tr.SlPrice, oco, FitzSAConfig.STOP_PREFIX + suffix, null));
            acct.Submit(orders.ToArray());
            FitzSALog.Log("[FITZSA] exits placed " + suffix + " TP @ " + tr.TpPrice.ToString(CultureInfo.InvariantCulture)
                + (tr.SlTicks > 0 ? " SL @ " + tr.SlPrice.ToString(CultureInfo.InvariantCulture) : " (no stop)"));
        }

        // Restart / lapse safety: the OPEN trade must have its TP (and stop, if any) working. If both are missing,
        // re-place both (same recorded prices). If only one is working, cancel it; the next pass re-places the pair.
        // Never re-enters, never resizes.
        private void RepairExits()
        {
            List<FitzTrade> open; lock (_lock) open = _trades.Values.Where(t => t.Status == "OPEN").ToList();
            foreach (var tr in open)
            {
                if ((DateTime.UtcNow - tr.LastRepairUtc).TotalSeconds < FitzSAConfig.EXIT_REPAIR_RETRY_S) continue;
                Account acct; try { acct = ResolveAccount(tr.Account); } catch { continue; }
                Order tp = null, sl = null; bool closing = false;
                lock (acct.Orders)
                    foreach (Order o in acct.Orders)
                    {
                        if (!IsLive(o) || o.Name == null) continue;
                        if (o.Name.StartsWith(FitzSAConfig.TARGET_PREFIX + tr.Id, StringComparison.Ordinal)) tp = o;
                        else if (o.Name.StartsWith(FitzSAConfig.STOP_PREFIX + tr.Id, StringComparison.Ordinal)) sl = o;
                        else if (o.Name.StartsWith(FitzSAConfig.CLOSE_PREFIX, StringComparison.Ordinal)) closing = true;
                    }
                if (closing) continue;
                bool needStop = tr.SlTicks > 0;
                if (tp != null && (sl != null || !needStop)) continue;
                tr.LastRepairUtc = DateTime.UtcNow;
                Position pos = null; lock (acct.Positions) foreach (Position p in acct.Positions) if (p != null && p.Quantity != 0) pos = p;
                if (pos == null) { tr.Status = "CLOSED"; SaveTrade(tr); continue; }
                bool same = pos.Instrument.FullName == tr.Instrument && pos.Quantity == tr.Filled
                    && pos.MarketPosition == (tr.Side == "BUY" ? MarketPosition.Long : MarketPosition.Short);
                if (!same) { Result(tr.OrderId, "safety", "{\"breaker\":\"position_mismatch\",\"reason\":\"position does not match recorded Fitz trade; exits not re-placed\",\"time\":" + FitzSAJson.Quote(DateTime.UtcNow.ToString("o")) + "}"); continue; }
                if (tp != null || sl != null)
                {
                    var half = new List<Order>(); if (tp != null) half.Add(tp); if (sl != null) half.Add(sl);
                    acct.Cancel(half);
                    Result(tr.OrderId, "exit_repair", "{\"event\":\"cancel_half_pair\",\"time\":" + FitzSAJson.Quote(DateTime.UtcNow.ToString("o")) + "}");
                    continue;
                }
                PlaceExits(acct, pos.Instrument, tr);
                Result(tr.OrderId, "exit_repair", "{\"tp_price\":" + FitzSAJson.Num(tr.TpPrice) + ",\"sl_price\":" + FitzSAJson.Num(tr.SlPrice) + ",\"time\":" + FitzSAJson.Quote(DateTime.UtcNow.ToString("o")) + "}");
            }
        }

        // ---------------------------------------------------------------- snapshots (read-only of NT8)
        private void SnapshotTick()
        {
            if (!_running || !_snapGuard.TryEnter()) return;
            bool failed = true;
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"schema_version\":\"fitz-1\",\"client_id\":\"FITZ_LABS\",\"addon_version\":").Append(FitzSAJson.Quote(FitzSAConfig.ADDON_VERSION))
                  .Append(",\"mode\":").Append(FitzSAJson.Quote(Held() ? "HELD" : "SIM_EXECUTION"))
                  .Append(",\"observed_at\":").Append(FitzSAJson.Quote(DateTime.UtcNow.ToString("o")))
                  .Append(",\"poll_runs\":").Append(_pollGuard.Runs).Append(",\"poll_failures\":").Append(_pollGuard.Failures)
                  .Append(",\"accounts\":{");
                bool first = true;
                var names = new SortedSet<string>(StringComparer.Ordinal);
                foreach (Account a in Account.All) if (IsTradableAccount(a)) names.Add(a.Name);
                if (FitzSAConfig.AccountAllowList != null) foreach (string n in FitzSAConfig.AccountAllowList) names.Add(n);
                foreach (string name in names)
                {
                    Account acct = null; int n = 0;
                    foreach (Account a in Account.All) if (a != null && a.Name == name) { acct = a; n++; }
                    bool ok = n == 1 && acct.Provider == Provider.Simulator;
                    if (!first) sb.Append(','); first = false;
                    sb.Append(FitzSAJson.Quote(name)).Append(":{\"present\":").Append(ok ? "true" : "false");
                    if (ok)
                    {
                        sb.Append(",\"provider\":").Append(FitzSAJson.Quote(acct.Provider.ToString()))
                          .Append(",\"connection\":").Append(FitzSAJson.Quote(acct.ConnectionStatus.ToString())).Append(",\"positions\":[");
                        bool f2 = true;
                        lock (acct.Positions) foreach (Position p in acct.Positions)
                        {
                            if (p == null || p.Quantity == 0) continue;
                            if (!f2) sb.Append(','); f2 = false;
                            sb.Append("{\"instrument\":").Append(FitzSAJson.Quote(p.Instrument.FullName)).Append(",\"quantity\":").Append(p.Quantity)
                              .Append(",\"direction\":").Append(FitzSAJson.Quote(p.MarketPosition.ToString()))
                              .Append(",\"avg_price\":").Append(FitzSAJson.Num(p.AveragePrice)).Append('}');
                        }
                        sb.Append("],\"orders\":[");
                        f2 = true;
                        lock (acct.Orders) foreach (Order o in acct.Orders)
                        {
                            if (!IsLive(o)) continue;
                            if (!f2) sb.Append(','); f2 = false;
                            sb.Append("{\"name\":").Append(FitzSAJson.Quote(o.Name ?? "")).Append(",\"fitz\":").Append(FitzSAConfig.IsFitzOrderName(o.Name) ? "true" : "false")
                              .Append(",\"state\":").Append(FitzSAJson.Quote(o.OrderState.ToString())).Append(",\"type\":").Append(FitzSAJson.Quote(o.OrderType.ToString()))
                              .Append(",\"quantity\":").Append(o.Quantity).Append(",\"filled\":").Append(o.Filled)
                              .Append(",\"limit\":").Append(FitzSAJson.Num(o.LimitPrice)).Append(",\"stop\":").Append(FitzSAJson.Num(o.StopPrice))
                              .Append(",\"time\":").Append(FitzSAJson.Quote(o.Time.ToUniversalTime().ToString("o"))).Append('}');
                        }
                        sb.Append(']');
                    }
                    sb.Append('}');
                }
                sb.Append("}}");
                WriteAtomic(Path.Combine(_snapshots, "fitz_snapshot.json"), sb.ToString());
                failed = false;
            }
            catch (Exception ex) { FitzSALog.Log("[FITZSA] snapshot error: " + ex.Message); }
            finally { _snapGuard.Exit(failed); }
        }

        // ---------------------------------------------------------------- persistence (Fitz queue dirs only)
        private void Ack(FitzSAEnvelope env, string safeId, string status, string reason, string src, bool duplicate)
        {
            string body = "{\"order_id\":" + FitzSAJson.Quote(env == null ? "" : env.OrderId) + ",\"client_id\":\"FITZ_LABS\""
                + ",\"status\":" + FitzSAJson.Quote(status) + ",\"account\":" + FitzSAJson.Quote(env == null ? "" : env.Account)
                + ",\"reason\":" + FitzSAJson.Quote(reason) + ",\"addon_version\":" + FitzSAJson.Quote(FitzSAConfig.ADDON_VERSION)
                + ",\"timestamp\":" + FitzSAJson.Quote(DateTime.UtcNow.ToString("o")) + ",\"source_file\":" + FitzSAJson.Quote(src == null ? "" : Path.GetFileName(src)) + "}";
            string file = duplicate ? safeId + ".duplicate_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".json" : safeId + ".json";
            try { WriteAtomic(Path.Combine(_ack, file), body); } catch (Exception ex) { FitzSALog.Log("[FITZSA] ack write failed: " + ex.Message); }
            FitzSALog.Log("[FITZSA] " + status + " " + safeId + ": " + reason);
        }

        private void Result(string key, string kind, string body)
        {
            try { WriteAtomic(Path.Combine(_results, FitzSAJson.SanitizeFilename(key) + "." + kind + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".json"), body); }
            catch (Exception ex) { FitzSALog.Log("[FITZSA] result write failed: " + ex.Message); }
        }

        private void SaveTrade(FitzTrade t)
        {
            string body = "{\"order_id\":" + FitzSAJson.Quote(t.OrderId) + ",\"account\":" + FitzSAJson.Quote(t.Account)
                + ",\"side\":" + FitzSAJson.Quote(t.Side) + ",\"instrument\":" + FitzSAJson.Quote(t.Instrument)
                + ",\"tp_ticks\":" + t.TpTicks + ",\"sl_ticks\":" + t.SlTicks + ",\"filled\":" + t.Filled + ",\"avg_fill\":" + FitzSAJson.Num(t.AvgFill)
                + ",\"tp_price\":" + FitzSAJson.Num(t.TpPrice) + ",\"sl_price\":" + FitzSAJson.Num(t.SlPrice) + ",\"status\":" + FitzSAJson.Quote(t.Status)
                + ",\"updated_at\":" + FitzSAJson.Quote(DateTime.UtcNow.ToString("o")) + "}";
            try { WriteAtomic(Path.Combine(_results, FitzSAJson.SanitizeFilename(t.OrderId) + ".trade.json"), body); }
            catch (Exception ex) { FitzSALog.Log("[FITZSA] trade write failed: " + ex.Message); }
        }

        private void LoadTrades()
        {
            foreach (string f in Directory.GetFiles(_results, "*.trade.json"))
            {
                try
                {
                    string j = File.ReadAllText(f, Encoding.UTF8);
                    var t = new FitzTrade
                    {
                        OrderId = FitzSAJson.ReadString(j, "order_id"), Account = FitzSAJson.ReadString(j, "account"),
                        Side = FitzSAJson.ReadString(j, "side"), Instrument = FitzSAJson.ReadString(j, "instrument"),
                        TpTicks = FitzSAJson.ReadInt(j, "tp_ticks"), SlTicks = FitzSAJson.ReadInt(j, "sl_ticks"), Filled = FitzSAJson.ReadInt(j, "filled"),
                        AvgFill = FitzSAJson.ReadNullableDouble(j, "avg_fill") ?? 0, TpPrice = FitzSAJson.ReadNullableDouble(j, "tp_price") ?? 0,
                        SlPrice = FitzSAJson.ReadNullableDouble(j, "sl_price") ?? 0, Status = FitzSAJson.ReadString(j, "status")
                    };
                    if (string.IsNullOrEmpty(t.OrderId) || !t.OrderId.StartsWith(FitzSAConfig.ORDER_PREFIX, StringComparison.Ordinal)) continue;
                    // A trade still SUBMITTED at restart is ambiguous: never auto-handled, reported for review.
                    if (t.Status == "SUBMITTED") { t.Status = "UNCERTAIN"; SaveTrade(t); }
                    t.OcoSeq = 1;   // exits re-placed after a restart get a fresh "~n" name
                    _trades[t.OrderId] = t;
                }
                catch { }
            }
        }

        private static void WriteAtomic(string path, string body)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, body, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }

        private static void Move(string src, string dir)
        {
            try
            {
                string dest = Path.Combine(dir, Path.GetFileName(src));
                if (File.Exists(dest)) dest = Path.Combine(dir, Path.GetFileNameWithoutExtension(src) + "_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".json");
                File.Move(src, dest);
            }
            catch (Exception ex) { FitzSALog.Log("[FITZSA] move failed: " + ex.Message); }
        }
    }
}
