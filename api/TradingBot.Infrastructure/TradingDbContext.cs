using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TradingBot.Domain;
namespace TradingBot.Infrastructure;

public sealed class TradingDbContext(DbContextOptions<TradingDbContext> options) : DbContext(options)
{
  public DbSet<TradingControlState> ControlStates => Set<TradingControlState>();
  public DbSet<PaperAccount> Accounts => Set<PaperAccount>();
  public DbSet<Order> Orders => Set<Order>();
  public DbSet<Position> Positions => Set<Position>();
  public DbSet<Trade> Trades => Set<Trade>();
  public DbSet<SystemEvent> Events => Set<SystemEvent>();
  public DbSet<ResearchSession> ResearchSessions => Set<ResearchSession>();
  public DbSet<ResearchAssessment> ResearchAssessments => Set<ResearchAssessment>();
  public DbSet<ResearchSuggestion> ResearchSuggestions => Set<ResearchSuggestion>();
  public DbSet<ResearchCoverage> ResearchCoverage => Set<ResearchCoverage>();
  protected override void OnModelCreating(ModelBuilder model)
  {
    model.Entity<Order>().HasIndex(x => x.ClientOrderId).IsUnique();
    model.Entity<Position>().HasIndex(x => x.Symbol).IsUnique();
    model.Entity<Trade>().HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId);
    model.Entity<SystemEvent>().HasIndex(x => x.TimestampUtc);
    model.Entity<Order>().HasIndex(x => x.CreatedUtc);
    model.Entity<ResearchAssessment>().HasIndex(x => new { x.SessionId, x.Code }).IsUnique();
    model.Entity<ResearchSuggestion>().HasIndex(x => new { x.Code, x.CandleCloseUtc, x.Strategy }).IsUnique();
    model.Entity<ResearchCoverage>().HasIndex(x => new { x.SessionId, x.MinuteUtc }).IsUnique();
  }
}
public sealed class TradingDbFactory : IDesignTimeDbContextFactory<TradingDbContext>
{
  public TradingDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite("Data Source=design-time-only.db").Options);
}
