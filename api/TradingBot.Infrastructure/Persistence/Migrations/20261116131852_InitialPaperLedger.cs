using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Infrastructure.Persistence.Migrations
{
  /// <inheritdoc />
  public partial class InitialPaperLedger : Migration
  {
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
      migrationBuilder.CreateTable(
          name: "Accounts",
          columns: table => new
          {
            Id = table.Column<int>(type: "INTEGER", nullable: false)
                  .Annotation("Sqlite:Autoincrement", true),
            InitialCapital = table.Column<decimal>(type: "TEXT", nullable: false),
            Cash = table.Column<decimal>(type: "TEXT", nullable: false),
            TotalCharges = table.Column<decimal>(type: "TEXT", nullable: false)
          },
          constraints: table =>
          {
            table.PrimaryKey("PK_Accounts", x => x.Id);
          });

      migrationBuilder.CreateTable(
          name: "ControlStates",
          columns: table => new
          {
            Id = table.Column<int>(type: "INTEGER", nullable: false)
                  .Annotation("Sqlite:Autoincrement", true),
            State = table.Column<int>(type: "INTEGER", nullable: false),
            Reason = table.Column<string>(type: "TEXT", nullable: false),
            UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
          },
          constraints: table =>
          {
            table.PrimaryKey("PK_ControlStates", x => x.Id);
          });

      migrationBuilder.CreateTable(
          name: "Events",
          columns: table => new
          {
            Id = table.Column<long>(type: "INTEGER", nullable: false)
                  .Annotation("Sqlite:Autoincrement", true),
            TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
            Category = table.Column<string>(type: "TEXT", nullable: false),
            Message = table.Column<string>(type: "TEXT", nullable: false)
          },
          constraints: table =>
          {
            table.PrimaryKey("PK_Events", x => x.Id);
          });

      migrationBuilder.CreateTable(
          name: "Orders",
          columns: table => new
          {
            Id = table.Column<long>(type: "INTEGER", nullable: false)
                  .Annotation("Sqlite:Autoincrement", true),
            ClientOrderId = table.Column<string>(type: "TEXT", nullable: false),
            Symbol = table.Column<string>(type: "TEXT", nullable: false),
            Side = table.Column<string>(type: "TEXT", nullable: false),
            Quantity = table.Column<int>(type: "INTEGER", nullable: false),
            FilledQuantity = table.Column<int>(type: "INTEGER", nullable: false),
            Price = table.Column<decimal>(type: "TEXT", nullable: false),
            Charges = table.Column<decimal>(type: "TEXT", nullable: false),
            ReservedCash = table.Column<decimal>(type: "TEXT", nullable: false),
            Status = table.Column<string>(type: "TEXT", nullable: false),
            DataSource = table.Column<string>(type: "TEXT", nullable: false),
            CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
          },
          constraints: table =>
          {
            table.PrimaryKey("PK_Orders", x => x.Id);
          });

      migrationBuilder.CreateTable(
          name: "Positions",
          columns: table => new
          {
            Id = table.Column<long>(type: "INTEGER", nullable: false)
                  .Annotation("Sqlite:Autoincrement", true),
            Symbol = table.Column<string>(type: "TEXT", nullable: false),
            Quantity = table.Column<int>(type: "INTEGER", nullable: false),
            Cost = table.Column<decimal>(type: "TEXT", nullable: false),
            LastPrice = table.Column<decimal>(type: "TEXT", nullable: false),
            DataSource = table.Column<string>(type: "TEXT", nullable: false),
            OpenedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
            PriceUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
          },
          constraints: table =>
          {
            table.PrimaryKey("PK_Positions", x => x.Id);
          });

      migrationBuilder.CreateTable(
          name: "Trades",
          columns: table => new
          {
            Id = table.Column<long>(type: "INTEGER", nullable: false)
                  .Annotation("Sqlite:Autoincrement", true),
            OrderId = table.Column<long>(type: "INTEGER", nullable: false),
            Symbol = table.Column<string>(type: "TEXT", nullable: false),
            Quantity = table.Column<int>(type: "INTEGER", nullable: false),
            Proceeds = table.Column<decimal>(type: "TEXT", nullable: false),
            Cost = table.Column<decimal>(type: "TEXT", nullable: false),
            Charges = table.Column<decimal>(type: "TEXT", nullable: false),
            NetProfit = table.Column<decimal>(type: "TEXT", nullable: false),
            ClosedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
          },
          constraints: table =>
          {
            table.PrimaryKey("PK_Trades", x => x.Id);
            table.ForeignKey(
                      name: "FK_Trades_Orders_OrderId",
                      column: x => x.OrderId,
                      principalTable: "Orders",
                      principalColumn: "Id",
                      onDelete: ReferentialAction.Cascade);
          });

      migrationBuilder.CreateIndex(
          name: "IX_Events_TimestampUtc",
          table: "Events",
          column: "TimestampUtc");

      migrationBuilder.CreateIndex(
          name: "IX_Orders_ClientOrderId",
          table: "Orders",
          column: "ClientOrderId",
          unique: true);

      migrationBuilder.CreateIndex(
          name: "IX_Orders_CreatedUtc",
          table: "Orders",
          column: "CreatedUtc");

      migrationBuilder.CreateIndex(
          name: "IX_Positions_Symbol",
          table: "Positions",
          column: "Symbol",
          unique: true);

      migrationBuilder.CreateIndex(
          name: "IX_Trades_OrderId",
          table: "Trades",
          column: "OrderId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
      migrationBuilder.DropTable(
          name: "Accounts");

      migrationBuilder.DropTable(
          name: "ControlStates");

      migrationBuilder.DropTable(
          name: "Events");

      migrationBuilder.DropTable(
          name: "Positions");

      migrationBuilder.DropTable(
          name: "Trades");

      migrationBuilder.DropTable(
          name: "Orders");
    }
  }
}
