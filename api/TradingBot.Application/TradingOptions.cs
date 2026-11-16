using System.Globalization;
namespace TradingBot.Application;

public sealed record TradingOptions
{
  public string Environment { get; init; } = "Production";
  public string TradingMode { get; init; } = "PAPER";
  public string DatabasePath { get; init; } = "";
  public string LocalPassword { get; init; } = "";
  public string AccessToken { get; init; } = "";
  public decimal CapitalLimit { get; init; } = 1000;
  public decimal DailyLossLimit { get; init; } = 50;
  public decimal TotalLossLimit { get; init; } = 100;
  public int MaxOpenPositions { get; init; } = 1;
  public decimal SlippageBps { get; init; } = 5;
  public string Root { get; init; } = "";
  public void Validate()
  {
    if (Environment != "Production") throw new InvalidOperationException("Only the Production environment is supported.");
    if (TradingMode != "PAPER") throw new InvalidOperationException("LIVE is unavailable: broker execution and compliance verification are not implemented.");
    if (LocalPassword.Length < 16 || LocalPassword.Contains("REPLACE", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Set LOCAL_ADMIN_PASSWORD to a unique value of at least 16 characters in the backend environment file.");
    if (CapitalLimit <= 0 || CapitalLimit > 1000 || DailyLossLimit <= 0 || TotalLossLimit < DailyLossLimit || TotalLossLimit > CapitalLimit || MaxOpenPositions != 1 || SlippageBps < 0 || SlippageBps > 100) throw new InvalidOperationException("Invalid risk configuration: capital 0–1000, one position, positive loss budgets within capital, slippage 0–100 bps.");
    var expected = "trading-prod.db";
    if (Path.GetFileName(DatabasePath) != expected) throw new InvalidOperationException($"Environment isolation requires database filename {expected}.");
  }
  public static decimal ParseDecimal(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
}
