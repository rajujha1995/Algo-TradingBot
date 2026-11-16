using System.Text.Json;
using TradingBot.Infrastructure;
using TradingBot.Application;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntegrationTests;

public sealed class RealtimeJournalTests : IDisposable
{
  readonly string directory = Path.Combine(Path.GetTempPath(), "realtime-journal-tests-" + Guid.NewGuid().ToString("N"));
  string Journal => Path.Combine(directory, "journal.json");
  RealtimeJournalStore Store() => new(Journal, TimeProvider.System);
  static WatchEntry Entry(long id = 1) => new() { Id = id, Symbol = "FIXTURE", Code = "NSE_1", ReferencePrice = 100, Quantity = 10, ReferenceUtc = DateTimeOffset.UtcNow, Stop = 95, Target = 110 };

  [Fact] public async Task RestartRetainsEvidenceAndPausesWithoutAssumingExit()
  {
    using (var store = Store()) {
      await store.InitializeAsync();
      await store.UpdateAsync(s => { s.State = "WATCHING"; s.Sessions.Add(new() { Id = 1, StartedUtc = DateTimeOffset.UtcNow }); s.Entries.Add(Entry()); });
      Assert.True(File.Exists(Journal + ".previous.json"));
    }
    using var reopened = Store(); await reopened.InitializeAsync();
    var state = await reopened.ReadAsync();
    Assert.Equal("IDLE", state.State); Assert.Equal("INTERRUPTED", Assert.Single(state.Sessions).Status);
    var entry = Assert.Single(state.Entries); Assert.Equal("PAUSED", entry.Status); Assert.True(entry.CoverageGap);
    Assert.Null(entry.ObservedMove); Assert.Null(entry.TargetObservedUtc); Assert.Empty(state.BrokerFills); Assert.False(state.OrderExecutionEnabled);
  }
  [Fact] public async Task ConcurrentWritersDoNotLoseUpdatesAndReadersCannotMutateState()
  {
    using var store = Store(); await store.InitializeAsync();
    await Task.WhenAll(Enumerable.Range(1, 20).Select(i => store.UpdateAsync(s => s.Entries.Add(Entry(i)))));
    var read = await store.ReadAsync(); Assert.Equal(20, read.Entries.Count); read.Entries.Clear();
    Assert.Equal(20, (await store.ReadAsync()).Entries.Count);
    using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(Journal)); Assert.Equal(20, persisted.RootElement.GetProperty("entries").GetArrayLength());
  }
  [Fact] public async Task CorruptionIsNeverSilentlyReset()
  {
    Directory.CreateDirectory(directory); await File.WriteAllTextAsync(Journal, "{invalid");
    using var store = Store(); await Assert.ThrowsAnyAsync<JsonException>(() => store.InitializeAsync());
    Assert.Equal("{invalid", await File.ReadAllTextAsync(Journal));
  }
  [Fact] public async Task DuplicateProcessCannotOwnSameJournal()
  {
    using var first = Store(); await first.InitializeAsync(); using var second = Store();
    await Assert.ThrowsAsync<IOException>(() => second.InitializeAsync());
  }
  [Fact] public async Task InvalidMutationAndFailedWriteKeepLastCommittedState()
  {
    using var store = Store(); await store.InitializeAsync();
    await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(s => s.OrderExecutionEnabled = true));
    Assert.False((await store.ReadAsync()).OrderExecutionEnabled);
    // Block the backup destination to simulate a portable filesystem failure.
    Directory.CreateDirectory(Journal + ".previous.json");
      await Assert.ThrowsAnyAsync<IOException>(() => store.UpdateAsync(s => s.Entries.Add(Entry())));
    Assert.Empty((await store.ReadAsync()).Entries); Assert.NotNull(store.PersistenceError);
    Directory.Delete(Journal + ".previous.json");
    await store.UpdateAsync(s => s.Entries.Add(Entry())); Assert.Null(store.PersistenceError);
  }
  [Theory]
  [InlineData(1, 29, false)] // 06:59 IST
  [InlineData(1, 30, true)]  // 07:00 IST
  [InlineData(12, 29, true)] // 17:59 IST
  [InlineData(12, 30, false)] // 18:00 IST
  public async Task StartWindowIsEnforcedOnServerAndNeverCreatesAFill(int hour, int minute, bool permitted)
  {
    var clock = new FixedClock(new DateTimeOffset(2026, 11, 17, hour, minute, 0, TimeSpan.Zero));
    using var store = Store(); await store.InitializeAsync();
    var options = new TradingOptions { AccessToken = "fixture-never-sent" };
    using var client = new HttpClient();
    var broker = new IndstocksBrokerAdapter(client, options);
    using var feed = new ObservationPriceFeed(options, clock);
    using var engine = new RealtimeResearchEngine(store, broker, feed, options, clock, NullLogger<RealtimeResearchEngine>.Instance);
    await Assert.ThrowsAsync<ArgumentException>(() => engine.StartWatchingAsync("START TRADING", default));
    if (permitted) {
      await engine.StartWatchingAsync("START WATCHING", default);
      Assert.Equal("WATCHING", (await store.ReadAsync()).State);
      await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartWatchingAsync("START WATCHING", default));
      await engine.StopWatchingAsync(default);
    } else await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartWatchingAsync("START WATCHING", default));
    var state = await store.ReadAsync(); Assert.Equal("IDLE", state.State); Assert.Empty(state.Entries); Assert.Empty(state.BrokerFills); Assert.False(state.OrderExecutionEnabled);
  }
  sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
  public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
