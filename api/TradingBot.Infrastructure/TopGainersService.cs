using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using TradingBot.Application;
namespace TradingBot.Infrastructure;

public sealed record EquityInstrument(string Code, string Symbol, string Exchange, string Isin);
public sealed record Gainer(string Symbol, string Exchange, string Code, decimal Price, decimal ChangePercent, decimal Volume);
public sealed record GainersSnapshot(IReadOnlyList<Gainer> Items, int UniverseCount, int QuotedCount, DateTimeOffset ScanStartedUtc, DateTimeOffset FetchedUtc, string Scope, string Freshness);

public sealed class TopGainersService(ITradingBroker broker, TimeProvider clock)
{
  readonly SemaphoreSlim gate = new(1, 1);
  GainersSnapshot? cached;
  public async Task<GainersSnapshot> GetAsync(CancellationToken ct)
  {
    await gate.WaitAsync(ct);
    try {
      if (cached != null && clock.GetUtcNow() - cached.FetchedUtc < TimeSpan.FromMinutes(5)) return cached;
      var started = clock.GetUtcNow();
      var instruments = ParseInstruments(await broker.GetInstrumentsAsync(ct));
      if (instruments.Count == 0) throw new InvalidOperationException("Broker instrument master contains no supported cash equities. Gainers are unavailable.");
      var all = new List<Gainer>();
      var quoted = 0;
      foreach (var batch in instruments.Chunk(200)) {
        var response = await broker.GetQuotesAsync(string.Join(',', batch.Select(x => x.Code)), ct);
        var result = RankQuotes(batch, response);
        quoted += result.Quoted;
        all.AddRange(result.Items);
      }
      if (quoted == 0) throw new InvalidOperationException("No usable equity quotes returned. Gainers are unavailable.");
      cached = new(all.OrderByDescending(x => x.ChangePercent).ThenBy(x => x.Symbol, StringComparer.Ordinal).Take(10).ToList(), instruments.Count, quoted, started, clock.GetUtcNow(),
        "NSE/BSE cash EQUITY across market caps, no cap filter; one listing per ISIN, NSE preferred. Unclassified instrument rows excluded.",
        "Broker snapshot; quote trading date and per-symbol freshness are not supplied. May reflect the previous session before market open or on holidays. Refresh manually; server reuses scans for 5 minutes. Not trading recommendations.");
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
      if (change > 0) items.Add(new(instrument.Symbol, instrument.Exchange, instrument.Code, price.Value, change, volume.Value));
    }
    return (items, quoted);
  }
}
