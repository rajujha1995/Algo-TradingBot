using System.Text.Json;
using TradingBot.Domain;
namespace TradingBot.Application;
// All callers, including hosted workers, use the same process-wide gate.
// A database lock additionally prevents two API processes owning the same environment.
public sealed class ExecutionGate { public SemaphoreSlim Mutex { get; } = new(1, 1); }
public interface ITradingStore
{
  Task InitializeAsync(CancellationToken ct = default);
  Task<TradingControlState> GetStateAsync(CancellationToken ct = default);
  Task<Portfolio> GetPortfolioAsync(CancellationToken ct = default);
  Task<IReadOnlyList<Order>> GetOrdersAsync(CancellationToken ct = default);
  Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default);
  Task<IReadOnlyList<Trade>> GetTradesAsync(CancellationToken ct = default);
  Task<IReadOnlyList<SystemEvent>> GetEventsAsync(CancellationToken ct = default);
  Task<TradingControlState> StopAsync(CancellationToken ct = default);
  Task<TradingControlState> RecoverAsync(string confirmation, CancellationToken ct = default);
  Task<Order> SubmitPaperAsync(PaperOrderRequest request, CancellationToken ct = default);
  Task CompletePendingAsync(CancellationToken ct = default);
}
public interface ITradingBroker
{
  Task<JsonElement> GetProfileAsync(CancellationToken ct);
  Task<JsonElement> GetFundsAsync(CancellationToken ct);
  Task<JsonElement> GetHoldingsAsync(CancellationToken ct);
  Task<JsonElement> GetPositionsAsync(CancellationToken ct);
  Task<JsonElement> GetQuotesAsync(string scripCodes, CancellationToken ct);
  Task<JsonElement> GetCandlesAsync(string scripCode, long start, long end, CancellationToken ct);
  Task<string> GetInstrumentsAsync(CancellationToken ct);
  Task PlaceOrderAsync(CancellationToken ct);
}
public interface IBrokerAccountReader
{
  Task<JsonElement> GetOrderBookAsync(CancellationToken ct);
  Task<JsonElement> GetTradeBookAsync(CancellationToken ct);
}
public sealed class TradingRiskService(TradingOptions options)
{
  public void CheckBuy(decimal cash, decimal reserved, decimal exposure, int openPositions, bool pendingBuy, decimal totalCost, decimal dailyNet, decimal totalNet)
  {
    if (dailyNet <= -options.DailyLossLimit || totalNet <= -options.TotalLossLimit) throw new InvalidOperationException("Loss budget exhausted.");
    if (pendingBuy || openPositions >= options.MaxOpenPositions) throw new InvalidOperationException("A position or pending buy already exists.");
    if (totalCost <= 0 || totalCost > cash - reserved || totalCost + exposure + reserved > options.CapitalLimit) throw new InvalidOperationException("Insufficient funds or capital limit exceeded including charges and pending commitments.");
  }
}
// Indicative NSE delivery model, not a contract note or tax engine. Values are documented.
public static class TradingCosts
{
  public static decimal Estimate(decimal turnover, bool sell)
  {
    if (turnover <= 0) throw new ArgumentOutOfRangeException(nameof(turnover));
    var exchange = turnover * 0.0000297m;
    var sebi = turnover * 0.000001m;
    var stt = turnover * 0.001m;
    var stamp = sell ? 0 : turnover * 0.00015m;
    return decimal.Round(exchange + sebi + stt + stamp + (exchange + sebi) * 0.18m + (sell ? 15.93m : 0m), 2, MidpointRounding.AwayFromZero);
  }
}
