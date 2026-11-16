namespace TradingBot.Domain;

public sealed class ResearchSession
{
  public long Id { get; set; }
  public DateTime StartedUtc { get; set; }
  public DateTime? EndedUtc { get; set; }
  public string Status { get; set; } = "ACTIVE";
  public string Message { get; set; } = "Waiting for fresh broker feed and completed candles.";
}
public sealed class ResearchAssessment
{
  public long Id { get; set; }
  public long SessionId { get; set; }
  public string Code { get; set; } = "";
  public string Symbol { get; set; } = "";
  public DateTime AssessedUtc { get; set; }
  public DateTime? CandleCloseUtc { get; set; }
  public string Outcome { get; set; } = "WAIT";
  public string Reason { get; set; } = "";
  public string EvidenceJson { get; set; } = "{}";
}
public sealed class ResearchSuggestion
{
  public long Id { get; set; }
  public long SessionId { get; set; }
  public string Strategy { get; set; } = "Breakout-v1";
  public string Symbol { get; set; } = "";
  public string Code { get; set; } = "";
  public DateTime SuggestedUtc { get; set; }
  public DateTime CandleCloseUtc { get; set; }
  public string Status { get; set; } = "PENDING_ENTRY";
  public string Reason { get; set; } = "";
  public string EvidenceJson { get; set; } = "{}";
  public int Quantity { get; set; } = 10;
  public int CapitalFitQuantity { get; set; }
  public decimal ProposedEntry { get; set; }
  public decimal EntryLimit { get; set; }
  public decimal Stop { get; set; }
  public decimal Target { get; set; }
  public decimal? EntryPrice { get; set; }
  public DateTime? EntryUtc { get; set; }
  public decimal? LastPrice { get; set; }
  public DateTime? MarkUtc { get; set; }
  public decimal? ExitPrice { get; set; }
  public DateTime? ExitUtc { get; set; }
  public decimal? GrossPnl { get; set; }
  public decimal? EstimatedCosts { get; set; }
  public decimal? NetPnl { get; set; }
  public decimal MaxFavorable { get; set; }
  public decimal MaxAdverse { get; set; }
  public long FeedGeneration { get; set; }
  public string OutcomeNote { get; set; } = "Hypothetical ten-share scenario; no capital allocated or broker order sent.";
}
public sealed class ResearchCoverage
{
  public long Id { get; set; }
  public long SessionId { get; set; }
  public DateTime MinuteUtc { get; set; }
  public int FreshSymbols { get; set; }
}
