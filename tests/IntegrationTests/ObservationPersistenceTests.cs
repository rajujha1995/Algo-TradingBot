using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Application;
using TradingBot.Domain;
using TradingBot.Infrastructure;
namespace IntegrationTests;

public sealed class ObservationPersistenceTests : IDisposable
{
  readonly string directory = Path.Combine(Path.GetTempPath(), "observation-tests-" + Guid.NewGuid());
  readonly TradingOptions options;
  readonly Factory factory;
  readonly ExecutionGate gate = new();
  readonly TimeProvider clock = new FixedClock();
  readonly PaperTradingBrokerAdapter store;
  readonly NeverNetwork network = new();
  public ObservationPersistenceTests() {
    Directory.CreateDirectory(directory);
    options = new TradingOptions { DatabasePath = Path.Combine(directory, "trading-prod.db"), Environment = "Production", LocalPassword = "disposable-observation-only", AccessToken = "test-not-real" };
    factory = new(options.DatabasePath);
    store = new(factory, gate, options, new TradingRiskService(options));
  }
  ObservationEngine Engine(TradingOptions? overrideOptions = null) {
    var o = overrideOptions ?? options;
    return new(factory, gate, new IndstocksBrokerAdapter(new HttpClient(network), o), new ObservationPriceFeed(o, clock), o, clock, NullLogger<ObservationEngine>.Instance);
  }
  async Task<long> Start() { await store.InitializeAsync(); return (await Engine().StartAsync("START TRADING", default)).Id; }
  async Task Seed(long session) {
    await using var db = factory.CreateDbContext();
    db.ResearchSuggestions.Add(new ResearchSuggestion { SessionId = session, Code = "NSE_123", Symbol = "TEST_FIXTURE", SuggestedUtc = clock.GetUtcNow().UtcDateTime, CandleCloseUtc = clock.GetUtcNow().UtcDateTime.AddMinutes(-5), Status = "OPEN", EntryPrice = 100, EntryUtc = clock.GetUtcNow().UtcDateTime, LastPrice = 110, NetPnl = 90, EvidenceJson = JsonSerializer.Serialize(new ObservationEvidence([], null, null, null, null, null, null)) });
    await db.SaveChangesAsync();
  }
  [Fact] public async Task StartRecordsObservationWithoutAllocatingCashOrSendingOrders()
  {
    await Start(); Assert.Equal(EngineState.OBSERVING, (await store.GetStateAsync()).State);
    Assert.Equal(1000, (await store.GetPortfolioAsync()).AvailableCash);
    Assert.Empty(await store.GetOrdersAsync()); Assert.Empty(await store.GetPositionsAsync()); Assert.Equal(0, network.Calls);
    await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecoverAsync("START PAPER"));
  }
  [Fact] public async Task MissingTokenCannotStartAndDoesNotChangeControl()
  {
    await store.InitializeAsync();
    await Assert.ThrowsAsync<InvalidOperationException>(() => Engine(options with { AccessToken = "" }).StartAsync("START TRADING", default));
    Assert.Equal(EngineState.STOPPED, (await store.GetStateAsync()).State);
  }
  [Fact] public async Task StopPersistsUncertainOpenOutcomeWithoutAssumingAnExit()
  {
    await Seed(await Start()); await store.StopAsync();
    await using var db = factory.CreateDbContext();
    var suggestion = await db.ResearchSuggestions.SingleAsync();
    Assert.Equal("UNCERTAIN", suggestion.Status); Assert.Null(suggestion.ExitPrice); Assert.Equal(90, suggestion.NetPnl);
    Assert.Equal("STOPPED", (await db.ResearchSessions.SingleAsync()).Status);
    var json = JsonSerializer.SerializeToElement(await new ObservationJournal(factory, clock).ReadAsync(default));
    Assert.Equal(0, json.GetProperty("summary").GetProperty("closed").GetInt32());
    Assert.Equal(0m, json.GetProperty("summary").GetProperty("realizedScenarioNet").GetDecimal());
  }
  [Fact] public async Task RestartRequiresManualStartAndKeepsJournalEvidence()
  {
    await Seed(await Start()); await store.InitializeAsync();
    Assert.Equal(EngineState.RECOVERY_REQUIRED, (await store.GetStateAsync()).State);
    await using var db = factory.CreateDbContext();
    Assert.Equal("INTERRUPTED", (await db.ResearchSessions.SingleAsync()).Status);
    var suggestion = await db.ResearchSuggestions.SingleAsync(); Assert.Equal("UNCERTAIN", suggestion.Status); Assert.NotEqual("{}", suggestion.EvidenceJson);
    Assert.Empty(await store.GetOrdersAsync());
  }
  [Fact] public async Task DoubleStartCannotCreateTwoActiveSessions()
  {
    await Start(); await Assert.ThrowsAsync<InvalidOperationException>(() => Engine().StartAsync("START TRADING", default));
    await using var db = factory.CreateDbContext(); Assert.Single(await db.ResearchSessions.ToListAsync());
  }
  public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }
  sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-11-16T09:00:00Z"); }
  sealed class Factory(string path) : IDbContextFactory<TradingDbContext> { public TradingDbContext CreateDbContext() => new(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite("Data Source=" + path).Options); }
  sealed class NeverNetwork : HttpMessageHandler { public int Calls; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; throw new InvalidOperationException("Tests must not use the broker network"); } }
}
