using Microsoft.EntityFrameworkCore;
using TradingBot.Application;
using TradingBot.Domain;
namespace TradingBot.Infrastructure;

public sealed class PaperTradingBrokerAdapter(IDbContextFactory<TradingDbContext> factory, ExecutionGate gate, TradingOptions options, TradingRiskService risk) : ITradingStore
{
  async Task<T> Locked<T>(Func<TradingDbContext, Task<T>> action, CancellationToken ct)
  {
    await gate.Mutex.WaitAsync(ct);
    try { await using var db = await factory.CreateDbContextAsync(ct); return await action(db); } finally { gate.Mutex.Release(); }
  }
  static void Event(TradingDbContext db, string message) => db.Events.Add(new SystemEvent { Category = "Trading", Message = message });
  public async Task InitializeAsync(CancellationToken ct = default) => await Locked(async db =>
  {
    await db.Database.MigrateAsync(ct);
    if (!await db.Accounts.AnyAsync(ct)) db.Accounts.Add(new PaperAccount { Cash = options.CapitalLimit, InitialCapital = options.CapitalLimit });
    var state = await db.ControlStates.SingleOrDefaultAsync(ct);
    if (state == null) db.ControlStates.Add(new TradingControlState());
    else if (state.State is EngineState.PAPER_RUNNING or EngineState.LIVE_RUNNING or EngineState.OBSERVING) { state.State = EngineState.RECOVERY_REQUIRED; state.Reason = "Restart detected; reconciliation and manual confirmation required"; state.UpdatedUtc = DateTime.UtcNow; }
    foreach (var session in await db.ResearchSessions.Where(x => x.Status == "ACTIVE").ToListAsync(ct)) { session.Status = "INTERRUPTED"; session.EndedUtc = DateTime.UtcNow; session.Message = "Restart interrupted observation; no exits inferred during downtime."; }
    foreach (var suggestion in await db.ResearchSuggestions.Where(x => x.Status == "OPEN" || x.Status == "PENDING_ENTRY").ToListAsync(ct)) { suggestion.Status = suggestion.Status == "OPEN" ? "UNCERTAIN" : "CANCELLED"; suggestion.OutcomeNote = "Restart gap; outcome is unverified. Last observed mark is not realized P&L."; }
    Event(db, "Application initialized. Automatic strategy execution disabled."); await db.SaveChangesAsync(ct); return true;
  }, ct);
  public async Task<TradingControlState> GetStateAsync(CancellationToken ct = default) { await using var db = await factory.CreateDbContextAsync(ct); return await db.ControlStates.AsNoTracking().SingleAsync(ct); }
  public async Task<IReadOnlyList<Order>> GetOrdersAsync(CancellationToken ct = default) { await using var db = await factory.CreateDbContextAsync(ct); return await db.Orders.AsNoTracking().OrderByDescending(x => x.Id).Take(200).ToListAsync(ct); }
  public async Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default) { await using var db = await factory.CreateDbContextAsync(ct); return await db.Positions.AsNoTracking().Where(x => x.Quantity > 0).ToListAsync(ct); }
  public async Task<IReadOnlyList<Trade>> GetTradesAsync(CancellationToken ct = default) { await using var db = await factory.CreateDbContextAsync(ct); return await db.Trades.AsNoTracking().OrderByDescending(x => x.Id).Take(200).ToListAsync(ct); }
  public async Task<IReadOnlyList<SystemEvent>> GetEventsAsync(CancellationToken ct = default) { await using var db = await factory.CreateDbContextAsync(ct); return await db.Events.AsNoTracking().OrderByDescending(x => x.Id).Take(200).ToListAsync(ct); }
  static async Task<Portfolio> PortfolioAsync(TradingDbContext db, CancellationToken ct)
  {
    var a = await db.Accounts.SingleAsync(ct); var positions = await db.Positions.Where(x => x.Quantity > 0).ToListAsync(ct);
    var active = await db.Orders.Where(x => x.Status == "PENDING" || x.Status == "PARTIAL").ToListAsync(ct); var trades = await db.Trades.ToListAsync(ct);
    var invested = positions.Sum(x => x.Cost); var value = positions.Sum(x => x.LastPrice * x.Quantity);
    var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")).Date;
    var realized = trades.Sum(x => x.NetProfit);
    var dayRealized = trades.Where(x => TimeZoneInfo.ConvertTimeFromUtc(x.ClosedUtc, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata")).Date == today).Sum(x => x.NetProfit);
    return new(invested, value, value - invested, invested == 0 ? 0 : (value - invested) / invested * 100, realized, dayRealized, a.Cash - active.Sum(x => x.ReservedCash), a.TotalCharges, positions.Count, active.Count, a.Cash + value, active.Sum(x => x.ReservedCash), "SIMULATED execution prices; unrealized marks may be stale. TodayNetPnl is realized only; overnight day-change unavailable.");
  }
  public async Task<Portfolio> GetPortfolioAsync(CancellationToken ct = default) { await using var db = await factory.CreateDbContextAsync(ct); return await PortfolioAsync(db, ct); }
  public Task<TradingControlState> StopAsync(CancellationToken ct = default) => Locked(async db =>
  {
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    var state = await db.ControlStates.SingleAsync(ct); state.State = EngineState.EMERGENCY_STOPPED; state.Reason = "Paper orders cancelled. Holdings retained. No live order adapter is enabled."; state.UpdatedUtc = DateTime.UtcNow;
    foreach (var o in await db.Orders.Where(x => x.Status == "PENDING" || x.Status == "PARTIAL").ToListAsync(ct)) { o.Status = "CANCELLED"; o.ReservedCash = 0; }
    foreach (var session in await db.ResearchSessions.Where(x => x.Status == "ACTIVE").ToListAsync(ct)) { session.Status = "STOPPED"; session.EndedUtc = DateTime.UtcNow; session.Message = "Observation explicitly stopped."; }
    foreach (var suggestion in await db.ResearchSuggestions.Where(x => x.Status == "OPEN" || x.Status == "PENDING_ENTRY").ToListAsync(ct)) { suggestion.Status = suggestion.Status == "OPEN" ? "UNCERTAIN" : "CANCELLED"; suggestion.OutcomeNote = "Monitoring stopped without an observed exit; excluded from realized results."; }
    Event(db, "EMERGENCY STOP: all paper pending remainders cancelled; no holdings liquidated."); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return state;
  }, ct);
  public Task<TradingControlState> RecoverAsync(string confirmation, CancellationToken ct = default) => Locked(async db =>
  {
    if (confirmation != "START PAPER") throw new InvalidOperationException("Type START PAPER to confirm recovery.");
    if ((await db.ControlStates.SingleAsync(ct)).State == EngineState.OBSERVING) throw new InvalidOperationException("Stop observation before starting the legacy DEMO simulator.");
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    var a = await db.Accounts.SingleAsync(ct); var positions = await db.Positions.ToListAsync(ct);
    if (a.Cash < 0 || positions.Any(x => x.Quantity < 0)) throw new InvalidOperationException("Paper ledger reconciliation failed.");
    // Cancel uncertain pending remainders before allowing another session.
    foreach (var o in await db.Orders.Where(x => x.Status == "PENDING" || x.Status == "PARTIAL").ToListAsync(ct)) { o.Status = "CANCELLED"; o.ReservedCash = 0; }
    var state = await db.ControlStates.SingleAsync(ct); state.State = EngineState.PAPER_RUNNING; state.Reason = "Paper ledger reconciled; manual simulation enabled. Automated strategies unavailable."; state.UpdatedUtc = DateTime.UtcNow;
    Event(db, "Manual recovery: START PAPER confirmed."); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return state;
  }, ct);
  public Task<Order> SubmitPaperAsync(PaperOrderRequest request, CancellationToken ct = default) => Locked(async db =>
  {
    if (!Guid.TryParse(request.ClientOrderId, out _) || request.Symbol != "DEMO" || request.Quantity <= 0 || request.Quantity > 1000 || request.Price <= 0 || request.Price > 100000 || request.Side is not ("BUY" or "SELL")) throw new InvalidOperationException("Use a UUID client order ID, DEMO symbol, BUY/SELL, positive whole quantity and positive price. This endpoint uses simulated prices only.");
    // Idempotent replays return the first outcome even after an emergency stop.
    var previous = await db.Orders.SingleOrDefaultAsync(x => x.ClientOrderId == request.ClientOrderId, ct);
    if (previous != null) { if (previous.Symbol != request.Symbol || previous.Quantity != request.Quantity || previous.Side != request.Side || previous.Price != decimal.Round(request.Price * (1 + (request.Side == "BUY" ? 1 : -1) * options.SlippageBps / 10000), 4)) throw new InvalidOperationException("Client order ID already used for a different request."); return previous; }
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    var state = await db.ControlStates.SingleAsync(ct);
    if (state.State != EngineState.PAPER_RUNNING) throw new InvalidOperationException("Paper engine is stopped. Recover manually first.");
    var a = await db.Accounts.SingleAsync(ct); var p = await PortfolioAsync(db, ct);
    var active = await db.Orders.Where(x => x.Status == "PENDING" || x.Status == "PARTIAL").ToListAsync(ct);
    var price = decimal.Round(request.Price * (1 + (request.Side == "BUY" ? 1 : -1) * options.SlippageBps / 10000), 4);
    var full = price * request.Quantity + TradingCosts.Estimate(price * request.Quantity, false) + (request.SimulatePartialFill ? 0.02m : 0m);
    if (request.Side == "BUY") risk.CheckBuy(a.Cash, p.ReservedCash, p.InvestedAmount, p.OpenPositions, active.Any(x => x.Side == "BUY"), full, p.TodayNetPnl, p.RealizedPnl + p.UnrealizedPnl);
    else
    {
      if (active.Count > 0) throw new InvalidOperationException("Pending order must finish or be cancelled before selling.");
      var position = await db.Positions.SingleOrDefaultAsync(x => x.Symbol == request.Symbol, ct);
      if (position == null || position.Quantity < request.Quantity) throw new InvalidOperationException("Short selling is disabled.");
      if (price * request.Quantity <= TradingCosts.Estimate(price * request.Quantity, true)) throw new InvalidOperationException("Estimated exit fees exceed sale proceeds.");
    }
    var order = new Order { ClientOrderId = request.ClientOrderId, Symbol = request.Symbol, Side = request.Side, Quantity = request.Quantity, Price = price };
    db.Orders.Add(order); await db.SaveChangesAsync(ct);
    var fill = request.SimulatePartialFill && request.Quantity > 1 ? Math.Max(1, request.Quantity / 2) : request.Quantity;
    await FillAsync(db, order, fill, ct);
    if (order.Side == "BUY" && fill < order.Quantity) order.ReservedCash = price * (order.Quantity - fill) + TradingCosts.Estimate(price * (order.Quantity - fill), false) + 0.01m;
    Event(db, $"Paper order {order.Id}: {order.Side} {order.Quantity} {order.Symbol}, {order.Status}. SIMULATED data.");
    await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return order;
  }, ct);
  static async Task FillAsync(TradingDbContext db, Order o, int qty, CancellationToken ct)
  {
    var a = await db.Accounts.SingleAsync(ct); var position = await db.Positions.SingleOrDefaultAsync(x => x.Symbol == o.Symbol, ct);
    var turnover = o.Price * qty; var fee = TradingCosts.Estimate(turnover, o.Side == "SELL");
    // Charge the per-order DP estimate only once across partial sell fills.
    if (o.Side == "SELL" && o.FilledQuantity > 0) fee -= 15.93m;
    if (o.Side == "BUY")
    {
      if (a.Cash < turnover + fee) throw new InvalidOperationException("Insufficient paper funds at fill.");
      a.Cash -= turnover + fee;
      if (position == null) { position = new Position { Symbol = o.Symbol }; db.Positions.Add(position); }
      position.Quantity += qty; position.Cost += turnover + fee;
    }
    else
    {
      if (position == null || position.Quantity < qty) throw new InvalidOperationException("Paper position reconciliation failed.");
      if (a.Cash + turnover - fee < 0) throw new InvalidOperationException("Insufficient cash to cover fees for this partial sell fill.");
      var basis = position.Cost * qty / position.Quantity; position.Quantity -= qty; position.Cost -= basis; a.Cash += turnover - fee;
      db.Trades.Add(new Trade { OrderId = o.Id, Symbol = o.Symbol, Quantity = qty, Proceeds = turnover, Cost = basis, Charges = fee, NetProfit = turnover - fee - basis });
    }
    position.LastPrice = o.Price; position.PriceUtc = DateTime.UtcNow; a.TotalCharges += fee; o.Charges += fee; o.FilledQuantity += qty; o.Status = o.FilledQuantity == o.Quantity ? "FILLED" : "PARTIAL"; o.ReservedCash = 0;
  }
  public async Task CompletePendingAsync(CancellationToken ct = default) => await Locked(async db =>
  {
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    if ((await db.ControlStates.SingleAsync(ct)).State != EngineState.PAPER_RUNNING) return false;
    foreach (var o in await db.Orders.Where(x => x.Status == "PARTIAL").ToListAsync(ct)) await FillAsync(db, o, o.Quantity - o.FilledQuantity, ct);
    await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true;
  }, ct);
}
