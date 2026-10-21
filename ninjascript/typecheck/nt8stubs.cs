// Minimal stand-ins for NinjaTrader 8 / WPF types, ONLY to type-check FitzLabs files with mono mcs on Linux.
// Signatures are from memory / from usage in the deployed (compiled) FitzLabsBridge.cs; not authoritative.
using System; using System.Collections.Generic;
namespace System.Windows.Threading { public enum DispatcherPriority { Background } public class Dispatcher { public object BeginInvoke(DispatcherPriority p, Delegate d) { return null; } } }
namespace System.Windows { public class Application { public static Application Current; public System.Windows.Threading.Dispatcher Dispatcher; } }
namespace NinjaTrader.Code { public enum PrintTo { OutputTab1 } public static class Output { public static void Process(string m, PrintTo t) {} } }
namespace NinjaTrader.Core { public class GeneralOptionsT { public TimeZoneInfo TimeZoneInfo; } public static class Globals { public static GeneralOptionsT GeneralOptions; } }
namespace NinjaTrader.Data { public enum BarsPeriodType { Minute, Tick } public class BarsPeriod { public BarsPeriodType BarsPeriodType; public int Value; } }
namespace NinjaTrader.Cbi {
  public enum Provider { Simulator, Rithmic } public enum ConnectionStatus { Connected, Disconnected }
  public enum OrderState { Initialized, Submitted, Accepted, Working, Filled, PartFilled, Cancelled, CancelPending, Rejected, Unknown }
  public enum OrderAction { Buy, Sell, SellShort, BuyToCover } public enum OrderType { Market, Limit, StopMarket, StopLimit }
  public enum TimeInForce { Day, Gtc } public enum MarketPosition { Long, Short, Flat } public enum ErrorCode { NoError }
  public class MarketDataEventArgs { public double Price; public DateTime Time; }
  public class MarketData { public MarketDataEventArgs Last; }
  public class MasterInstrument { public string Name; public double TickSize; public double RoundToTickSize(double p) { return p; } }
  public class Instrument { public string FullName; public MarketData MarketData; public MasterInstrument MasterInstrument; public static Instrument GetInstrument(string s) { return null; } }
  public class CustomOrder {}
  public class Order { public string Name; public OrderState OrderState; public int Filled, Quantity; public double LimitPrice, StopPrice; public OrderType OrderType; public DateTime Time; public Account Account; public Instrument Instrument; }
  public class Position { public int Quantity; public Instrument Instrument; public MarketPosition MarketPosition; public double AveragePrice; }
  public class OrderEventArgs : EventArgs { public Order Order; public OrderState OrderState; public int Filled; public double AverageFillPrice; public ErrorCode Error; public string Comment; }
  public class Execution { public Order Order; public string ExecutionId; public Account Account; public Instrument Instrument; public double Price; public int Quantity; public MarketPosition MarketPosition; public DateTime Time; }
  public class ExecutionEventArgs : EventArgs { public Execution Execution; }
  public class Account { public static List<Account> All; public string Name; public Provider Provider; public ConnectionStatus ConnectionStatus;
    public List<Position> Positions; public List<Order> Orders;
    public event EventHandler<OrderEventArgs> OrderUpdate; public event EventHandler<ExecutionEventArgs> ExecutionUpdate;
    public Order CreateOrder(Instrument i, OrderAction a, OrderType t, TimeInForce tif, int q, double lp, double sp, string oco, string name, CustomOrder c) { return null; }
    public void Submit(IEnumerable<Order> o) {} public void Cancel(IEnumerable<Order> o) {} }
}
namespace NinjaTrader.NinjaScript {
  public enum State { SetDefaults, Configure, DataLoaded, Active, Terminated } public enum Calculate { OnBarClose, OnEachTick }
  public class NinjaScriptBase { public State State; public string Name, Description; protected virtual void OnStateChange() {} public void Print(object o) {} }
  public class AddOnBase : NinjaScriptBase {}
  public class SeriesD { public double this[int i] { get { return 0; } } } public class SeriesT { public DateTime this[int i] { get { return DateTime.MinValue; } } }
  public class IndicatorBase : NinjaScriptBase { public Calculate Calculate; public bool IsOverlay, DisplayInDataBox, DrawOnPricePanel, PaintPriceMarkers, IsSuspendedWhileInactive;
    public NinjaTrader.Data.BarsPeriod BarsPeriod; public NinjaTrader.Cbi.Instrument Instrument; public int BarsInProgress, CurrentBar;
    public SeriesT Time; public SeriesD Open, High, Low, Close, Volume; protected virtual void OnBarUpdate() {} }
}
namespace NinjaTrader.NinjaScript.Indicators { public class Indicator : NinjaTrader.NinjaScript.IndicatorBase {} }
namespace System.ComponentModel.DataAnnotations { public class DisplayAttribute : Attribute { public string Name, GroupName; public int Order; } }
