using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingBot.Application;
namespace TradingBot.Worker;

public sealed class PaperFillWorker(ITradingStore store, ILogger<PaperFillWorker> log) : BackgroundService
{
  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
    try
    {
      while (await timer.WaitForNextTickAsync(stoppingToken))
      {
        try { await store.CompletePendingAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        catch (Exception ex) { log.LogError("Paper fill worker failed ({ErrorType}); new fills are paused until the next successful database transaction.", ex.GetType().Name); }
      }
    }
    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
  }
}
