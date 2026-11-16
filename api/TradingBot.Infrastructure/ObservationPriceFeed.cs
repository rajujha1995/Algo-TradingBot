using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using TradingBot.Application;
namespace TradingBot.Infrastructure;

public sealed record ResearchTick(string Code, decimal Price, DateTime Utc, DateTime ReceivedUtc, long Generation);

// Market-data only. This class has no broker execution methods.
public sealed class ObservationPriceFeed(TradingOptions options, TimeProvider clock) : BackgroundService
{
  EquityInstrument[] desired = [];
  readonly ConcurrentDictionary<string, EquityInstrument[]> owners = new();
  readonly object subscriptionSync = new();
  CancellationTokenSource? connectionCancellation;
  readonly ConcurrentDictionary<string, ResearchTick> latest = new();
  readonly ConcurrentQueue<ResearchTick> queue = new();
  long generation;
  volatile bool connected;
  public string Status { get; private set; } = "Not observing";
  public long Generation => Interlocked.Read(ref generation);
  public bool HasSubscriptions => Volatile.Read(ref desired).Length > 0;
  public int SubscribedCount => Volatile.Read(ref desired).Length;
  public void Configure(IEnumerable<EquityInstrument> instruments) => ConfigureFor("research", instruments);
  public void ConfigureFor(string owner, IEnumerable<EquityInstrument> instruments)
  {
    lock (subscriptionSync) {
    owners[owner] = instruments.ToArray();
    // The documented LTP envelope identifies only the numeric token. Never guess
    // exchange when NSE/BSE numeric tokens collide across shared subscriptions.
    var merged = owners.Values.SelectMany(x => x).DistinctBy(x => x.Code)
      .GroupBy(x => x.Code.Split('_')[1]).Where(group => group.Count() == 1).Select(group => group.Single())
      .OrderBy(x => x.Code, StringComparer.Ordinal).Take(3000).ToArray();
    if (!Volatile.Read(ref desired).Select(x => x.Code).SequenceEqual(merged.Select(x => x.Code))) {
      Interlocked.Exchange(ref desired, merged);
      try { connectionCancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }
    }
  }
  public ResearchTick? Fresh(string code)
  {
    if (!connected || !latest.TryGetValue(code, out var tick) || tick.Generation != Generation) return null;
    return IsFresh(tick.Utc, tick.ReceivedUtc, clock.GetUtcNow().UtcDateTime) ? tick : null;
  }
  public static bool IsFresh(DateTime source, DateTime received, DateTime now) => source <= now.AddSeconds(2) && source >= now.AddSeconds(-15) && received <= now.AddSeconds(2) && received >= now.AddSeconds(-15);
  public IReadOnlyList<ResearchTick> Drain()
  {
    var result = new List<ResearchTick>();
    while (queue.TryDequeue(out var tick)) result.Add(tick);
    return result;
  }
  void Gap() { connected = false; Interlocked.Increment(ref generation); latest.Clear(); while (queue.TryDequeue(out _)) { } }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    var failures = 0;
    while (!stoppingToken.IsCancellationRequested) {
      var instruments = Volatile.Read(ref desired);
      if (instruments.Length == 0 || string.IsNullOrWhiteSpace(options.AccessToken)) {
        Status = instruments.Length == 0 ? "Waiting for an observation session in normal market hours" : "Broker token missing; no prices or suggestions invented";
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); continue;
      }
      try {
        Gap();
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", options.AccessToken);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (subscriptionSync) connectionCancellation = lifetime;
        lifetime.CancelAfter(TimeSpan.FromSeconds(15));
        Status = "Connecting to INDstocks price feed";
        await ws.ConnectAsync(new Uri("wss://ws-prices.indstocks.com/api/v1/ws/prices"), lifetime.Token);
        lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
        await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { action = "subscribe", mode = "ltp", instruments = instruments.Select(x => x.Code.Replace('_', ':')).ToArray() })), WebSocketMessageType.Text, true, lifetime.Token);
        connected = true; Status = "Connected; waiting for timestamped prices";
        var buffer = new byte[8192];
        while (ws.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested && Volatile.Read(ref desired).Select(x => x.Code).SequenceEqual(instruments.Select(x => x.Code))) {
          using var frame = new MemoryStream();
          WebSocketReceiveResult part;
          do {
            using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            receiveTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            part = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), receiveTimeout.Token);
            if (part.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Price feed closed");
            if (part.MessageType != WebSocketMessageType.Text || frame.Length + part.Count > 65536) throw new InvalidOperationException("Unsupported price frame");
            frame.Write(buffer, 0, part.Count);
          } while (!part.EndOfMessage);
          var tick = ParseFrame(Encoding.UTF8.GetString(frame.ToArray()), instruments, clock.GetUtcNow().UtcDateTime, Generation);
          if (tick == null) continue;
          if (latest.TryGetValue(tick.Code, out var previous)) {
            if (tick.Utc < previous.Utc) throw new InvalidOperationException("Out-of-order price stream; continuity unverified");
            if (tick.Utc == previous.Utc) {
              if (tick.Price != previous.Price) throw new InvalidOperationException("Conflicting prices share a timestamp; ordering is unverified");
              continue;
            }
          }
          if (queue.Count >= 5000) throw new InvalidOperationException("Price backlog; observation continuity lost");
          latest[tick.Code] = tick; queue.Enqueue(tick); failures = 0; Status = "Receiving timestamped broker LTP";
        }
      } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
      catch (Exception) { Status = "Feed interrupted or authentication/schema failed; pending entries blocked; retrying with backoff"; failures++; }
      finally { lock (subscriptionSync) connectionCancellation = null; Gap(); }
      await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(failures, 6)))), stoppingToken);
    }
  }

  public static ResearchTick? ParseFrame(string text, IReadOnlyList<EquityInstrument> instruments, DateTime now, long generation)
  {
    if (BrokerFrames.IsHeartbeat(text)) return null;
    using var outer = JsonDocument.Parse(text);
    using var decoded = outer.RootElement.ValueKind == JsonValueKind.String ? JsonDocument.Parse(outer.RootElement.GetString()!) : null;
    var r = decoded?.RootElement ?? outer.RootElement;
    if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("mode", out var mode) || mode.GetString() != "ltp") return null;
    if (!r.TryGetProperty("instrument", out var token) || token.ValueKind != JsonValueKind.String || !r.TryGetProperty("timestamp", out var ts) || !ts.TryGetInt64(out var ms) || !r.TryGetProperty("data", out var data) || !data.TryGetProperty("ltp", out var price) || !price.TryGetDecimal(out var value) || value <= 0) return null;
    var instrument = instruments.SingleOrDefault(x => x.Code.Split('_')[1] == token.GetString());
    if (instrument == null) return null;
    DateTime utc;
    try { utc = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime; } catch (ArgumentOutOfRangeException) { return null; }
    return IsFresh(utc, now, now) ? new(instrument.Code, value, utc, now, generation) : null;
  }
}
