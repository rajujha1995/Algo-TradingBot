using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using TradingBot.Application;
namespace TradingBot.Infrastructure;
public sealed class BrokerInvalidScripCodesException() : InvalidOperationException("Broker rejected one or more quote instrument codes; quote availability must be checked individually.");
// Read-only by construction: no method can send an HTTP POST, PUT or DELETE.
// Raw JSON responses retain the broker's documented schema without guessed field mapping.
public sealed partial class IndstocksBrokerAdapter(HttpClient client, TradingOptions options) : ITradingBroker, IBrokerAccountReader, IClosingPriceReader
{
  readonly SemaphoreSlim requests = new(1, 1);
  DateTime lastRequest = DateTime.MinValue;
  async Task<string> GetAsync(string path, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(options.AccessToken)) throw new InvalidOperationException("INDstocks token is not configured. Read-only monitoring requires a valid backend token.");
    await requests.WaitAsync(ct);
    try
    {
      var delay = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - lastRequest); if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
      using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.indstocks.com" + path);
      request.Headers.TryAddWithoutValidation("Authorization", options.AccessToken); lastRequest = DateTime.UtcNow;
      using var response = await client.SendAsync(request, ct);
      if (response.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("Broker authentication failed. Renew your access token in backend configuration and restart. No orders were sent.");
      if (response.StatusCode == HttpStatusCode.Forbidden) throw new InvalidOperationException("Broker denied this request (HTTP 403). Check token validity and market-data permissions. No orders were sent.");
      if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new InvalidOperationException("Broker rate limit reached. Wait before requesting again.");
      if (response.StatusCode == HttpStatusCode.BadRequest && path.StartsWith("/market/quotes/", StringComparison.Ordinal)) {
        // Only this documented validation error permits splitting a read-only batch.
        // Never expose arbitrary upstream payloads or treat auth/rate/server errors as bad symbols.
        try {
          using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
          if (new[] { "debug_info", "message" }.Any(key => error.RootElement.ValueKind == JsonValueKind.Object && error.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() == "Invalid scrip codes or mode")) throw new BrokerInvalidScripCodesException();
        } catch (JsonException) { }
      }
      if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Broker request failed (HTTP {(int)response.StatusCode}). No automatic retry was performed.");
      return await response.Content.ReadAsStringAsync(ct);
    }
    finally { requests.Release(); }
  }
  async Task<JsonElement> JsonAsync(string path, CancellationToken ct)
  {
    var text = await GetAsync(path, ct); using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone();
  }
  public Task<JsonElement> GetProfileAsync(CancellationToken ct) => JsonAsync("/user/profile", ct);
  public Task<JsonElement> GetFundsAsync(CancellationToken ct) => JsonAsync("/funds", ct);
  public Task<JsonElement> GetHoldingsAsync(CancellationToken ct) => JsonAsync("/portfolio/holdings", ct);
  public Task<JsonElement> GetPositionsAsync(CancellationToken ct) => JsonAsync("/portfolio/positions", ct);
  public Task<JsonElement> GetOrderBookAsync(CancellationToken ct) => JsonAsync("/order-book", ct);
  public Task<JsonElement> GetTradeBookAsync(CancellationToken ct) => JsonAsync("/trade-book?segment=EQUITY", ct);
  public Task<JsonElement> GetDailyCandlesAsync(string code, long start, long end, CancellationToken ct) {
    ValidateCode(code);
    if (code.Contains(',') || start <= 0 || end <= start || end - start > TimeSpan.FromDays(30).TotalMilliseconds) throw new ArgumentException("Select one cash equity and a daily range up to 30 days.");
    return JsonAsync($"/market/historical/1day?scrip-codes={Uri.EscapeDataString(code)}&start_time={start}&end_time={end}", ct);
  }
  static void ValidateCode(string codes) { if (codes.Length > 8000 || codes.Split(',').Length > 500 || !Codes().IsMatch(codes)) throw new InvalidOperationException("Use documented equity scrip codes, for example NSE_3045; up to 500 comma-separated codes."); }
  [GeneratedRegex(@"^(?:NSE|BSE)_\d+(?:,(?:NSE|BSE)_\d+){0,499}$")] private static partial Regex Codes();
  public Task<JsonElement> GetQuotesAsync(string scripCodes, CancellationToken ct) { ValidateCode(scripCodes); return JsonAsync("/market/quotes/full?scrip-codes=" + Uri.EscapeDataString(scripCodes), ct); }
  public Task<JsonElement> GetCandlesAsync(string scripCode, long start, long end, CancellationToken ct)
  {
    ValidateCode(scripCode); if (scripCode.Contains(',') || start <= 0 || end <= start || end - start > TimeSpan.FromDays(30).TotalMilliseconds) throw new InvalidOperationException("Select one instrument and a historical interval of up to 30 days in epoch milliseconds.");
    return JsonAsync($"/market/historical/5minute?scrip-codes={Uri.EscapeDataString(scripCode)}&start_time={start}&end_time={end}", ct);
  }
  public Task<string> GetInstrumentsAsync(CancellationToken ct) => GetAsync("/market/instruments?source=equity", ct);
  public Task PlaceOrderAsync(CancellationToken ct) => throw new NotSupportedException("Live order submission is disabled. A verified execution adapter, reconciliation, static IP and explicit activation are required.");
}
