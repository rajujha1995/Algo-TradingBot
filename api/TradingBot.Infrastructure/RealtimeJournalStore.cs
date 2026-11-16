using System.Text.Json;
using TradingBot.Application;
namespace TradingBot.Infrastructure;

public sealed class RealtimeState
{
  public int SchemaVersion { get; set; } = 1;
  public bool OrderExecutionEnabled { get; set; }
  public string State { get; set; } = "IDLE";
  public string Reason { get; set; } = "Ready to watch real market data. Order execution is disabled.";
  public DateTimeOffset UpdatedUtc { get; set; }
  public List<WatchSession> Sessions { get; set; } = [];
  public List<WatchAssessment> Assessments { get; set; } = [];
  public List<WatchEntry> Entries { get; set; } = [];
  public List<WatchCoverage> Coverage { get; set; } = [];
  public List<BrokerOrderEvent> BrokerEvents { get; set; } = [];
  public List<JsonElement> BrokerFills { get; set; } = [];
}
public sealed class WatchSession
{
  public long Id { get; set; }
  public DateTimeOffset StartedUtc { get; set; }
  public DateTimeOffset? EndedUtc { get; set; }
  public string Status { get; set; } = "ACTIVE";
}
public sealed record WatchAssessment(string Symbol, string Code, DateTimeOffset AssessedUtc, DateTime? CandleCloseUtc, bool Qualifies, string Reason, ObservationEvidence Evidence);
public sealed record WatchCoverage(DateTimeOffset MinuteUtc, int FreshSymbols);
public sealed class WatchEntry
{
  public long Id { get; set; }
  public string Symbol { get; set; } = "";
  public string Code { get; set; } = "";
  public string Kind { get; set; } = "WATCHED_SUGGESTION";
  public string Strategy { get; set; } = ObservationStrategy.Version;
  public DateTimeOffset SuggestedUtc { get; set; }
  public DateTime CandleCloseUtc { get; set; }
  public int Quantity { get; set; } = 10;
  public decimal ReferencePrice { get; set; }
  public DateTimeOffset ReferenceUtc { get; set; }
  public decimal Stop { get; set; }
  public decimal Target { get; set; }
  public decimal? LatestPrice { get; set; }
  public DateTimeOffset? PriceUtc { get; set; }
  public decimal? ObservedMove { get; set; }
  public string Status { get; set; } = "WATCHING";
  public bool CoverageGap { get; set; }
  public DateTimeOffset? StopObservedUtc { get; set; }
  public DateTimeOffset? TargetObservedUtc { get; set; }
  public string Reason { get; set; } = "";
  public ObservationEvidence? Evidence { get; set; }
  public string Note { get; set; } = "Observed price movement × reference quantity. No order placed, no assumed fill, no realized trading profit.";
}

// Single-writer, atomic JSON persistence. Existing SQLite files are never opened.
public sealed class RealtimeJournalStore(string path, TimeProvider clock) : IDisposable
{
  static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
  readonly SemaphoreSlim gate = new(1, 1);
  RealtimeState? current;
  FileStream? ownership;
  public string? PersistenceError { get; private set; }
  public string FileName => Path.GetFileName(path);
  public async Task InitializeAsync(CancellationToken ct = default)
  {
    await gate.WaitAsync(ct);
    try {
      Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
      ownership ??= new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
      RealtimeState next;
      if (File.Exists(path)) {
        var content = await File.ReadAllTextAsync(path, ct);
        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("schemaVersion", out _) || !document.RootElement.TryGetProperty("entries", out _)) throw new InvalidOperationException("Journal schema is invalid. Existing file was not overwritten; restore the private previous backup after review.");
        next = JsonSerializer.Deserialize<RealtimeState>(content, Json) ?? throw new InvalidOperationException("Journal could not be loaded.");
        Validate(next);
      } else {
        if (File.Exists(path + ".previous.json")) throw new InvalidOperationException("Journal is missing but a backup exists. Review recovery before creating a new journal.");
        next = new RealtimeState();
      }
      if (next.State == "WATCHING") {
        next.State = "IDLE"; next.Reason = "Restart detected. Start Watching manually; no outcomes inferred during downtime.";
        foreach (var session in next.Sessions.Where(x => x.Status == "ACTIVE")) { session.Status = "INTERRUPTED"; session.EndedUtc = clock.GetUtcNow(); }
        foreach (var entry in next.Entries.Where(x => x.Status == "WATCHING")) { entry.Status = "PAUSED"; entry.CoverageGap = true; }
      }
      await Persist(next, ct); current = next;
    } finally { gate.Release(); }
  }
  public async Task<RealtimeState> ReadAsync(CancellationToken ct = default)
  {
    await gate.WaitAsync(ct);
    try { return Clone(current ?? throw new InvalidOperationException("Journal not initialized.")); }
    finally { gate.Release(); }
  }
  public async Task UpdateAsync(Action<RealtimeState> update, CancellationToken ct = default)
  {
    await gate.WaitAsync(ct);
    try {
      var next = Clone(current ?? throw new InvalidOperationException("Journal not initialized."));
      update(next); Validate(next); await Persist(next, ct); current = next;
    } finally { gate.Release(); }
  }
  static RealtimeState Clone(RealtimeState state) => JsonSerializer.Deserialize<RealtimeState>(JsonSerializer.Serialize(state, Json), Json)!;
  static void Validate(RealtimeState state)
  {
    if (state.SchemaVersion != 1 || state.OrderExecutionEnabled || state.Entries == null || state.Sessions == null || state.Assessments == null || state.Coverage == null || state.BrokerEvents == null || state.BrokerFills == null || state.State is not ("IDLE" or "WATCHING")) throw new InvalidOperationException("Unsupported or unsafe JSON journal schema; file was not overwritten.");
    if (state.Entries.Any(x => x.ReferencePrice <= 0 || x.Quantity <= 0 || x.Kind != "WATCHED_SUGGESTION") || state.Entries.Select(x => x.Id).Distinct().Count() != state.Entries.Count) throw new InvalidOperationException("Invalid reference entries; journal was not overwritten.");
  }
  async Task Persist(RealtimeState state, CancellationToken ct)
  {
    state.UpdatedUtc = clock.GetUtcNow();
    var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
    try {
      var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Json);
      await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough)) { await file.WriteAsync(bytes, ct); await file.FlushAsync(ct); file.Flush(true); }
      ct.ThrowIfCancellationRequested();
      if (File.Exists(path)) File.Replace(temporary, path, path + ".previous.json", true); else File.Move(temporary, path);
      PersistenceError = null;
    } catch { PersistenceError = "JSON journal could not be saved. Last committed observations retained; check disk/access before relying on new records."; throw; }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
  }
  public void Dispose() { ownership?.Dispose(); gate.Dispose(); }
}
