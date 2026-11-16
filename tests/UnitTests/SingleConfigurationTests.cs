using TradingBot.Application;
using TradingBot.Infrastructure;
namespace UnitTests;

public class SingleConfigurationTests
{
  [Fact] public void DefaultOptionsAreProduction() => Assert.Equal("Production", new TradingOptions().Environment);
  [Theory]
  [InlineData("Development")]
  [InlineData("Staging")]
  public void AlternateConfigurationCannotBeLoaded(string environment) =>
    Assert.Throws<InvalidOperationException>(() => EnvironmentConfiguration.Load(Path.GetTempPath(), environment));
  [Fact] public void DevelopmentCannotBeEnabledEvenInPaperMode() =>
    Assert.Throws<InvalidOperationException>(() => new TradingOptions { Environment = "Development", TradingMode = "PAPER", DatabasePath = "trading-prod.db", LocalPassword = "disposable-test-password" }.Validate());
}
