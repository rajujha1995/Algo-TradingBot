using System.Net;
using TradingBot.Application;
using TradingBot.Infrastructure;
using TradingBot.Domain;
namespace UnitTests;

public class RiskAndBrokerTests
{
  static TradingOptions Options() => new() { JournalPath = "realtime-journal.json", LocalPassword = "a-unique-local-test-password" };
  [Theory]
  [InlineData("Development")]
  [InlineData("Staging")]
  public void AlternateEnvironmentsAreRejected(string environment) => Assert.Throws<InvalidOperationException>(() => (Options() with { Environment = environment }).Validate());
  [Fact] public void JournalCannotOverwriteDatabase() => Assert.Throws<InvalidOperationException>(() => (Options() with { JournalPath = "retired.db" }).Validate());
  [Fact] public void WeakPasswordsAreRejected() => Assert.Throws<InvalidOperationException>(() => (Options() with { LocalPassword = "password" }).Validate());
  [Fact] public void ChargesArePositiveAndExitIncludesDpCost() { Assert.True(TradingCosts.Estimate(100, false) > 0); Assert.True(TradingCosts.Estimate(100, true) > 15.93m); }
  [Fact] public async Task LiveOrderAdapterNeverSendsARequest() { var handler = new RecordingHandler(HttpStatusCode.OK); var broker = new IndstocksBrokerAdapter(new HttpClient(handler), Options()); await Assert.ThrowsAsync<NotSupportedException>(() => broker.PlaceOrderAsync(default)); Assert.Equal(0, handler.Calls); }
  [Theory]
  [InlineData(401)]
  [InlineData(403)]
  [InlineData(429)]
  [InlineData(500)]
  public async Task BrokerErrorsAreReportedWithoutRetryOrCredentialLeak(int status) { var handler = new RecordingHandler((HttpStatusCode)status); var token = "private-test-token"; var broker = new IndstocksBrokerAdapter(new HttpClient(handler), Options() with { AccessToken = token }); var error = await Assert.ThrowsAsync<InvalidOperationException>(() => broker.GetProfileAsync(default)); Assert.DoesNotContain(token, error.Message); Assert.Equal(1, handler.Calls); Assert.Equal(token, handler.Auth); }
  [Fact] public async Task BrokerUsesRawAuthAndVerifiedPath() { var handler = new RecordingHandler(HttpStatusCode.OK); var broker = new IndstocksBrokerAdapter(new HttpClient(handler), Options() with { AccessToken = "test-token" }); await broker.GetProfileAsync(default); Assert.Equal("https://api.indstocks.com/user/profile", handler.Uri); Assert.Equal("test-token", handler.Auth); }
  [Fact] public async Task InvalidInstrumentsFailBeforeNetwork() { var handler = new RecordingHandler(HttpStatusCode.OK); var broker = new IndstocksBrokerAdapter(new HttpClient(handler), Options()); await Assert.ThrowsAsync<InvalidOperationException>(() => broker.GetQuotesAsync("RELIANCE", default)); Assert.Equal(0, handler.Calls); }
  [Fact] public async Task MissingTokenFailsBeforeNetwork() { var handler = new RecordingHandler(HttpStatusCode.OK); var broker = new IndstocksBrokerAdapter(new HttpClient(handler), Options()); await Assert.ThrowsAsync<InvalidOperationException>(() => broker.GetFundsAsync(default)); Assert.Equal(0, handler.Calls); }
  [Fact] public async Task TimeoutIsNotRetried() { var handler = new RecordingHandler(HttpStatusCode.OK) { Timeout = true }; var broker = new IndstocksBrokerAdapter(new HttpClient(handler), Options() with { AccessToken = "test" }); await Assert.ThrowsAsync<TaskCanceledException>(() => broker.GetProfileAsync(default)); Assert.Equal(1, handler.Calls); }
  [Theory]
  [InlineData("debug_info")]
  [InlineData("message")]
  public async Task QuoteCodeRejectionIsTypedWithoutLeakingUpstreamDetails(string field) {
    var handler = new RecordingHandler(HttpStatusCode.BadRequest) { Body = "{\"" + field + "\":\"Invalid scrip codes or mode\",\"private\":\"sensitive-upstream-detail\"}" };
    var broker = new IndstocksBrokerAdapter(new HttpClient(handler), Options() with { AccessToken = "test" });
    var error = await Assert.ThrowsAsync<BrokerInvalidScripCodesException>(() => broker.GetQuotesAsync("NSE_1", default));
    Assert.DoesNotContain("sensitive", error.Message); Assert.Equal(1, handler.Calls);
  }
  [Theory]
  [InlineData(400, "{\"message\":\"Different validation error\"}")]
  [InlineData(400, "not-json")]
  [InlineData(401, "{\"debug_info\":\"Invalid scrip codes or mode\"}")]
  [InlineData(429, "{\"debug_info\":\"Invalid scrip codes or mode\"}")]
  public async Task OtherQuoteErrorsAreNotClassifiedAsInvalidSymbols(int status, string body) {
    var handler = new RecordingHandler((HttpStatusCode)status) { Body = body };
    var broker = new IndstocksBrokerAdapter(new HttpClient(handler), Options() with { AccessToken = "test" });
    await Assert.ThrowsAsync<InvalidOperationException>(() => broker.GetQuotesAsync("NSE_1", default)); Assert.Equal(1, handler.Calls);
  }
  sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
  {
    public int Calls; public string? Auth; public string? Uri; public bool Timeout;
    public string Body = "{\"status\":\"success\",\"data\":{}}";
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; Auth = request.Headers.GetValues("Authorization").Single(); Uri = request.RequestUri!.ToString(); if (Timeout) throw new TaskCanceledException(); return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(Body) }); }
  }
}
public class BacktestTests
{
  static List<Candle> Candles() => Enumerable.Range(0, 30).Select(i => new Candle(new DateTime(2026, 1, 5, 4, 0, 0, DateTimeKind.Utc).AddMinutes(5 * i), 100 + i, 102 + i, 99 + i, 101 + i, 10000)).ToList();
  [Fact] public void BacktestIsDeterministicAndAccountsForCosts() { var one = Backtest.Run(Candles()); var two = Backtest.Run(Candles()); Assert.Equal(one.FinalEquity, two.FinalEquity); Assert.True(one.TotalCharges > 0); Assert.Equal(1, one.NumberOfTrades); }
  [Fact] public void BacktestRejectsUnsortedData() { var input = Candles(); input.Reverse(); Assert.Throws<InvalidOperationException>(() => Backtest.Run(input)); }
  [Fact] public void FuturePricesDoNotChangePastEquity() { var one = Candles(); var two = Candles(); two[^1] = two[^1] with { Open = 500, High = 502, Low = 499, Close = 501 }; Assert.Equal(Backtest.Run(one).EquityCurve.SkipLast(1), Backtest.Run(two).EquityCurve.SkipLast(1)); }
}
