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
  sealed class FakeBroker : ITradingBroker
  {
    public int QuoteCalls, MasterCalls;
    public Task<string> GetInstrumentsAsync(CancellationToken ct) { MasterCalls++; return Task.FromResult(Header + string.Join('\n', Enumerable.Range(1, 205).Select(i => $"NSE,E,{i},EQUITY,S{i},ISIN{i}"))); }
    public Task<JsonElement> GetQuotesAsync(string codes, CancellationToken ct) {
      QuoteCalls++; Assert.InRange(codes.Split(',').Length, 1, 200);
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
