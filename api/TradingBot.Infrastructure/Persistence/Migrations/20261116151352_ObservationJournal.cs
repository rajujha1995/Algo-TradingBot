using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradingBot.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ObservationJournal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResearchAssessments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<long>(type: "INTEGER", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    AssessedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CandleCloseUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    EvidenceJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchAssessments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ResearchCoverage",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<long>(type: "INTEGER", nullable: false),
                    MinuteUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FreshSymbols = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchCoverage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ResearchSessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ResearchSuggestions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<long>(type: "INTEGER", nullable: false),
                    Strategy = table.Column<string>(type: "TEXT", nullable: false),
                    Symbol = table.Column<string>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    SuggestedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CandleCloseUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    EvidenceJson = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    CapitalFitQuantity = table.Column<int>(type: "INTEGER", nullable: false),
                    ProposedEntry = table.Column<decimal>(type: "TEXT", nullable: false),
                    EntryLimit = table.Column<decimal>(type: "TEXT", nullable: false),
                    Stop = table.Column<decimal>(type: "TEXT", nullable: false),
                    Target = table.Column<decimal>(type: "TEXT", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    EntryUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    MarkUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExitPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    ExitUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    GrossPnl = table.Column<decimal>(type: "TEXT", nullable: true),
                    EstimatedCosts = table.Column<decimal>(type: "TEXT", nullable: true),
                    NetPnl = table.Column<decimal>(type: "TEXT", nullable: true),
                    MaxFavorable = table.Column<decimal>(type: "TEXT", nullable: false),
                    MaxAdverse = table.Column<decimal>(type: "TEXT", nullable: false),
                    FeedGeneration = table.Column<long>(type: "INTEGER", nullable: false),
                    OutcomeNote = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchSuggestions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResearchAssessments_SessionId_Code",
                table: "ResearchAssessments",
                columns: new[] { "SessionId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResearchCoverage_SessionId_MinuteUtc",
                table: "ResearchCoverage",
                columns: new[] { "SessionId", "MinuteUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResearchSuggestions_Code_CandleCloseUtc_Strategy",
                table: "ResearchSuggestions",
                columns: new[] { "Code", "CandleCloseUtc", "Strategy" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResearchAssessments");

            migrationBuilder.DropTable(
                name: "ResearchCoverage");

            migrationBuilder.DropTable(
                name: "ResearchSessions");

            migrationBuilder.DropTable(
                name: "ResearchSuggestions");
        }
    }
}
