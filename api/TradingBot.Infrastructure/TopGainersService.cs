using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using TradingBot.Application;
namespace TradingBot.Infrastructure;

public sealed record EquityInstrument(string Code, string Symbol, string Exchange, string Isin);
public sealed record Gainer(string Symbol, string Exchange, string Code, decimal Price, decimal ChangePercent, decimal Volume, decimal? PreviousClose = null, decimal? TodayClose = null, string? CloseDate = null, string CloseStatus = "Not verified", decimal? LatestClose = null, string? LatestCloseDate = null);
public sealed record GainersSnapshot(IReadOnlyList<Gainer> Items, int UniverseCount, int QuotedCount, DateTimeOffset ScanStartedUtc, DateTimeOffset FetchedUtc, string Scope, string Freshness, int RejectedCount = 0, int UnresolvedCount = 0);

public sealed class TopGainersService(ITradingBroker broker, TimeProvider clock, IClosingPriceReader? history = null)
{
  readonly SemaphoreSlim gate = new(1, 1);
  GainersSnapshot? cached;
  readonly Dictionary<string, DateTimeOffset> rejectedUntil = new(StringComparer.Ordinal);
  public async Task<GainersSnapshot> GetAsync(CancellationToken ct)
  {
    await gate.WaitAsync(ct);
    try {
      if (cached != null && clock.GetUtcNow() - cached.FetchedUtc < TimeSpan.FromMinutes(1)) return cached;
      var started = clock.GetUtcNow();
      var instruments = ParseInstruments(await broker.GetInstrumentsAsync(ct));
      if (instruments.Count == 0) throw new InvalidOperationException("Broker instrument master contains no supported cash equities. Gainers are unavailable.");
      var all = new List<Gainer>();
      var quoted = 0;
      var rejected = 0; var unresolved = 0; var subdivisionsRemaining = 64;
      foreach (var code in rejectedUntil.Where(x => x.Value <= started).Select(x => x.Key).ToArray()) rejectedUntil.Remove(code);
      foreach (var batch in instruments.Where(x => !rejectedUntil.ContainsKey(x.Code)).Chunk(200)) await ReadBatch(batch, false);
      rejected += instruments.Count(x => rejectedUntil.ContainsKey(x.Code));

      async Task ReadBatch(EquityInstrument[] batch, bool subdivision) {
        ct.ThrowIfCancellationRequested();
        if (subdivision && subdivisionsRemaining-- <= 0) { unresolved += batch.Length; return; }
        try {
          var response = await broker.GetQuotesAsync(string.Join(',', batch.Select(x => x.Code)), ct);
          var result = RankQuotes(batch, response);
          quoted += result.Quoted; all.AddRange(result.Items);
        } catch (BrokerInvalidScripCodesException) {
          if (batch.Length == 1) { rejectedUntil[batch[0].Code] = clock.GetUtcNow().AddHours(1); return; }
          var middle = batch.Length / 2;
          await ReadBatch(batch[..middle], true);
          await ReadBatch(batch[middle..], true);
        }
      }
      if (quoted == 0) throw new InvalidOperationException("No usable equity quotes returned. Gainers are unavailable.");
      var leaders = all.OrderByDescending(x => x.ChangePercent).ThenBy(x => x.Symbol, StringComparer.Ordinal).Take(10).ToList();
      for (var i = 0; i < leaders.Count; i++) {
        var now = clock.GetUtcNow();
        if (history == null) { leaders[i] = leaders[i] with { CloseStatus = "Daily close unavailable" }; continue; }
        try {
          var daily = await history.GetDailyCandlesAsync(leaders[i].Code, now.AddDays(-7).ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds(), ct);
          var close = ClosingPrice(daily, leaders[i].Code, now);
          var today = TradingSessionPolicy.InIndia(now).Date;
          var latest = RealtimeResearchEngine.ParseCandles(daily, leaders[i].Code).Where(x => x.Close > 0 && x.Volume > 0 && (TradingSessionPolicy.InIndia(new DateTimeOffset(x.TimestampUtc, TimeSpan.Zero)).Date < today || (TradingSessionPolicy.InIndia(new DateTimeOffset(x.TimestampUtc, TimeSpan.Zero)).Date == today && AfterNormalClose(now)))).OrderByDescending(x => x.TimestampUtc).FirstOrDefault();
          leaders[i] = leaders[i] with { TodayClose = close.Price, CloseDate = close.Date, LatestClose = latest?.Close, LatestCloseDate = latest == null ? null : TradingSessionPolicy.InIndia(new DateTimeOffset(latest.TimestampUtc, TimeSpan.Zero)).ToString("yyyy-MM-dd"), CloseStatus = close.Price == null ? "Today's dated close pending / unavailable" : "Broker daily close; not exchange-certified" };
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { leaders[i] = leaders[i] with { CloseStatus = "Daily close unavailable; not replaced with LTP" }; }
      }
      cached = new(leaders, instruments.Count, quoted, started, clock.GetUtcNow(),
        "NSE/BSE cash EQUITY across market caps, no cap filter; one listing per ISIN, NSE preferred. Unclassified instrument rows excluded.",
        "Quotes are timestamped fetch snapshots, not a streamed market-wide ranking. Today's close uses a dated broker 1day candle after 16:00 IST on normal weekdays; it is not an exchange-certified official close and may be revised. Missing closes remain unavailable. Server scan cache: one minute. Quote validation subdivisions are bounded; rejected codes are rechecked after one hour.", rejected, unresolved);
      return cached;
    } finally { gate.Release(); }
  }

  public static IReadOnlyList<EquityInstrument> ParseInstruments(string csv, bool deduplicateListings = true)
  {
    using var parser = new TextFieldParser(new StringReader(csv));
    parser.SetDelimiters(","); parser.HasFieldsEnclosedInQuotes = true;
    var headers = parser.ReadFields()?.Select(x => x.Trim().TrimStart('\uFEFF').ToUpperInvariant()).ToArray() ?? [];
    string[] required = ["EXCH", "SEGMENT", "SECURITY_ID", "INSTRUMENT_NAME", "TRADING_SYMBOL", "ISIN"];
    if (required.Any(x => !headers.Contains(x))) throw new InvalidOperationException("Broker instrument CSV columns differ from the documented equity schema.");
    var rows = new List<EquityInstrument>();
    while (!parser.EndOfData) {
      var fields = parser.ReadFields();
      if (fields == null || fields.Length != headers.Length) throw new InvalidOperationException("Broker instrument CSV has an invalid row; no ranking was published.");
      string Value(string name) => fields[Array.IndexOf(headers, name)].Trim();
      var exchange = Value("EXCH"); var id = Value("SECURITY_ID");
      if (exchange is not ("NSE" or "BSE") || Value("SEGMENT") != "E" || Value("INSTRUMENT_NAME") != "EQUITY") continue;
      if (id.Length == 0 || id.Any(c => c is < '0' or > '9') || Value("TRADING_SYMBOL").Length == 0) continue;
      rows.Add(new(exchange + "_" + id, Value("TRADING_SYMBOL"), exchange, Value("ISIN")));
    }
    if (!deduplicateListings) return rows.DistinctBy(x => x.Code).OrderBy(x => x.Code, StringComparer.Ordinal).ToList();
    return rows.GroupBy(x => x.Isin.Length > 0 ? x.Isin : x.Code, StringComparer.Ordinal)
      .Select(group => group.OrderBy(x => x.Exchange == "NSE" ? 0 : 1).ThenBy(x => x.Code, StringComparer.Ordinal).First())
      .DistinctBy(x => x.Code).OrderBy(x => x.Code, StringComparer.Ordinal).ToList();
  }

  public static (IReadOnlyList<Gainer> Items, int Quoted) RankQuotes(IEnumerable<EquityInstrument> instruments, JsonElement response)
  {
    if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() != "success" || !response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
      throw new InvalidOperationException("Broker quote response was unsuccessful or invalid; no ranking was published.");
    var items = new List<Gainer>(); var quoted = 0;
    foreach (var instrument in instruments) {
      if (!data.TryGetProperty(instrument.Code, out var quote) || quote.ValueKind != JsonValueKind.Object) continue;
      decimal? Number(string key) => quote.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;
      var price = Number("live_price"); var previous = Number("prev_close"); var volume = Number("volume");
      if (price is null or <= 0 || previous is null or <= 0 || volume is null or <= 0) continue;
      quoted++;
      var change = (price.Value - previous.Value) / previous.Value * 100m;
      if (change > 0) items.Add(new(instrument.Symbol, instrument.Exchange, instrument.Code, price.Value, change, volume.Value, previous));
    }
    return (items, quoted);
  }
  public static bool AfterNormalClose(DateTimeOffset now) {
    var ist = TradingSessionPolicy.InIndia(now);
    return ist.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && ist.TimeOfDay >= TimeSpan.FromHours(16);
  }
  public static (decimal? Price, string? Date) ClosingPrice(JsonElement response, string code, DateTimeOffset now) {
    if (!AfterNormalClose(now)) return (null, null);
    var today = TradingSessionPolicy.InIndia(now).Date;
    var bars = RealtimeResearchEngine.ParseCandles(response, code);
    var bar = bars.Where(x => TradingSessionPolicy.InIndia(new DateTimeOffset(x.TimestampUtc, TimeSpan.Zero)).Date == today && x.Close > 0 && x.Volume > 0).OrderByDescending(x => x.TimestampUtc).FirstOrDefault();
    return bar == null ? (null, null) : (bar.Close, today.ToString("yyyy-MM-dd"));
  }
}
