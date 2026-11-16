using Microsoft.EntityFrameworkCore;
using TradingBot.Application;
using TradingBot.Domain;
using TradingBot.Infrastructure;
namespace IntegrationTests;

public sealed class PaperLedgerTests : IDisposable
{
  readonly string directory = Path.Combine(Path.GetTempPath(), "indstocks-tests-" + Guid.NewGuid());
  readonly TradingOptions options;
  readonly TestFactory factory;
  readonly ExecutionGate gate = new();
  readonly ITradingStore store;
  public PaperLedgerTests() { Directory.CreateDirectory(directory); options = new TradingOptions { DatabasePath = Path.Combine(directory, "trading-prod.db"), LocalPassword = "test-password-for-disposable-database" }; factory = new(options.DatabasePath); store = NewStore(); }
  ITradingStore NewStore() => new PaperTradingBrokerAdapter(factory, gate, options, new TradingRiskService(options));
  static PaperOrderRequest Buy(int qty = 2, decimal price = 100, bool partial = false) => new(Guid.NewGuid().ToString(), "DEMO", "BUY", qty, price, partial);
  async Task Start() { await store.InitializeAsync(); await store.RecoverAsync("START PAPER"); }
  [Fact]
  public async Task PartialSellCannotMakeCashNegative() {
    await Start();
    await store.SubmitPaperAsync(Buy(998, 1));
    await Assert.ThrowsAsync<InvalidOperationException>(() => store.SubmitPaperAsync(new(Guid.NewGuid().ToString(), "DEMO", "SELL", 998, 0.02m, true)));
    Assert.True((await store.GetPortfolioAsync()).AvailableCash >= 0);
    Assert.Equal(998, (await store.GetPositionsAsync()).Single().Quantity);
  }
  [Fact] public async Task InitialInstallIsStoppedWithSeparateCash() { await store.InitializeAsync(); Assert.Equal(EngineState.STOPPED, (await store.GetStateAsync()).State); Assert.Equal(1000, (await store.GetPortfolioAsync()).AvailableCash); await Assert.ThrowsAsync<InvalidOperationException>(() => store.SubmitPaperAsync(Buy())); }
  [Fact] public async Task BuySellAccountingDoesNotDoubleCountCashOrFees() { await Start(); await store.SubmitPaperAsync(Buy()); var p = await store.GetPortfolioAsync(); Assert.Equal(1, p.OpenPositions); Assert.Equal(p.AvailableCash + p.CurrentPortfolioValue, p.TotalAccountValue); await store.SubmitPaperAsync(new(Guid.NewGuid().ToString(), "DEMO", "SELL", 2, 110)); p = await store.GetPortfolioAsync(); Assert.Equal(0, p.OpenPositions); Assert.Equal(1000 + p.RealizedPnl, p.TotalAccountValue); Assert.True(p.TotalCharges > 0); }
  [Fact] public async Task DuplicateIdDoesNotCreateSecondOrder() { await Start(); var request = Buy(); var one = await store.SubmitPaperAsync(request); var two = await store.SubmitPaperAsync(request); Assert.Equal(one.Id, two.Id); Assert.Single(await store.GetOrdersAsync()); }
  [Fact] public async Task DuplicateIdWithChangedPriceIsRejected() { await Start(); var request = Buy(); await store.SubmitPaperAsync(request); await Assert.ThrowsAsync<InvalidOperationException>(() => store.SubmitPaperAsync(request with { Price = 101 })); }
  [Fact] public async Task PartialFillsReserveFundsAndStopCancelsRemainder() { await Start(); var order = await store.SubmitPaperAsync(Buy(4, 100, true)); Assert.Equal("PARTIAL", order.Status); var before = await store.GetPortfolioAsync(); Assert.True(before.ReservedCash > 0); await store.StopAsync(); await store.CompletePendingAsync(); var after = await store.GetPortfolioAsync(); Assert.Equal(0, after.ReservedCash); Assert.Equal(before.InvestedAmount, after.InvestedAmount); Assert.Equal(2, (await store.GetPositionsAsync()).Single().Quantity); }
  [Fact] public async Task WorkerCompletesPartialFills() { await Start(); await store.SubmitPaperAsync(Buy(4, 100, true)); await store.CompletePendingAsync(); Assert.Equal("FILLED", (await store.GetOrdersAsync()).Single().Status); Assert.Equal(4, (await store.GetPositionsAsync()).Single().Quantity); }
  [Fact] public async Task EmergencyStopSurvivesRestartAndIsIdempotent() { await Start(); await store.StopAsync(); await store.StopAsync(); var restarted = NewStore(); await restarted.InitializeAsync(); Assert.Equal(EngineState.EMERGENCY_STOPPED, (await restarted.GetStateAsync()).State); await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.SubmitPaperAsync(Buy())); }
  [Fact] public async Task RunningSessionRequiresRecoveryAfterRestart() { await Start(); await NewStore().InitializeAsync(); Assert.Equal(EngineState.RECOVERY_REQUIRED, (await store.GetStateAsync()).State); await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecoverAsync("yes")); }
  [Fact] public async Task ConcurrentOrderAndStopHaveNoFillsAfterStopCompletes() { await Start(); var order = store.SubmitPaperAsync(Buy(4, 100, true)); var stop = store.StopAsync(); await Task.WhenAll(order, stop); var before = (await store.GetPositionsAsync()).Single().Quantity; await store.CompletePendingAsync(); Assert.Equal(before, (await store.GetPositionsAsync()).Single().Quantity); await Assert.ThrowsAsync<InvalidOperationException>(() => store.SubmitPaperAsync(Buy())); }
  [Fact] public async Task CapitalLimitIncludesCharges() { await Start(); await Assert.ThrowsAsync<InvalidOperationException>(() => store.SubmitPaperAsync(Buy(10, 100))); Assert.Empty(await store.GetOrdersAsync()); }
  [Fact] public async Task OpenPositionPreventsAnotherBuy() { await Start(); await store.SubmitPaperAsync(Buy()); await Assert.ThrowsAsync<InvalidOperationException>(() => store.SubmitPaperAsync(Buy())); }
  [Fact] public async Task ShortSellingIsRejected() { await Start(); await Assert.ThrowsAsync<InvalidOperationException>(() => store.SubmitPaperAsync(new(Guid.NewGuid().ToString(), "DEMO", "SELL", 1, 100))); }
  [Fact] public async Task DailyLossBudgetBlocksNewBuys() { await Start(); await store.SubmitPaperAsync(Buy(5, 100)); await store.SubmitPaperAsync(new(Guid.NewGuid().ToString(), "DEMO", "SELL", 5, 80)); await Assert.ThrowsAsync<InvalidOperationException>(() => store.SubmitPaperAsync(Buy(1, 10))); }
  [Fact] public async Task DatabaseFailureDoesNotReturnSuccessfulOrder() { await Start(); factory.Broken = true; await Assert.ThrowsAsync<InvalidOperationException>(() => store.SubmitPaperAsync(Buy())); }
  public void Dispose() { SqliteClear(); try { Directory.Delete(directory, true); } catch (IOException) { } }
  static void SqliteClear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
  sealed class TestFactory(string path) : IDbContextFactory<TradingDbContext>
  {
    public bool Broken;
    public TradingDbContext CreateDbContext() { if (Broken) throw new InvalidOperationException("Database unavailable"); return new(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite("Data Source=" + path).Options); }
  }
}
