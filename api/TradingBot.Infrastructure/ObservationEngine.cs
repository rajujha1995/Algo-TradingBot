using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingBot.Application;
using TradingBot.Domain;
namespace TradingBot.Infrastructure;

public sealed class ObservationEngine(IDbContextFactory<TradingDbContext> factory, ExecutionGate gate, ITradingBroker broker, ObservationPriceFeed feed, TradingOptions options, TimeProvider clock, ILogger<ObservationEngine> log) : BackgroundService
{
  public static readonly string[] Symbols = ["RELIANCE", "ICICIBANK", "SBIN"];
  EquityInstrument[] instruments = [];
  long resolvedSession;
  DateTime nextAnalysis = DateTime.MinValue;
  public async Task<ResearchSession> StartAsync(string? confirmation, CancellationToken ct)
  {
    TradingSessionPolicy.CheckStart(clock.GetUtcNow(), confirmation);
    if (string.IsNullOrWhiteSpace(options.AccessToken)) throw new InvalidOperationException("Configure a valid INDstocks token in the backend to start real-data observation. No synthetic data will be substituted.");
    await gate.Mutex.WaitAsync(ct);
    try {
      await using var db = await factory.CreateDbContextAsync(ct);
      await using var tx = await db.Database.BeginTransactionAsync(ct);
      var state = await db.ControlStates.SingleAsync(ct);
      if (state.State is EngineState.PAPER_RUNNING or EngineState.LIVE_RUNNING or EngineState.OBSERVING) throw new InvalidOperationException("A session is already running. Stop it explicitly before starting another.");
      var session = new ResearchSession { StartedUtc = clock.GetUtcNow().UtcDateTime };
      db.ResearchSessions.Add(session);
      state.State = EngineState.OBSERVING; state.UpdatedUtc = clock.GetUtcNow().UtcDateTime; state.Reason = "Real broker data observation only. Ten-share what-if scenarios; no money allocated and no broker orders.";
      db.Events.Add(new SystemEvent { Category = "Research", Message = "START TRADING confirmed: observation session started; execution disabled." });
      await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
      nextAnalysis = DateTime.MinValue;
      return session;
    } finally { gate.Mutex.Release(); }
  }

  protected override async Task ExecuteAsync(CancellationToken ct)
  {
    while (!ct.IsCancellationRequested) {
      try {
        var active = await ProcessTicks(ct);
        if (active != null && clock.GetUtcNow().UtcDateTime >= nextAnalysis) {
          nextAnalysis = clock.GetUtcNow().UtcDateTime.AddSeconds(60);
          await Analyze(active, ct);
        }
      } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
      catch (Exception ex) {
        log.LogWarning("Observation iteration failed ({ErrorType}); no synthetic observations substituted", ex.GetType().Name);
        await Message("Broker data or database unavailable; check the token via Settings. Waiting without generating suggestions from missing data.", ct);
      }
      await Task.Delay(TimeSpan.FromSeconds(1), ct);
    }
  }

  async Task<ResearchSession?> ProcessTicks(CancellationToken ct)
  {
    await gate.Mutex.WaitAsync(ct);
    try {
      await using var db = await factory.CreateDbContextAsync(ct);
      if ((await db.ControlStates.SingleAsync(ct)).State != EngineState.OBSERVING) { feed.Configure([]); feed.Drain(); return null; }
      var session = await db.ResearchSessions.SingleOrDefaultAsync(x => x.Status == "ACTIVE", ct);
      if (session == null) { feed.Configure([]); return null; }
      var now = clock.GetUtcNow(); var ist = TradingSessionPolicy.InIndia(now);
      var scenarios = await db.ResearchSuggestions.Where(x => x.SessionId == session.Id && (x.Status == "OPEN" || x.Status == "PENDING_ENTRY")).ToListAsync(ct);
      if (ist.TimeOfDay >= new TimeSpan(15, 30, 0) || ist.Date != TradingSessionPolicy.InIndia(new DateTimeOffset(session.StartedUtc, TimeSpan.Zero)).Date) {
        session.Status = "FINISHED"; session.EndedUtc = now.UtcDateTime; session.Message = "Normal cash session ended. Unobserved exits remain uncertain; restart observation next trading day.";
        foreach (var s in scenarios) Invalidate(s, "Session ended without an observed exit. No closing price or fill invented.");
        var control = await db.ControlStates.SingleAsync(ct); control.State = EngineState.STOPPED; control.Reason = session.Message; control.UpdatedUtc = now.UtcDateTime;
        feed.Configure([]); feed.Drain(); await db.SaveChangesAsync(ct); return null;
      }
      if (!ObservationStrategy.NormalSession(now)) feed.Configure([]);
      foreach (var s in scenarios) {
        if (s.FeedGeneration != feed.Generation || (s.Status == "OPEN" && (s.MarkUtc == null || now.UtcDateTime - s.MarkUtc > TimeSpan.FromSeconds(15)))) Invalidate(s, "Feed continuity lost or mark older than 15 seconds; missed stop/target outcomes cannot be inferred.");
        else if (s.Status == "PENDING_ENTRY" && now.UtcDateTime - s.SuggestedUtc > TimeSpan.FromMinutes(2)) { s.Status = "EXPIRED"; s.OutcomeNote = "No eligible fresh post-suggestion tick within two minutes."; }
      }
      foreach (var tick in feed.Drain()) {
        if (!ObservationPriceFeed.IsFresh(tick.Utc, tick.ReceivedUtc, now.UtcDateTime) || tick.Generation != feed.Generation) continue;
        foreach (var s in scenarios.Where(x => x.Code == tick.Code && x.Status is "PENDING_ENTRY" or "OPEN")) ApplyTick(s, tick, now.UtcDateTime, options.SlippageBps);
      }
      var fresh = instruments.Count(x => feed.Fresh(x.Code) != null);
      if (fresh > 0) {
        var minute = new DateTime(now.UtcDateTime.Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var coverage = await db.ResearchCoverage.SingleOrDefaultAsync(x => x.SessionId == session.Id && x.MinuteUtc == minute, ct);
        if (coverage == null) db.ResearchCoverage.Add(new ResearchCoverage { SessionId = session.Id, MinuteUtc = minute, FreshSymbols = fresh });
        else coverage.FreshSymbols = Math.Max(coverage.FreshSymbols, fresh);
      }
      await db.SaveChangesAsync(ct); return session;
    } finally { gate.Mutex.Release(); }
  }

  static void Invalidate(ResearchSuggestion s, string reason) { s.Status = s.Status == "OPEN" ? "UNCERTAIN" : "CANCELLED"; s.OutcomeNote = reason + " Last mark is not realized P&L."; }

  public static void ApplyTick(ResearchSuggestion s, ResearchTick tick, DateTime now, decimal slippageBps)
  {
    if (s.Code != tick.Code || tick.Generation != s.FeedGeneration || !ObservationPriceFeed.IsFresh(tick.Utc, tick.ReceivedUtc, now)) return;
    var cutoff = TradingSessionPolicy.InIndia(new DateTimeOffset(tick.Utc, TimeSpan.Zero)).TimeOfDay >= new TimeSpan(15, 20, 0);
    if (s.Status == "PENDING_ENTRY") {
      if (tick.Utc <= s.SuggestedUtc) return; // Never fill at the candle that produced the signal.
      var entry = tick.Price * (1 + slippageBps / 10000m);
      if (cutoff || tick.Utc > s.SuggestedUtc.AddMinutes(2) || entry > s.EntryLimit || entry <= s.Stop || ObservationStrategy.Net(entry, s.Target, s.Quantity) < 1.5m * Math.Abs(ObservationStrategy.Net(entry, s.Stop, s.Quantity))) { s.Status = "SKIPPED"; s.OutcomeNote = "Next observed price failed entry distance, time or net reward/risk checks; no assumed fill."; return; }
      s.EntryPrice = entry; s.EntryUtc = tick.Utc; s.Status = "OPEN";
      s.OutcomeNote = "Assumed full fill at next received LTP plus configured adverse slippage. Order-book spread, queue position and market impact are unverified.";
    }
    if (s.Status != "OPEN" || s.EntryPrice == null || (s.MarkUtc != null && tick.Utc <= s.MarkUtc)) return;
    s.LastPrice = tick.Price; s.MarkUtc = tick.Utc;
    var hypotheticalExit = tick.Price * (1 - slippageBps / 10000m);
    s.GrossPnl = (hypotheticalExit - s.EntryPrice.Value) * s.Quantity;
    s.EstimatedCosts = ObservationStrategy.Fees(s.EntryPrice.Value, hypotheticalExit, s.Quantity);
    s.NetPnl = s.GrossPnl - s.EstimatedCosts;
    s.MaxFavorable = Math.Max(s.MaxFavorable, (tick.Price - s.EntryPrice.Value) * s.Quantity);
    s.MaxAdverse = Math.Min(s.MaxAdverse, (tick.Price - s.EntryPrice.Value) * s.Quantity);
    if (tick.Price <= s.Stop || tick.Price >= s.Target || cutoff) {
      s.Status = tick.Price <= s.Stop ? "EXIT_STOP" : tick.Price >= s.Target ? "EXIT_TARGET" : "EXIT_TIME";
      s.ExitPrice = hypotheticalExit; s.ExitUtc = tick.Utc;
      s.OutcomeNote = "Hypothetical same-day exit at first received trigger LTP minus adverse slippage; not a broker fill. Gaps may exceed the planned stop. Fees are indicative NSE cash intraday estimates, including assumed brokerage; actual contract-note rounding may differ.";
    }
  }

  async Task Analyze(ResearchSession session, CancellationToken ct)
  {
    if (!ObservationStrategy.NormalSession(clock.GetUtcNow())) { await Message("Armed for observation; waiting for normal weekday cash hours and fresh broker trades. No holiday/special-session trading is inferred.", ct); return; }
    if (resolvedSession != session.Id) {
      var master = TopGainersService.ParseInstruments(await broker.GetInstrumentsAsync(ct));
      instruments = Symbols.Select(symbol => master.SingleOrDefault(x => x.Exchange == "NSE" && x.Symbol == symbol) ?? throw new InvalidOperationException("Required NSE equity is missing or ambiguous in broker master.")).ToArray();
      resolvedSession = session.Id;
    }
    feed.Configure(instruments);
    foreach (var instrument in instruments) {
      var now = clock.GetUtcNow();
      var response = await broker.GetCandlesAsync(instrument.Code, now.AddDays(-7).ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds(), ct);
      var candles = ParseCandles(response, instrument.Code);
      now = clock.GetUtcNow();
      var decision = ObservationStrategy.Evaluate(candles, now, options.CapitalLimit);
      var tick = feed.Fresh(instrument.Code);
      var qualifies = decision.Qualifies && tick != null && TradingSessionPolicy.InIndia(now).TimeOfDay < new TimeSpan(15, 15, 0);
      var reason = tick == null ? "WAIT: no fresh timestamped broker price; entries blocked. " + decision.Reason : TradingSessionPolicy.InIndia(now).TimeOfDay >= new TimeSpan(15, 15, 0) ? "NO TRADE: 15:15 entry cutoff reached. " + decision.Reason : decision.Reason;
      await gate.Mutex.WaitAsync(ct);
      try {
        await using var db = await factory.CreateDbContextAsync(ct);
        if ((await db.ControlStates.SingleAsync(ct)).State != EngineState.OBSERVING || !await db.ResearchSessions.AnyAsync(x => x.Id == session.Id && x.Status == "ACTIVE", ct)) return;
        qualifies = qualifies && tick != null && feed.Fresh(instrument.Code) != null && feed.Generation == tick.Generation && ObservationStrategy.NormalSession(clock.GetUtcNow());
        var a = await db.ResearchAssessments.SingleOrDefaultAsync(x => x.SessionId == session.Id && x.Code == instrument.Code, ct);
        if (a == null) { a = new ResearchAssessment { SessionId = session.Id, Code = instrument.Code, Symbol = instrument.Symbol }; db.ResearchAssessments.Add(a); }
        a.AssessedUtc = now.UtcDateTime; a.CandleCloseUtc = decision.CandleCloseUtc; a.Outcome = qualifies ? "SETUP" : "WAIT / NO TRADE"; a.Reason = reason; a.EvidenceJson = JsonSerializer.Serialize(decision.Evidence);
        if (qualifies && decision.CandleCloseUtc != null && !await db.ResearchSuggestions.AnyAsync(x => x.Code == instrument.Code && (x.CandleCloseUtc == decision.CandleCloseUtc || x.Status == "OPEN" || x.Status == "PENDING_ENTRY"), ct)) {
          db.ResearchSuggestions.Add(new ResearchSuggestion { SessionId = session.Id, Strategy = ObservationStrategy.Version, Code = instrument.Code, Symbol = instrument.Symbol, SuggestedUtc = now.UtcDateTime, CandleCloseUtc = decision.CandleCloseUtc.Value, Reason = reason, EvidenceJson = a.EvidenceJson, ProposedEntry = decision.Entry, Stop = decision.Stop, Target = decision.Target, EntryLimit = decision.EntryLimit, CapitalFitQuantity = decision.CapitalFitQuantity, FeedGeneration = tick!.Generation });
        }
        var active = await db.ResearchSessions.SingleAsync(x => x.Id == session.Id, ct);
        active.Message = "Observing RELIANCE / ICICIBANK / SBIN. " + feed.Status + ". Technical-only research; news and index context not checked.";
        await db.SaveChangesAsync(ct);
      } finally { gate.Mutex.Release(); }
    }
  }

  public static IReadOnlyList<Candle> ParseCandles(JsonElement response, string code)
  {
    if (!response.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True || !response.TryGetProperty("data", out var data)) throw new InvalidOperationException("Unsuccessful historical candle response.");
    if (!data.TryGetProperty(code, out var instrument) || !instrument.TryGetProperty("candles", out var candles) || candles.ValueKind != JsonValueKind.Array) return [];
    return candles.EnumerateArray().Select(x => new Candle(DateTimeOffset.FromUnixTimeSeconds(x.GetProperty("ts").GetInt64()).UtcDateTime, x.GetProperty("o").GetDecimal(), x.GetProperty("h").GetDecimal(), x.GetProperty("l").GetDecimal(), x.GetProperty("c").GetDecimal(), x.GetProperty("v").GetInt64())).ToList();
  }

  async Task Message(string message, CancellationToken ct)
  {
    try {
      await using var db = await factory.CreateDbContextAsync(ct);
      var session = await db.ResearchSessions.SingleOrDefaultAsync(x => x.Status == "ACTIVE", ct);
      if (session != null) { session.Message = message; await db.SaveChangesAsync(ct); }
    } catch (Exception) when (!ct.IsCancellationRequested) { /* A storage failure never becomes a synthetic result. */ }
  }
}
