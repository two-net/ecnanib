using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_providers",
                columns: table => new
                {
                    code = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_ai_providers", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "prompt_templates",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_prompt_templates", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    code = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.code);
                });

            migrationBuilder.InsertData(
                table: "ai_providers",
                columns: new[] { "code", "display_name" },
                values: new object[,]
                {
                    { "claude", "Claude" },
                    { "gemini", "Gemini" },
                    { "gpt", "GPT" },
                    { "grok", "Grok" }
                });

            migrationBuilder.InsertData(
                table: "prompt_templates",
                columns: new[] { "id", "is_active", "kind", "subject", "text", "version" },
                values: new object[,]
                {
                    { 1, true, "Role", "MarketAnalyst", "Reads price, volume, and indicators, and on Futures also funding and open interest. Structured result: Bias (bullish, bearish, or neutral), key price levels, volatility read.", 1 },
                    { 2, true, "Role", "SentimentAnalyst", "Reads news and social context for the symbol and the wider market. Structured result: Bias, notable events, confidence, and whether live data was available.", 1 },
                    { 3, true, "Role", "Strategist", "Combines both reports, recalled lessons, and the current position into a trade plan. Structured result: Action, entry type and price, stop-loss, take-profit, risk fraction, rationale.", 1 },
                    { 4, true, "Role", "RiskManager", "Reviews the plan independently. Structured result: Approve, reduce, or veto, with reasons.", 1 },
                    { 5, true, "Market", "Spot", "Long-only. Actions are open long, hold, close, and adjust protection. No leverage and no shorting; a bearish view means staying in the quote currency.", 1 },
                    { 6, true, "Market", "Futures", "Long and short. Adds open short and reverse. Covers leverage within the cap, isolated margin, liquidation distance, funding cost over the expected holding period, and reduce-only exits.", 1 },
                    { 7, true, "Timeframe", "15m", "Style: Intraday momentum and pullbacks, with close attention to fees and spread. Context: 15m candles, with 1H and 4H context. Expected holding period: Minutes to hours.", 1 },
                    { 8, true, "Timeframe", "1H", "Style: Intraday to short swing. Context: 1H candles, with 4H and 1D context. Expected holding period: Hours to days.", 1 },
                    { 9, true, "Timeframe", "4H", "Style: Swing. Context: 4H candles, with 1D and 1W context. Expected holding period: Days to weeks.", 1 },
                    { 10, true, "Timeframe", "1D", "Style: Position. Context: 1D candles, with 1W context. Expected holding period: Weeks to months.", 1 },
                    { 11, true, "Profile", "Aggressive", "Look for opportunities actively, act on moderate-conviction setups, use the full risk allowed, and trade both directions on Futures, while always defining a stop-loss and staying within the stated limits.", 1 }
                });

            migrationBuilder.InsertData(
                table: "roles",
                columns: new[] { "code", "display_name" },
                values: new object[,]
                {
                    { "MarketAnalyst", "Market Analyst" },
                    { "RiskManager", "Risk Manager" },
                    { "SentimentAnalyst", "Sentiment Analyst" },
                    { "Strategist", "Strategist" }
                });

            migrationBuilder.CreateIndex(
                name: "ix_prompt_templates_kind_subject",
                table: "prompt_templates",
                columns: new[] { "kind", "subject" },
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_prompt_templates_kind_subject_version",
                table: "prompt_templates",
                columns: new[] { "kind", "subject", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_providers");

            migrationBuilder.DropTable(
                name: "prompt_templates");

            migrationBuilder.DropTable(
                name: "roles");
        }
    }
}
