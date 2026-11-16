namespace TradingBot.Application;

public static class TradingSessionPolicy
{
  public const string LiveIntentClaim = "AlgoTrade.LiveRequested";
  public const string LiveBlocker = "Real-money trading is unavailable: broker execution, reconciliation, protective exits and exchange-session checks are not implemented. No money has been committed.";
  public static DateTimeOffset InIndia(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
  public static bool IsStartWindow(DateTimeOffset utc)
  {
    var time = InIndia(utc).TimeOfDay;
    return time >= TimeSpan.FromHours(7) && time < TimeSpan.FromHours(18);
  }
  public static void Confirm(string? supplied, string expected)
  {
    if (supplied != expected) throw new ArgumentException($"Type {expected} exactly to confirm.");
  }
  public static void CheckStart(DateTimeOffset now, string? confirmation)
  {
    Confirm(confirmation, "START TRADING");
    if (!IsStartWindow(now)) throw new InvalidOperationException("Starting is allowed only from 7:00 AM until 6:00 PM IST. Browsing remains available.");
  }
}
