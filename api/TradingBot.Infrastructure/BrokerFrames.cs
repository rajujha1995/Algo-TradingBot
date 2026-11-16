using System.Text.Json;
namespace TradingBot.Infrastructure;

public static class BrokerFrames
{
  public static bool IsHeartbeat(string text)
  {
    var value = text.Trim().Trim('"');
    return value.Equals("ping", StringComparison.OrdinalIgnoreCase) || value.Equals("pong", StringComparison.OrdinalIgnoreCase) || value.Equals("heartbeat", StringComparison.OrdinalIgnoreCase);
  }
  public static JsonElement Decode(string text)
  {
    using var outer = JsonDocument.Parse(text);
    if (outer.RootElement.ValueKind != JsonValueKind.String) return outer.RootElement.Clone();
    using var inner = JsonDocument.Parse(outer.RootElement.GetString()!);
    return inner.RootElement.Clone();
  }
}
