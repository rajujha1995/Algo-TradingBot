using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingBot.Application;
namespace TradingBot.Infrastructure;

public sealed class ObservationJournal(IDbContextFactory<TradingDbContext> factory, TimeProvider clock)
{
  public async Task<object> ReadAsync(CancellationToken ct)
  {
    await using var db = await factory.CreateDbContextAsync(ct);
    var since = clock.GetUtcNow().UtcDateTime.AddDays(-30);
    var sessions = await db.ResearchSessions.AsNoTracking().OrderByDescending(x => x.Id).Take(30).ToListAsync(ct);
    var sessionId = sessions.FirstOrDefault()?.Id;
    var assessments = await db.ResearchAssessments.AsNoTracking().Where(x => x.SessionId == sessionId).OrderBy(x => x.Symbol).ToListAsync(ct);
    var suggestions = await db.ResearchSuggestions.AsNoTracking().Where(x => x.SuggestedUtc >= since).OrderByDescending(x => x.Id).ToListAsync(ct);
    var coverage = await db.ResearchCoverage.AsNoTracking().Where(x => x.MinuteUtc >= since).ToListAsync(ct);
    var closed = suggestions.Where(x => x.Status.StartsWith("EXIT_") && x.NetPnl != null).ToList();
    string Day(DateTime utc) => TradingSessionPolicy.InIndia(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc))).ToString("yyyy-MM-dd");
    var days = coverage.Select(x => Day(x.MinuteUtc)).Union(suggestions.Select(x => Day(x.SuggestedUtc))).OrderDescending().ToList();
    return new {
      sessions,
      assessments = assessments.Select(x => new { x.Id, x.Symbol, x.Code, x.AssessedUtc, x.CandleCloseUtc, x.Outcome, x.Reason, evidence = JsonSerializer.Deserialize<ObservationEvidence>(x.EvidenceJson) }),
      journal = suggestions.Take(500).Select(x => new { x.Id, x.Symbol, x.Code, x.Strategy, x.SuggestedUtc, x.CandleCloseUtc, x.Status, x.Quantity, x.CapitalFitQuantity, x.ProposedEntry, x.EntryLimit, x.Stop, x.Target, x.EntryPrice, x.EntryUtc, x.LastPrice, x.MarkUtc, x.ExitPrice, x.ExitUtc, x.GrossPnl, x.EstimatedCosts, x.NetPnl, x.MaxFavorable, x.MaxAdverse, x.Reason, x.OutcomeNote }),
      summary = new { signals = suggestions.Count, filled = suggestions.Count(x => x.EntryPrice != null), closed = closed.Count, uncertain = suggestions.Count(x => x.Status == "UNCERTAIN"), realizedScenarioNet = closed.Sum(x => x.NetPnl ?? 0), knownClosedWinRate = closed.Count == 0 ? (decimal?)null : closed.Count(x => x.NetPnl > 0) * 100m / closed.Count, daysWithObservations = coverage.Select(x => Day(x.MinuteUtc)).Distinct().Count(), observedMinutes = coverage.Select(x => x.MinuteUtc).Distinct().Count(), fullWatchlistMinutes = coverage.Where(x => x.FreshSymbols == 3).Select(x => x.MinuteUtc).Distinct().Count() },
      daily = days.Select(day => new { date = day, observedMinutes = coverage.Where(x => Day(x.MinuteUtc) == day).Select(x => x.MinuteUtc).Distinct().Count(), signals = suggestions.Count(x => Day(x.SuggestedUtc) == day), closed = closed.Count(x => Day(x.SuggestedUtc) == day), net = closed.Where(x => Day(x.SuggestedUtc) == day).Sum(x => x.NetPnl ?? 0), uncertain = suggestions.Count(x => Day(x.SuggestedUtc) == day && x.Status == "UNCERTAIN") }),
      model = "Independent ten-share same-day cash what-if scenarios; not a financed portfolio or a recommendation to buy. Capital-fit quantity uses the configured limit and a Rs50 planned stop budget. Indicators use completed 5-minute candles. Full fills assume next timestamped LTP plus adverse slippage; no order-book queue, partial fills or market impact model. Indicative costs assume Rs10 brokerage per leg, NSE exchange/IPFT charges (2026-03-01 schedule), SEBI fees, intraday sell STT, buy stamp duty and applicable GST; no DP charge for assumed same-day exits. Actual tariffs/rounding may differ. News/index context is not checked. Missed feed intervals and stopped monitoring produce uncertain outcomes, excluded from closed P&L.",
      review = "Aim for at least five trading days with useful coverage. Counts are minute buckets with fresh data at a sample, not continuously observed full minutes. Days with any data are not full-session completion. Keep the API and laptop awake; observation stops on restart and must be started manually each day. One week is an operational observation trial, not proof of profitability. Summary covers 30 days; journal displays the latest 500 suggestions."
    };
  }
  public async Task<object?> DetailAsync(long id, CancellationToken ct)
  {
    await using var db = await factory.CreateDbContextAsync(ct);
    var s = await db.ResearchSuggestions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    return s == null ? null : new { suggestion = s, evidence = JsonSerializer.Deserialize<ObservationEvidence>(s.EvidenceJson) };
  }
}
