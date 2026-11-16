using System.Text.Json;
using Microsoft.Extensions.Hosting;
using TradingBot.Application;
namespace TradingBot.Infrastructure;

public sealed record AccountSection(JsonElement? Data, DateTimeOffset? FetchedUtc, string? Error);
public sealed record BrokerHoldingMark(string Symbol, string? Code, decimal? Quantity, decimal? AveragePrice, decimal? Price, DateTime? PriceUtc, decimal? MarketValue, decimal? UnrealizedPnl, bool Fresh, string Note);

public sealed class BrokerAccountMonitor(ITradingBroker broker, IBrokerAccountReader reader, ObservationPriceFeed prices, BrokerOrderFeed orders, TradingOptions options, TimeProvider clock, RealtimeJournalStore? journal = null) : BackgroundService
{
  readonly object sync = new();
  readonly Dictionary<string, AccountSection> sections = new();
  IReadOnlyList<EquityInstrument> master = [];
  DateTimeOffset masterFetched;
  CancellationTokenSource? monitorCancellation;
  long epoch;
  int refreshRequested = 1;
  bool subscribed;
  volatile bool enabled;
  string mappingStatus = "Not resolved";
  public bool Enabled => enabled;
  public void SetEnabled(bool value)
  {
    if (value && string.IsNullOrWhiteSpace(options.AccessToken)) throw new InvalidOperationException("Configure a broker token in api/.env.production before starting account monitoring.");
    lock (sync) {
      if (value == enabled) return;
      enabled = value; epoch++;
      monitorCancellation?.Cancel(); monitorCancellation = value ? new CancellationTokenSource() : null;
      if (!value) prices.ConfigureFor("account", []);
      orders.Enable(value); Interlocked.Exchange(ref refreshRequested, 1);
    }
  }
  public object Snapshot()
  {
    lock (sync) {
      var holdingRows = Rows(sections.GetValueOrDefault("holdings")?.Data);
      var marks = holdingRows.Select(row => Mark(row, master, code => enabled ? prices.Fresh(code) : null)).ToArray();
      var positionMarks = Rows(sections.GetValueOrDefault("positions")?.Data).Select(row => Mark(row, master, code => enabled ? prices.Fresh(code) : null, "net_qty")).ToArray();
      var watchlist = master.Where(x => x.Exchange == "NSE" && RealtimeResearchEngine.Symbols.Contains(x.Symbol)).Select(x => { var tick = enabled ? prices.Fresh(x.Code) : null; return new { x.Symbol, x.Code, price = tick?.Price, priceUtc = tick?.Utc, fresh = tick != null }; }).ToArray();
      return new { enabled, serverUtc = clock.GetUtcNow(), sections = new Dictionary<string, AccountSection>(sections), holdingMarks = marks, positionMarks, watchlist, priceFeed = prices.Status, orderFeed = orders.Snapshot(), mappingStatus,
        policy = "Read-only. Prices and order events use WebSockets; account sections refresh about every 30 seconds and after order events. All sections retain their own fetch times. Missing/stale prices are not replaced with zero. No broker orders can be submitted here. Stopping the watch does not cancel orders placed in your broker account elsewhere." };
    }
  }

  protected override async Task ExecuteAsync(CancellationToken ct)
  {
    if (!subscribed) { orders.ReconcileRequested += RequestRefresh; subscribed = true; }
    var next = DateTimeOffset.MinValue;
    while (!ct.IsCancellationRequested) {
      if (enabled && (Interlocked.Exchange(ref refreshRequested, 0) == 1 || clock.GetUtcNow() >= next)) {
        next = clock.GetUtcNow().AddSeconds(30);
        CancellationToken token; long current;
        lock (sync) { token = monitorCancellation?.Token ?? ct; current = epoch; }
        using var cycle = CancellationTokenSource.CreateLinkedTokenSource(ct, token);
        try { await Refresh(current, cycle.Token); }
        catch (OperationCanceledException) when (cycle.IsCancellationRequested) { }
        catch (Exception) { lock (sync) mappingStatus = "Account refresh failed; retained snapshots may be stale. Check token/connection."; }
      }
      await Task.Delay(TimeSpan.FromSeconds(1), ct);
    }
  }
  void RequestRefresh() => Interlocked.Exchange(ref refreshRequested, 1);

  async Task Refresh(long current, CancellationToken ct)
  {
    async Task<bool> Fetch(string key, Func<CancellationToken, Task<JsonElement>> get, JsonValueKind kind)
    {
      try {
        var response = await get(ct); Validate(response, kind);
        lock (sync) {
          if (!enabled || current != epoch) return false;
          sections[key] = new(response.Clone(), clock.GetUtcNow(), null);
          return true;
        }
      } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
      catch (Exception) {
        lock (sync) if (enabled && current == epoch) { var previous = sections.GetValueOrDefault(key); sections[key] = new(previous?.Data, previous?.FetchedUtc, "Broker refresh failed or response invalid; check token/connection. Previous data, if any, is stale."); }
        return false;
      }
    }
    await Fetch("funds", broker.GetFundsAsync, JsonValueKind.Object);
    await Fetch("holdings", broker.GetHoldingsAsync, JsonValueKind.Array);
    await Fetch("positions", broker.GetPositionsAsync, JsonValueKind.Array);
    var generation = orders.Generation;
    if (await Fetch("orders", reader.GetOrderBookAsync, JsonValueKind.Array)) orders.AcknowledgeReconciliation(generation);
    if (await Fetch("trades", reader.GetTradeBookAsync, JsonValueKind.Array) && journal != null) {
      JsonElement[] fills;
      lock (sync) fills = Rows(sections.GetValueOrDefault("trades")?.Data);
      await journal.UpdateAsync(state => {
        foreach (var fill in fills.Where(x => x.TryGetProperty("fill_id", out _) && x.TryGetProperty("trade_date", out _))) {
          var id = fill.GetProperty("fill_id").ToString(); var date = fill.GetProperty("trade_date").ToString();
          if (!state.BrokerFills.Any(x => x.GetProperty("fill_id").ToString() == id && x.GetProperty("trade_date").ToString() == date)) state.BrokerFills.Add(fill.Clone());
        }
      }, ct);
    }
    try {
      if (master.Count == 0 || clock.GetUtcNow() - masterFetched > TimeSpan.FromHours(6)) {
        var resolved = TopGainersService.ParseInstruments(await broker.GetInstrumentsAsync(ct), deduplicateListings: false);
        lock (sync) if (enabled && current == epoch) { master = resolved; masterFetched = clock.GetUtcNow(); }
      }
      lock (sync) {
        if (!enabled || current != epoch) return;
        var holdings = Rows(sections.GetValueOrDefault("holdings")?.Data);
        var positions = Rows(sections.GetValueOrDefault("positions")?.Data);
        var selected = holdings.Concat(positions).Select(row => Resolve(row, master)).Where(x => x != null).Cast<EquityInstrument>()
          .Concat(master.Where(x => x.Exchange == "NSE" && RealtimeResearchEngine.Symbols.Contains(x.Symbol))).DistinctBy(x => x.Code).Take(500).ToArray();
        prices.ConfigureFor("account", selected);
        mappingStatus = $"Resolved {selected.Length} cash-equity price subscriptions (maximum 500). Ambiguous tokens or unresolved IDs remain unpriced; no exchange is guessed. Non-equity positions are read-only snapshots.";
      }
    } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    catch (Exception) { lock (sync) mappingStatus = "Instrument mapping failed. No unverified instrument IDs were subscribed."; }
  }

  public static void Validate(JsonElement response, JsonValueKind kind)
  {
    if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() != "success" || !response.TryGetProperty("data", out var data) || data.ValueKind != kind) throw new InvalidOperationException("Invalid account response");
  }
  public static JsonElement[] Rows(JsonElement? response) => response is JsonElement r && r.ValueKind == JsonValueKind.Object && r.TryGetProperty("data", out var rows) && rows.ValueKind == JsonValueKind.Array ? rows.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToArray() : [];
  static string? String(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
  static decimal? Number(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;
  public static EquityInstrument? Resolve(JsonElement row, IReadOnlyList<EquityInstrument> instruments)
  {
    var id = String(row, "security_id"); var isin = String(row, "isin"); var exchange = String(row, "exchange");
    if (String(row, "segment") is string segment && segment != "EQUITY") return null;
    if (string.IsNullOrWhiteSpace(id)) return null;
    var matches = instruments.Where(x => x.Code.Split('_')[1] == id && (string.IsNullOrWhiteSpace(exchange) || x.Exchange == exchange) && (string.IsNullOrWhiteSpace(isin) || x.Isin == isin)).ToList();
    return matches.Count == 1 ? matches[0] : null;
  }
  public static BrokerHoldingMark Mark(JsonElement row, IReadOnlyList<EquityInstrument> instruments, Func<string, ResearchTick?> quote, string quantityField = "total_qty")
  {
    var instrument = Resolve(row, instruments); var tick = instrument == null ? null : quote(instrument.Code);
    var quantity = Number(row, quantityField); var average = Number(row, "avg_price");
    return new(String(row, "symbol") ?? "Unlabelled", instrument?.Code, quantity, average, tick?.Price, tick?.Utc,
      tick == null || quantity == null ? null : tick.Price * quantity,
      tick == null || quantity == null || average == null ? null : (tick.Price - average) * quantity,
      tick != null, instrument == null ? "Unresolved/ambiguous broker instrument; no price guessed" : tick == null ? "No fresh timestamped LTP (15-second limit)" : "Price × broker snapshot quantity; P&L versus reported average, before exit costs/corporate-action adjustments");
  }
}
