using System.Globalization;
namespace TradingBot.Application;

public sealed record TradingOptions
{
  public string Environment { get; init; } = "Production";
  public string JournalPath { get; init; } = "";
  public string LocalPassword { get; init; } = "";
  public string AccessToken { get; init; } = "";
  public string Root { get; init; } = "";
  public void Validate()
  {
    if (Environment != "Production") throw new InvalidOperationException("Only the Production environment is supported.");
    if (LocalPassword.Length < 16 || LocalPassword.Contains("REPLACE", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Set LOCAL_ADMIN_PASSWORD to a unique value of at least 16 characters in the backend environment file.");
    if (Path.GetExtension(JournalPath) != ".json") throw new InvalidOperationException("The research journal must be a JSON file, never an existing database or environment file.");
  }
  public static decimal ParseDecimal(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
}
