using TradingBot.Application;
namespace UnitTests;

public class TradingSessionPolicyTests
{
  [Theory]
  [InlineData("2026-11-16T01:29:59Z", false)]
  [InlineData("2026-11-16T01:30:00Z", true)]
  [InlineData("2026-11-16T12:29:59Z", true)]
  [InlineData("2026-11-16T12:30:00Z", false)]
  [InlineData("2026-11-16T20:00:00Z", false)]
  [InlineData("2026-11-16T07:00:00+05:30", true)]
  public void UsesIndiaTimeAndExclusiveEnd(string now, bool expected) => Assert.Equal(expected, TradingSessionPolicy.IsStartWindow(DateTimeOffset.Parse(now)));

  [Theory]
  [InlineData("go live", "GO LIVE")]
  [InlineData("GO LIVE ", "GO LIVE")]
  [InlineData("start trading", "START TRADING")]
  [InlineData("START PAPER", "START TRADING")]
  [InlineData(null, "PAPER")]
  public void RequiresExactConfirmation(string? actual, string expected) => Assert.Throws<ArgumentException>(() => TradingSessionPolicy.Confirm(actual, expected));

  [Fact] public void BackendRejectsStartOutsideWindow() => Assert.Throws<InvalidOperationException>(() => TradingSessionPolicy.CheckStart(DateTimeOffset.Parse("2026-11-16T12:30:00Z"), "START TRADING"));
  [Fact] public void BackendAcceptsExactStartWithinWindow() => TradingSessionPolicy.CheckStart(DateTimeOffset.Parse("2026-11-16T01:30:00Z"), "START TRADING");
}
