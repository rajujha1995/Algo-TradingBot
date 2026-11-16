using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using TradingBot.Application;
namespace TradingBot.Infrastructure;

public sealed record BrokerOrderEvent(string OrderId, DateTimeOffset PublishedUtc, DateTimeOffset ReceivedUtc, string Status, string StatusCode, string? Name, string? Side, decimal? RequestedQuantity, decimal? FilledLots, decimal? ExecutionPrice);

// Read-only subscription to orders placed elsewhere. This service cannot send an order.
public sealed class BrokerOrderFeed(TradingOptions options, TimeProvider clock, RealtimeJournalStore? journal = null) : BackgroundService
{
  readonly object sync = new();
  readonly Dictionary<string, BrokerOrderEvent> events = new();
  volatile bool enabled, connected, reconciled;
  CancellationTokenSource? connectionCancellation;
  long generation;
  public string Status { get; private set; } = "Account monitoring stopped";
  public event Action? ReconcileRequested;
  public long Generation => Interlocked.Read(ref generation);
  public void Enable(bool value)
  {
    lock (sync) {
      enabled = value; reconciled = false;
      if (!value) { connected = false; try { connectionCancellation?.Cancel(); } catch (ObjectDisposedException) { } Status = "Account monitoring stopped"; }
    }
  }
  public void AcknowledgeReconciliation(long expectedGeneration) { lock (sync) if (connected && Generation == expectedGeneration) reconciled = true; }
  public object Snapshot() { lock (sync) return new { connected, reconciled, status = Status, events = events.Values.OrderByDescending(x => x.PublishedUtc).Take(200).ToArray(), limitation = "Latest 200 order-event states; fill lots are not assumed to equal share quantity. REST order/trade books remain separate reconciliation evidence." }; }
  void Gap() { lock (sync) { connected = false; reconciled = false; Interlocked.Increment(ref generation); } ReconcileRequested?.Invoke(); }

  protected override async Task ExecuteAsync(CancellationToken ct)
  {
    var failures = 0;
    while (!ct.IsCancellationRequested) {
      if (!enabled) { await Task.Delay(TimeSpan.FromSeconds(1), ct); continue; }
      DateTimeOffset? opened = null;
      try {
        Gap();
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", options.AccessToken);
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (sync) { connectionCancellation = lifetime; if (!enabled) lifetime.Cancel(); }
        lifetime.CancelAfter(TimeSpan.FromSeconds(15)); Status = "Connecting to broker order events";
        await ws.ConnectAsync(new Uri("wss://ws-order-updates.indstocks.com/api/v1/ws/trades"), lifetime.Token);
        lifetime.CancelAfter(Timeout.InfiniteTimeSpan);
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"action\":\"subscribe\",\"mode\":\"order_update\"}"), WebSocketMessageType.Text, true, lifetime.Token);
        opened = clock.GetUtcNow();
        lock (sync) { connected = true; Status = "Connected; REST reconciliation required"; } ReconcileRequested?.Invoke();
        var buffer = new byte[8192];
        while (enabled && ws.State == WebSocketState.Open && !lifetime.IsCancellationRequested) {
          using var frame = new MemoryStream(); WebSocketReceiveResult part;
          do {
            part = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), lifetime.Token);
            if (part.MessageType == WebSocketMessageType.Close) throw new WebSocketException();
            if (part.MessageType != WebSocketMessageType.Text || frame.Length + part.Count > 65536) throw new InvalidOperationException("Unsupported broker frame");
            frame.Write(buffer, 0, part.Count);
          } while (!part.EndOfMessage);
          var text = Encoding.UTF8.GetString(frame.ToArray());
          if (BrokerFrames.IsHeartbeat(text)) continue;
          var json = BrokerFrames.Decode(text);
          if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String || mode.GetString() != "order_update") continue;
          var id = json.GetProperty("data").GetProperty("order_id").GetString();
          if (string.IsNullOrWhiteSpace(id)) continue;
          BrokerOrderEvent? recorded;
          lock (sync) {
            events.TryGetValue(id, out var previous);
            var update = Merge(previous, json, clock.GetUtcNow());
            if (update != null) { events[id] = update; failures = 0; }
            recorded = update;
            while (events.Count > 200) events.Remove(events.Values.MinBy(x => x.PublishedUtc)!.OrderId);
            Status = "Receiving broker order events; balances and positions refresh via REST";
          }
          if (recorded != null && journal != null) await journal.UpdateAsync(state => {
            if (!state.BrokerEvents.Any(x => x.OrderId == recorded.OrderId && x.PublishedUtc == recorded.PublishedUtc && x.StatusCode == recorded.StatusCode)) state.BrokerEvents.Add(recorded);
          }, lifetime.Token);
          ReconcileRequested?.Invoke();
        }
      } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
      catch (Exception) { if (enabled) { Status = "Order feed interrupted/authentication failed; REST fallback, retrying with backoff"; if (opened != null && clock.GetUtcNow() - opened >= TimeSpan.FromSeconds(30)) failures = 0; failures++; } }
      finally { lock (sync) connectionCancellation = null; Gap(); }
      await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(failures, 6)))), ct);
    }
  }

  public static BrokerOrderEvent? Merge(BrokerOrderEvent? previous, JsonElement frame, DateTimeOffset received)
  {
    if (!frame.TryGetProperty("timestamp", out var ts) || !ts.TryGetInt64(out var ms) || ms <= 0 || !frame.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
    DateTimeOffset published;
    try { published = DateTimeOffset.FromUnixTimeMilliseconds(ms); } catch (ArgumentOutOfRangeException) { return null; }
    if (published > received.AddSeconds(2) || (previous != null && published < previous.PublishedUtc)) return previous;
    string? String(string key) => data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    decimal? Number(string key) => data.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) ? number : null;
    var id = String("order_id"); var code = String("order_status");
    if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || (previous != null && previous.OrderId != id)) return null;
    var status = code switch { "R" => "Received", "P" => "Pending", "S" => "Executed", "F" => "Failed", "C" => "Cancelled", "RJ" => "Rejected", "PF" => "Partially filled", "PFC" => "Partially filled; remainder cancelled", _ => "Unknown broker status" };
    return new(id, published, received, status, code ?? "Unknown", String("entity_name") ?? previous?.Name, String("order_type") ?? previous?.Side, Number("req_quantity") ?? previous?.RequestedQuantity, Number("lot") ?? previous?.FilledLots, Number("executed_price") ?? previous?.ExecutionPrice);
  }
}
