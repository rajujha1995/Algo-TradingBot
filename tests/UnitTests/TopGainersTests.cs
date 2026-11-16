using System.Text.Json;
using TradingBot.Application;
using TradingBot.Infrastructure;
namespace UnitTests;

public class TopGainersTests
{
  const string Header = "EXCH,SEGMENT,SECURITY_ID,INSTRUMENT_NAME,TRADING_SYMBOL,ISIN\n";
  [Fact] public void ParsesQuotedCsvAndPrefersNsePerIsinWithoutCapFilter()
  {
    var instruments = TopGainersService.ParseInstruments(Header + "BSE,E,1,EQUITY,ABC,IN123\nNSE,E,2,EQUITY,\"ABC, LTD\",IN123\nBSE,E,3,EQUITY,SMALL,IN456\nNSE,FNO,4,FUTSTK,NO,\nNSE,E,not-a-code,EQUITY,NO,\n");
    Assert.Equal(2, instruments.Count);
    Assert.Contains(instruments, x => x.Code == "NSE_2" && x.Symbol == "ABC, LTD");
    Assert.Contains(instruments, x => x.Code == "BSE_3");
  }
  [Fact] public void SchemaMismatchFailsExplicitly() => Assert.Throws<InvalidOperationException>(() => TopGainersService.ParseInstruments("SYMBOL,ID\nABC,1"));
  [Fact] public void MissingQuotesAndZeroVolumeDoNotBecomeGainers()
  {
    var instruments = Enumerable.Range(1, 6).Select(i => new EquityInstrument($"NSE_{i}", $"S{i}", "NSE", ""));
    using var json = JsonDocument.Parse("""{"status":"success","data":{"NSE_1":{"live_price":110,"prev_close":100,"volume":50},"NSE_2":{"live_price":1000,"prev_close":0,"volume":5},"NSE_3":{"live_price":500,"prev_close":100,"volume":0},"NSE_4":{"live_price":90,"prev_close":100,"volume":10},"NSE_6":{"live_price":"120","prev_close":100,"volume":100}}}""");
    var ranked = TopGainersService.RankQuotes(instruments, json.RootElement);
    Assert.Equal(2, ranked.Quoted);
    Assert.Equal(10m, Assert.Single(ranked.Items).ChangePercent);
  }
  [Fact] public void UnsuccessfulQuoteResponseCannotPublishRanking()
  {
    using var json = JsonDocument.Parse("""{"status":"error","data":{}}""");
    Assert.Throws<InvalidOperationException>(() => TopGainersService.RankQuotes([], json.RootElement));
  }
  [Fact] public async Task FullScanBatchesRanksTopTenAndCaches()
  {
    var fake = new FakeBroker(); var service = new TopGainersService(fake, TimeProvider.System);
    var snapshot = await service.GetAsync(default);
    Assert.Equal(205, snapshot.UniverseCount); Assert.Equal(205, snapshot.QuotedCount);
    Assert.Equal(10, snapshot.Items.Count); Assert.Equal("S205", snapshot.Items[0].Symbol);
    Assert.Equal(2, fake.QuoteCalls); Assert.Equal(1, fake.MasterCalls);
    Assert.Same(snapshot, await service.GetAsync(default)); Assert.Equal(2, fake.QuoteCalls);
  }
  [Fact] public async Task RejectedInstrumentDoesNotDiscardValidNeighboursAndIsRecheckedLater()
  {
    var fake = new FakeBroker(); fake.InvalidCodes.Add("NSE_73");
    var clock = new MutableClock(); var service = new TopGainersService(fake, clock);
    var snapshot = await service.GetAsync(default);
    Assert.Equal(204, snapshot.QuotedCount); Assert.Equal(1, snapshot.RejectedCount); Assert.Equal(0, snapshot.UnresolvedCount);
    Assert.Equal("S205", snapshot.Items[0].Symbol); Assert.DoesNotContain(snapshot.Items, x => x.Code == "NSE_73");
    clock.Now = clock.Now.AddMinutes(2); fake.Requests.Clear();
    Assert.Equal(204, (await service.GetAsync(default)).QuotedCount);
    Assert.DoesNotContain(fake.Requests, codes => codes.Split(',').Contains("NSE_73"));
    clock.Now = clock.Now.AddHours(1); fake.InvalidCodes.Clear();
    var recovered = await service.GetAsync(default); Assert.Equal(205, recovered.QuotedCount); Assert.Equal(0, recovered.RejectedCount);
  }
  [Fact] public async Task BroadRejectionsHaveABoundedReadBudgetAndExplicitPartialCoverage()
  {
    var fake = new FakeBroker { Count = 600 };
    foreach (var code in Enumerable.Range(1, 600).Select(i => $"NSE_{i}").OrderBy(x => x, StringComparer.Ordinal).Take(200)) fake.InvalidCodes.Add(code);
    var snapshot = await new TopGainersService(fake, TimeProvider.System).GetAsync(default);
    Assert.Equal(400, snapshot.QuotedCount); Assert.True(snapshot.UnresolvedCount > 0);
    Assert.Equal(600, snapshot.QuotedCount + snapshot.RejectedCount + snapshot.UnresolvedCount);
    Assert.InRange(fake.QuoteCalls, 3, 67);
  }
  [Fact] public async Task OtherBrokerFailuresAbortWithoutSubdivision()
  {
    var fake = new FakeBroker { Failure = new InvalidOperationException("Broker rate limit reached") };
    await Assert.ThrowsAsync<InvalidOperationException>(() => new TopGainersService(fake, TimeProvider.System).GetAsync(default));
    Assert.Equal(1, fake.QuoteCalls);
  }
  sealed class MutableClock : TimeProvider {
    public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-17T10:00:00Z");
    public override DateTimeOffset GetUtcNow() => Now;
  }
  sealed class FakeBroker : ITradingBroker
  {
    public int QuoteCalls, MasterCalls;
    public int Count = 205;
    public HashSet<string> InvalidCodes = [];
    public List<string> Requests = [];
    public Exception? Failure;
    public Task<string> GetInstrumentsAsync(CancellationToken ct) { MasterCalls++; return Task.FromResult(Header + string.Join('\n', Enumerable.Range(1, Count).Select(i => $"NSE,E,{i},EQUITY,S{i},ISIN{i}"))); }
    public Task<JsonElement> GetQuotesAsync(string codes, CancellationToken ct) {
      QuoteCalls++; Assert.InRange(codes.Split(',').Length, 1, 200);
      Requests.Add(codes);
      if (Failure != null) throw Failure;
      if (codes.Split(',').Any(InvalidCodes.Contains)) throw new BrokerInvalidScripCodesException();
      return Task.FromResult(JsonSerializer.SerializeToElement(new { status = "success", data = codes.Split(',').ToDictionary(x => x, x => new { live_price = 100 + int.Parse(x.Split('_')[1]), prev_close = 100, volume = 100 }) }));
    }
    public Task<JsonElement> GetProfileAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<JsonElement> GetFundsAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<JsonElement> GetHoldingsAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<JsonElement> GetPositionsAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<JsonElement> GetCandlesAsync(string code, long start, long end, CancellationToken ct) => throw new NotSupportedException();
    public Task PlaceOrderAsync(CancellationToken ct) => throw new NotSupportedException();
  }
}
