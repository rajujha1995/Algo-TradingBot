namespace TradingBot.Domain;
public sealed record Candle(DateTime TimestampUtc, decimal Open, decimal High, decimal Low, decimal Close, long Volume);
