using TradingBot.Application;
namespace TradingBot.Infrastructure;

public static class EnvironmentConfiguration
{
  public static TradingOptions Load(string root, string environment, Func<string, string?>? secret = null)
  {
    if (environment != "Production") throw new InvalidOperationException("Only the Production configuration is supported.");
    var path = Path.Combine(root, "api", ".env.production");
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    if (File.Exists(path)) foreach (var line in File.ReadAllLines(path))
    {
      var trimmed = line.Trim(); if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
      var parts = trimmed.Split('=', 2); if (parts.Length != 2) throw new InvalidOperationException("Invalid environment-file line.");
      values.Add(parts[0].Trim(), parts[1].Trim());
    }
    string Get(string key, string fallback = "") => secret?.Invoke(key) ?? System.Environment.GetEnvironmentVariable(key) ?? values.GetValueOrDefault(key, fallback);
    if (Get("APP_ENV", environment) != environment) throw new InvalidOperationException("APP_ENV must match ASPNETCORE_ENVIRONMENT.");
    if (Get("AUTO_TRADING_ENABLED", "false") != "false") throw new InvalidOperationException("Automatic strategy execution is unavailable; keep AUTO_TRADING_ENABLED=false.");
    if (Get("INDSTOCKS_BASE_URL", "https://api.indstocks.com") != "https://api.indstocks.com") throw new InvalidOperationException("Only the official INDstocks API base URL is allowed.");
    var options = new TradingOptions
    {
      Root = root,
      Environment = environment,
      LocalPassword = Get("LOCAL_ADMIN_PASSWORD"),
      AccessToken = Get("INDSTOCKS_ACCESS_TOKEN"),
      JournalPath = Path.GetFullPath(Get("JOURNAL_PATH", "data/realtime-journal.json"), Path.Combine(root, "api"))
    };
    options.Validate(); return options;
  }
  public static string FindRoot()
  {
    var configured = System.Environment.GetEnvironmentVariable("TRADINGBOT_ROOT"); if (configured != null) return Path.GetFullPath(configured);
    for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent) if (Directory.Exists(Path.Combine(d.FullName, "api")) && Directory.Exists(Path.Combine(d.FullName, "ui"))) return d.FullName;
    throw new InvalidOperationException("Run from repository root or set TRADINGBOT_ROOT.");
  }
}
