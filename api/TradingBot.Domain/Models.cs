namespace TradingBot.Domain;

public enum EngineState { STOPPED, PAPER_RUNNING, LIVE_RUNNING, EMERGENCY_STOPPED, RECOVERY_REQUIRED, OBSERVING }
public sealed class TradingControlState
{
  public int Id { get; set; } = 1;
  public EngineState State { get; set; } = EngineState.STOPPED;
  public string Reason { get; set; } = "First installation: manual start required";
  public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
public sealed class Order
{
  public long Id { get; set; }
  public string ClientOrderId { get; set; } = "";
  public string Symbol { get; set; } = "";
  public string Side { get; set; } = "BUY";
  public int Quantity { get; set; }
  public int FilledQuantity { get; set; }
  public decimal Price { get; set; }
  public decimal Charges { get; set; }
  public decimal ReservedCash { get; set; }
  public string Status { get; set; } = "PENDING";
  public string DataSource { get; set; } = "SIMULATED";
  public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
public sealed class Position
{
  public long Id { get; set; }
  public string Symbol { get; set; } = "";
  public int Quantity { get; set; }
  public decimal Cost { get; set; }
  public decimal LastPrice { get; set; }
  public string DataSource { get; set; } = "SIMULATED";
  public DateTime OpenedUtc { get; set; } = DateTime.UtcNow;
  public DateTime PriceUtc { get; set; } = DateTime.UtcNow;
}
public sealed class Trade
{
  public long Id { get; set; }
  public long OrderId { get; set; }
  public string Symbol { get; set; } = "";
  public int Quantity { get; set; }
  public decimal Proceeds { get; set; }
  public decimal Cost { get; set; }
  public decimal Charges { get; set; }
  public decimal NetProfit { get; set; }
  public DateTime ClosedUtc { get; set; } = DateTime.UtcNow;
}
public sealed class PaperAccount
{
  public int Id { get; set; } = 1;
  public decimal InitialCapital { get; set; }
  public decimal Cash { get; set; }
  public decimal TotalCharges { get; set; }
}
public sealed class SystemEvent
{
  public long Id { get; set; }
  public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
  public string Category { get; set; } = "System";
  public string Message { get; set; } = "";
}
public sealed record Quote(string Symbol, decimal Price, DateTime TimestampUtc, string Source);
public sealed record Candle(DateTime TimestampUtc, decimal Open, decimal High, decimal Low, decimal Close, long Volume);
public sealed record PaperOrderRequest(string ClientOrderId, string Symbol, string Side, int Quantity, decimal Price, bool SimulatePartialFill = false);
public sealed record Portfolio(decimal InvestedAmount, decimal CurrentPortfolioValue, decimal UnrealizedPnl, decimal UnrealizedPercent, decimal RealizedPnl, decimal TodayNetPnl, decimal AvailableCash, decimal TotalCharges, int OpenPositions, int ActiveOrders, decimal TotalAccountValue, decimal ReservedCash, string ValuationStatus);
