using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingBot.Application;
using TradingBot.Domain;
namespace TradingBot.Infrastructure;

public sealed class RealtimeResearchEngine(RealtimeJournalStore journal, ITradingBroker broker, ObservationPriceFeed feed, TradingOptions options, TimeProvider clock, ILogger<RealtimeResearchEngine> log) : BackgroundService
{
  public static readonly string[] Symbols = ["RELIANCE", "ICICIBANK", "SBIN"];
  EquityInstrument[] instruments = [];
  DateTimeOffset nextAssessment;
  long resolvedSession;
  long observedGeneration = -1;
  public async Task StartWatchingAsync(string? confirmation, CancellationToken ct)
  {
    TradingSessionPolicy.Confirm(confirmation, "START WATCHING");
    if (!TradingSessionPolicy.IsStartWindow(clock.GetUtcNow())) throw new InvalidOperationException("Start Watching is available from 7:00 AM until 6:00 PM IST.");
    if (string.IsNullOrWhiteSpace(options.AccessToken)) throw new InvalidOperationException("Configure a valid broker token before watching real data.");
    await journal.UpdateAsync(state => {
      if (state.State == "WATCHING") throw new InvalidOperationException("Watching is already active.");
      state.State = "WATCHING"; state.Reason = "Watching real broker prices and completed candles; order execution disabled.";
      state.Sessions.Add(new WatchSession { Id = state.Sessions.Select(x => x.Id).DefaultIfEmpty().Max() + 1, StartedUtc = clock.GetUtcNow() });
    }, ct);
    nextAssessment = DateTimeOffset.MinValue;
  }
  public Task StopWatchingAsync(CancellationToken ct) => journal.UpdateAsync(state => Stop(state, clock.GetUtcNow(), "Watching stopped. Reference observations retained; no order or exit assumed."), ct);
  static void Stop(RealtimeState state, DateTimeOffset now, string reason)
  {
    state.State = "IDLE"; state.Reason = reason;
    foreach (var session in state.Sessions.Where(x => x.Status == "ACTIVE")) { session.Status = "ENDED"; session.EndedUtc = now; }
    foreach (var entry in state.Entries.Where(x => x.Status == "WATCHING")) entry.Status = "PAUSED";
  }
  protected override async Task ExecuteAsync(CancellationToken ct)
  {
    while (!ct.IsCancellationRequested) {
      try { await Cycle(ct); }
      catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
      catch (Exception ex) {
        log.LogWarning("Real-time research paused for unavailable data/storage ({ErrorType})", ex.GetType().Name);
        try { await journal.UpdateAsync(s => s.Reason = "Broker data or journal unavailable; no suggestion generated from missing data. Check token/connectivity/storage.", ct); } catch (Exception) when (!ct.IsCancellationRequested) { }
      }
      await Task.Delay(TimeSpan.FromSeconds(5), ct);
    }
  }
  async Task Cycle(CancellationToken ct)
  {
    var state = await journal.ReadAsync(ct);
    if (state.State != "WATCHING") { feed.Configure([]); feed.Drain(); return; }
    var now = clock.GetUtcNow();
    var session = state.Sessions.Last(x => x.Status == "ACTIVE");
    var ist = TradingSessionPolicy.InIndia(now);
    if (ist.TimeOfDay >= new TimeSpan(18, 0, 0) || ist.Date != TradingSessionPolicy.InIndia(session.StartedUtc).Date) { await journal.UpdateAsync(s => Stop(s, now, "Daily watch window ended. Observations saved; start manually next trading day."), ct); feed.Configure([]); feed.Drain(); return; }
    if (!ObservationStrategy.NormalSession(now)) {
      feed.Configure([]); feed.Drain();
      const string waiting = "Watch enabled. Waiting for fresh data in the normal weekday cash session (09:15–15:30 IST); no observations invented outside it.";
      if (state.Reason != waiting) await journal.UpdateAsync(s => s.Reason = waiting, ct);
      return;
    }
    if (resolvedSession != session.Id) {
      var master = TopGainersService.ParseInstruments(await broker.GetInstrumentsAsync(ct));
      instruments = Symbols.Select(symbol => master.SingleOrDefault(x => x.Exchange == "NSE" && x.Symbol == symbol) ?? throw new InvalidOperationException("Research equity could not be resolved from broker master.")).ToArray();
      resolvedSession = session.Id;
    }
    feed.Configure(instruments);
    var ticks = feed.Drain();
    var generation = feed.Generation;
    var interrupted = observedGeneration >= 0 && observedGeneration != generation;
    observedGeneration = generation;
    await journal.UpdateAsync(next => {
      if (next.State != "WATCHING") return;
      foreach (var entry in next.Entries.Where(x => x.Status == "WATCHING")) {
        var freshTick = feed.Fresh(entry.Code);
        if (interrupted || freshTick == null) entry.CoverageGap = true;
        if (freshTick != null) foreach (var tick in ticks.Where(x => x.Code == entry.Code && x.Generation == generation)) ApplyPrice(entry, tick, clock.GetUtcNow().UtcDateTime);
      }
      var fresh = instruments.Count(x => feed.Fresh(x.Code) != null);
      var minute = new DateTimeOffset(now.UtcTicks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, TimeSpan.Zero);
      if (fresh > 0 && !next.Coverage.Any(x => x.MinuteUtc == minute)) next.Coverage.Add(new(minute, fresh));
    }, ct);
    if (now < nextAssessment) return;
    nextAssessment = now.AddMinutes(1);
    foreach (var instrument in instruments) {
      var response = await broker.GetCandlesAsync(instrument.Code, now.AddDays(-7).ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds(), ct);
      var decision = ObservationStrategy.Evaluate(ParseCandles(response, instrument.Code), clock.GetUtcNow());
      await journal.UpdateAsync(next => {
        if (next.State != "WATCHING") return;
        var assessedNow = clock.GetUtcNow();
        var tick = feed.Fresh(instrument.Code);
        var withinLevels = tick != null && tick.Price > decision.Stop && tick.Price < decision.Target && tick.Price <= decision.Entry + (decision.Evidence.Atr14 ?? 0) * .25m;
        var qualifies = decision.Qualifies && withinLevels && ObservationStrategy.NormalSession(assessedNow) && TradingSessionPolicy.InIndia(assessedNow).TimeOfDay < new TimeSpan(15, 15, 0);
        var reason = tick == null ? "WAIT: no fresh broker price. " + decision.Reason : decision.Qualifies && !withinLevels ? "WAIT: live price has moved outside the recorded setup's permitted reference range. " + decision.Reason : decision.Reason;
        next.Assessments.RemoveAll(x => x.Code == instrument.Code);
        next.Assessments.Add(new(instrument.Symbol, instrument.Code, clock.GetUtcNow(), decision.CandleCloseUtc, qualifies, reason, decision.Evidence));
        // One independently watched suggestion per symbol/day; no financed portfolio or fills.
        if (qualifies && !next.Entries.Any(x => x.Code == instrument.Code && TradingSessionPolicy.InIndia(x.SuggestedUtc).Date == ist.Date)) next.Entries.Add(new WatchEntry {
          Id = next.Entries.Select(x => x.Id).DefaultIfEmpty().Max() + 1, Symbol = instrument.Symbol, Code = instrument.Code,
          SuggestedUtc = clock.GetUtcNow(), CandleCloseUtc = decision.CandleCloseUtc!.Value, ReferencePrice = tick!.Price, ReferenceUtc = new DateTimeOffset(tick.Utc, TimeSpan.Zero),
          LatestPrice = tick.Price, PriceUtc = new DateTimeOffset(tick.Utc, TimeSpan.Zero), ObservedMove = 0, Stop = decision.Stop, Target = decision.Target, Reason = reason, Evidence = decision.Evidence
        });
        next.Reason = "Watching real data. " + feed.Status + ". Suggestions and price changes are saved to JSON; no orders are placed.";
      }, ct);
    }
  }
  public static void ApplyPrice(WatchEntry entry, ResearchTick tick, DateTime now)
  {
    if (entry.Status != "WATCHING" || tick.Code != entry.Code || !ObservationPriceFeed.IsFresh(tick.Utc, tick.ReceivedUtc, now) || tick.Utc < entry.ReferenceUtc.UtcDateTime || (entry.PriceUtc != null && tick.Utc <= entry.PriceUtc.Value.UtcDateTime)) return;
    entry.LatestPrice = tick.Price; entry.PriceUtc = new DateTimeOffset(tick.Utc, TimeSpan.Zero);
    entry.ObservedMove = (tick.Price - entry.ReferencePrice) * entry.Quantity;
    if (tick.Price <= entry.Stop) entry.StopObservedUtc ??= entry.PriceUtc;
    if (tick.Price >= entry.Target) entry.TargetObservedUtc ??= entry.PriceUtc;
  }
  public static IReadOnlyList<Candle> ParseCandles(JsonElement response, string code)
  {
    if (!response.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True || !response.TryGetProperty("data", out var data)) throw new InvalidOperationException("Unsuccessful broker candle response.");
    if (!data.TryGetProperty(code, out var row) || !row.TryGetProperty("candles", out var candles) || candles.ValueKind != JsonValueKind.Array) return [];
    return candles.EnumerateArray().Select(x => new Candle(DateTimeOffset.FromUnixTimeSeconds(x.GetProperty("ts").GetInt64()).UtcDateTime, x.GetProperty("o").GetDecimal(), x.GetProperty("h").GetDecimal(), x.GetProperty("l").GetDecimal(), x.GetProperty("c").GetDecimal(), x.GetProperty("v").GetInt64())).ToList();
  }
}
