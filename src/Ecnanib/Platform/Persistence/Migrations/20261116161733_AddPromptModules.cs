using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Ecnanib.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    [DestructiveMigration]
    public partial class AddPromptModules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 5,
                column: "is_active",
                value: false);

            migrationBuilder.UpdateData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 6,
                column: "is_active",
                value: false);

            migrationBuilder.UpdateData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 7,
                column: "is_active",
                value: false);

            migrationBuilder.UpdateData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 8,
                column: "is_active",
                value: false);

            migrationBuilder.UpdateData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 9,
                column: "is_active",
                value: false);

            migrationBuilder.UpdateData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 10,
                column: "is_active",
                value: false);

            migrationBuilder.UpdateData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 11,
                column: "is_active",
                value: false);

            migrationBuilder.InsertData(
                table: "prompt_templates",
                columns: new[] { "id", "is_active", "kind", "subject", "text", "version" },
                values: new object[,]
                {
                    { 12, true, "Market", "Spot", "Market: Binance Spot. The agent buys the asset itself with the quote currency, and sells what it bought to exit.\nLong-only. The allowed actions are open long, hold, close, and adjust protection. Open long buys the asset. Hold changes nothing. Close sells the open position. Adjust protection moves the stop-loss or the take-profit of the open position.\nNo leverage and no shorting. A bearish view means staying in the quote currency: close an open long, or hold with no position. Never plan a short or a leveraged position.", 2 },
                    { 13, true, "Market", "Futures", "Market: Binance USDⓈ-margined perpetual Futures.\nLong and short. The allowed actions are open long, open short, hold, close, reverse, and adjust protection. Reverse closes the open position and then opens one on the opposite side.\nLeverage: the system sets the leverage and the quantity, and never goes above the leverage cap of the stated limits. Choose neither: shape the trade through the stop-loss and the risk fraction.\nIsolated margin: only the margin of the position itself backs it, and the exchange liquidates the position when the loss nears that margin.\nLiquidation distance: the estimated liquidation price must be further from the entry than the stop-loss, by the liquidation buffer of the stated limits, or the plan is rejected. Keep the stop-loss well inside it.\nFunding: an open position pays or receives funding at each funding time. Weigh the funding cost over the expected holding period against the target of the trade.\nExits are reduce-only: the stop-loss and the take-profit only reduce the position, and never open one on the other side.", 2 },
                    { 14, true, "Timeframe", "15m", "Style: intraday momentum and pullbacks. Look for moves that are under way on the 15m candles and for pullbacks within them. Pay close attention to fees and spread: a 15m move is small, so take only setups whose target is worth several times the cost of entering and leaving.\nContext: closed 15m candles, with 1H and 4H candles for context. Read the direction on 4H and 1H first, then time the entry on 15m. A setup against both higher timeframes needs a clear reason.\nExpected holding period: minutes to hours. Plan a stop-loss and a take-profit that a move of that length can reach, and do not plan to hold a position for days.", 2 },
                    { 15, true, "Timeframe", "1H", "Style: intraday to short swing. Trade the moves that take shape over one session or a few: a trend that continues after a pullback, a break of a range that holds, or a turn at a level that the higher timeframes show.\nContext: closed 1H candles, with 4H and 1D candles for context. Read the direction on 1D and 4H first, then find the setup and the entry on 1H.\nExpected holding period: hours to days. Give the stop-loss room for the swings of an hour, and set a take-profit that can be reached within days.", 2 },
                    { 16, true, "Timeframe", "4H", "Style: swing. Trade the swings of a trend or of a wide range, from one turn to the next, and let the moves inside a single 4H candle pass.\nContext: closed 4H candles, with 1D and 1W candles for context. Read the trend and the main levels on 1W and 1D first, then find the setup and the entry on 4H.\nExpected holding period: days to weeks. Place the stop-loss beyond the level that would prove the swing wrong, and set the take-profit at the next main level.", 2 },
                    { 17, true, "Timeframe", "1D", "Style: position. Trade the main trend and its large turns, and leave the moves of a single day aside.\nContext: closed 1D candles, with 1W candles for context. Read the trend and the main levels on 1W first, then find the setup and the entry on 1D.\nExpected holding period: weeks to months. Use a stop-loss beyond the level that would end the trend, and a take-profit far enough to be worth the wait.", 2 },
                    { 18, true, "Profile", "Aggressive", "Profile: Aggressive.\nLook for opportunities actively. At every cycle, ask whether there is a trade to take, and do not wait for a perfect setup.\nAct on moderate-conviction setups. A setup that is more likely right than wrong, with a reward that meets the minimum reward-to-risk ratio, is enough to act on. Say hold when nothing reaches that.\nUse the full risk allowed. When you act, plan with the whole risk per trade of the stated limits, and reduce it only for a reason that you name.\nTrade both directions on Futures: take a short as readily as a long when the read is bearish. On Spot, a bearish read means no position.\nAlways define a stop-loss. Every plan to open a position names its stop-loss, with no exception.\nStay within the stated limits. The limits of the agent context are fixed: never plan beyond them, and never suggest a way around them.", 2 }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 12);

            migrationBuilder.DeleteData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 13);

            migrationBuilder.DeleteData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 14);

            migrationBuilder.DeleteData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 15);

            migrationBuilder.DeleteData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 16);

            migrationBuilder.DeleteData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 17);

            migrationBuilder.DeleteData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 18);

            migrationBuilder.UpdateData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 5,
                column: "is_active",
                value: true);

            migrationBuilder.UpdateData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 6,
                column: "is_active",
                value: true);

            migrationBuilder.UpdateData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 7,
                column: "is_active",
                value: true);

            migrationBuilder.UpdateData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 8,
                column: "is_active",
                value: true);

            migrationBuilder.UpdateData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 9,
                column: "is_active",
                value: true);

            migrationBuilder.UpdateData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 10,
                column: "is_active",
                value: true);

            migrationBuilder.UpdateData(
                table: "prompt_templates",
                keyColumn: "id",
                keyValue: 11,
                column: "is_active",
                value: true);
        }
    }
}
