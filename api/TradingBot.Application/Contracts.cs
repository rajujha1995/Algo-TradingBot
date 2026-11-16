using System.Text.Json;
namespace TradingBot.Application;
public interface IClosingPriceReader
{
  Task<JsonElement> GetDailyCandlesAsync(string code, long start, long end, CancellationToken ct);
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
