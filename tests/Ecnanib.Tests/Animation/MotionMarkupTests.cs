using System.Runtime.ExceptionServices;
using Ecnanib.DecisionEngine;
using Ecnanib.ExchangeGateway;
using Ecnanib.Shared;
using Ecnanib.Tests.Logging;
using Ecnanib.WebUi.Animation;
using Ecnanib.WebUi.Live;
using Ecnanib.WebUi.Pages.Agents;
using Ecnanib.WebUi.Pages.Dashboard;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecnanib.Tests.Animation;

/// <summary>
/// A changing figure counts from its old value to its new one (FR-ANI-2). The script of the
/// pages does the counting, and it is told a figure by how the page writes it: the number in an
/// element of its own, which says its value, inside the sentence that the page said before.
/// These tests hold what the server writes to that: the value of a figure is its number as it
/// is written, the sentence around it is unchanged, a figure that is not known has no number,
/// and a figure that changes on the connection of a page is changed in its place. See
/// docs/motion.md.
/// </summary>
/// <remarks>
/// The marks on the pages themselves, the figures of the dashboard and of the screen of an
/// agent, the keys of the items of a list, and the state of an agent by its name, are held
/// where those pages are: DashboardPageTests, AgentScreenPageTests, and LivePageTests.
/// </remarks>
public class MotionMarkupTests
{
    private static readonly DateTimeOffset Morning = new(2026, 11, 24, 9, 0, 7, TimeSpan.Zero);

    private static readonly OpenPosition Held = new(
        Market.Spot,
        TradingMode.Demo,
        "BTCUSDT",
        "USDT",
        PositionSide.Long,
        Quantity: 0.01m,
        EntryPrice: 100_000m,
        StopLoss: null,
        TakeProfit: null,
        OpenedAt: Morning.AddMinutes(-10));

    private static readonly LivePrice LastTraded = new(Market.Spot, TradingMode.Demo, "BTCUSDT", ExchangePriceKind.LastTraded);

    [Theory]
    [InlineData("1012.4", false, "1012.4")]
    [InlineData("0", false, "0")]
    [InlineData("+12.4", false, "12.4")]
    [InlineData("-5.99", false, "-5.99")]
    [InlineData("5.995", true, "-5.995")]
    [InlineData("-5.995", true, "-5.995")]
    public void The_value_of_a_figure_is_its_number_as_it_is_written(string number, bool isLoss, string value)
    {
        var figure = PageFigure.Of(number, "It made ", " USDT.", isLoss);

        // The value is worked out from the number that the page writes, and from nothing else:
        // a plus is dropped, and a loss that the words say has a minus.
        Assert.Equal(value, figure.Value);
        Assert.Equal(number, figure.Number);
        Assert.Equal($"It made {number} USDT.", figure.Text);
    }

    [Fact]
    public void A_figure_is_a_number_and_never_nothing()
    {
        Assert.Throws<ArgumentException>(() => PageFigure.Of(string.Empty));
        Assert.Throws<ArgumentNullException>(() => PageFigure.Of(null!));
    }

    [Fact]
    public void An_amount_and_a_profit_or_a_loss_are_figures_with_the_asset_after_them()
    {
        // An amount, to 2 decimal places, and what it is in.
        var amount = DashboardPages.AmountFigure(1012.404m, "USDT");

        Assert.Equal((string.Empty, "1012.4", "1012.4", " USDT"), (amount.Before, amount.Number, amount.Value, amount.After));
        Assert.Equal(DashboardPages.Amount(1012.404m, "USDT"), amount.Text);

        // A profit and a loss, each with its sign as the page writes it (FR-THM-5), and nothing
        // made with none.
        var profit = DashboardPages.SignedFigure(12.4m, "USDT");
        var loss = DashboardPages.SignedFigure(-5.994m, "USDT");
        var nothing = DashboardPages.SignedFigure(0.001m, "USDT");

        Assert.Equal(("+12.4", "12.4"), (profit.Number, profit.Value));
        Assert.Equal(("-5.99", "-5.99"), (loss.Number, loss.Value));
        Assert.Equal(("0", "0"), (nothing.Number, nothing.Value));
        Assert.Equal("+12.4 USDT", profit.Text);
        Assert.Equal(DashboardPages.Signed(-5.994m, "USDT"), loss.Text);

        // A share in percent, as the return of an agent is said.
        var share = AgentReportPages.SignedPercentFigure(1.254m);

        Assert.Equal(("+1.25", "1.25", " percent"), (share!.Number, share.Value, share.After));
        Assert.Equal(AgentReportPages.SignedPercent(1.254m), share.Text);
    }

    [Fact]
    public void A_figure_that_is_not_known_has_no_number()
    {
        // A figure that is not known is never written as a number: there is none to count to.
        Assert.Null(DashboardPages.AmountFigureOrNot(null, "USDT"));
        Assert.Null(DashboardPages.SignedFigureOrNot(null, "USDT"));
        Assert.Null(AgentReportPages.SignedPercentFigure(null));

        var unpriced = new AgentEquity(Guid.NewGuid(), TradingMode.Demo, "USDT", Capital: 1000m, Equity: null, MadeToday: null, Held, Price: null);

        Assert.Null(DashboardPages.PriceFigure(unpriced));
        Assert.Null(DashboardPages.WouldMakeFigure(unpriced));

        // And the page says that it is not known, as it did.
        Assert.Equal(DashboardPages.NotKnown, DashboardPages.AmountOrNot(null, "USDT"));
        Assert.Equal(DashboardPages.NotKnown, DashboardPages.PriceOf(unpriced));
        Assert.Equal(DashboardPages.NotKnown, DashboardPages.WouldMake(unpriced));
    }

    [Fact]
    public void A_price_is_a_figure_inside_the_sentence_that_says_it()
    {
        var price = new WatchedPrice(100_250m, Morning);

        // The price that a symbol last traded at, and the mark price: the number alone is the
        // figure, and when it was read stays as it is written.
        var traded = LivePages.PriceFigure(LastTraded, price, "USDT");
        var mark = LivePages.PriceFigure(LastTraded with { Market = Market.Futures, Kind = ExchangePriceKind.Mark }, price, quoteAsset: null);

        Assert.Equal(
            ("BTCUSDT last traded at ", "100250", "100250", " USDT, as of 24 November 2026, 09:00:07 UTC."),
            (traded.Before, traded.Number, traded.Value, traded.After));
        Assert.Equal(LivePages.Says(LastTraded, price, "USDT"), traded.Text);
        Assert.Equal("The mark price of BTCUSDT is 100250, as of 24 November 2026, 09:00:07 UTC.", mark.Text);

        // On the dashboard the price stands first, with when it was read after it.
        var priced = new AgentEquity(Guid.NewGuid(), TradingMode.Demo, "USDT", Capital: 1000m, Equity: 1002.5m, MadeToday: 2.5m, Held, price);

        Assert.Equal(("100250", ", as of 24 November 2026, 09:00:07 UTC"), (DashboardPages.PriceFigure(priced)!.Number, DashboardPages.PriceFigure(priced)!.After));
        Assert.Equal(DashboardPages.PriceOf(priced), DashboardPages.PriceFigure(priced)!.Text);
        Assert.Equal(("+2.5", "2.5"), (DashboardPages.WouldMakeFigure(priced)!.Number, DashboardPages.WouldMakeFigure(priced)!.Value));
    }

    [Fact]
    public void What_a_position_would_make_is_a_figure_whose_words_say_a_profit_or_a_loss()
    {
        // 0.01 that was entered at 100000 would make 2.5 at 100250, and lose 5.995 at 99400.5.
        var profit = LivePages.UnrealizedFigure(Held, new WatchedPrice(100_250m, Morning));
        var loss = LivePages.UnrealizedFigure(Held, new WatchedPrice(99_400.5m, Morning));

        Assert.Equal("At that price the position would make 2.5 USDT, before fees and funding.", profit!.Text);
        Assert.Equal(("2.5", "2.5"), (profit.Number, profit.Value));

        // A loss is written without a sign, and its value is less than nothing: so the script
        // does not count across a profit and a loss, whose words change at once.
        Assert.Equal("At that price the position would lose 5.995 USDT, before fees and funding.", loss!.Text);
        Assert.Equal(("5.995", "-5.995"), (loss.Number, loss.Value));

        // A position that would make nothing has no figure, and the page says so in words.
        Assert.Null(LivePages.UnrealizedFigure(Held, new WatchedPrice(100_000m, Morning)));
        Assert.Equal(LivePages.NothingMade, LivePages.Unrealized(Held, new WatchedPrice(100_000m, Morning)));
    }

    [Fact]
    public async Task A_figure_is_written_as_its_text_with_the_number_in_an_element_of_its_own()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());
        await using var scope = application.Services.CreateAsyncScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());

        Task<string> WrittenAsync(PageFigure? figure, string? otherwise) =>
            renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<FigureText>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(FigureText.Of)] = figure,
                    [nameof(FigureText.Otherwise)] = otherwise,
                }))).ToHtmlString());

        // The sentence as it was, with the number in an element that says its value, and
        // nothing between the number and what is around it.
        Assert.Equal(
            "BTCUSDT last traded at <data class=\"figure\" value=\"100250\">100250</data> USDT, as of 24 November 2026, 09:00:07 UTC.",
            await WrittenAsync(LivePages.PriceFigure(LastTraded, new WatchedPrice(100_250m, Morning), "USDT"), otherwise: null));

        Assert.Equal(
            "<data class=\"figure\" value=\"-5.99\">-5.99</data> USDT",
            await WrittenAsync(DashboardPages.SignedFigure(-5.99m, "USDT"), DashboardPages.NotKnown));

        // A figure that is not known is what the page says otherwise, as plain text.
        Assert.Equal(DashboardPages.NotKnown, await WrittenAsync(figure: null, DashboardPages.NotKnown));
    }

    [Fact]
    public async Task A_figure_that_changes_on_the_connection_of_a_page_is_changed_in_its_place()
    {
        using var application = new LoggedApplication(LoggedApplication.Settings());
        await using var scope = application.Services.CreateAsyncScope();
        using var written = new Written(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<ILoggerFactory>());

        // The position of an agent with the price that is followed, as the page first writes it.
        await written.ShowAsync(new WatchedPrice(100_250m, Morning));
        written.Changes.Clear();

        // Another price is read, a second later.
        await written.ShowAsync(new WatchedPrice(100_251.5m, Morning.AddSeconds(1)));

        // The number and its value are changed where they are, and so is when the price was
        // read. No element and no text is taken away or written anew: the script counts in the
        // text that is there, and the connection of the page writes to the same text next time.
        Assert.NotEmpty(written.Changes);
        Assert.Contains("UpdateText", written.Changes);
        Assert.Contains("SetAttribute", written.Changes);
        Assert.All(written.Changes, change => Assert.Contains(change, InPlace));
    }

    // What changes an element or a text where it is, and what leads to it, as the framework
    // names the changes that it makes.
    private static readonly string[] InPlace = ["UpdateText", "SetAttribute", "StepIn", "StepOut"];

    /// <summary>
    /// Writes the position of an agent again and again, as the connection of a page does, and
    /// keeps how each writing changed what was written before.
    /// </summary>
#pragma warning disable BL0006 // What a renderer is handed is the framework's to shape: read here is how a part was changed, and nothing else.
    private sealed class Written(IServiceProvider services, ILoggerFactory loggers) : Renderer(services, loggers)
    {
        private int? _part;

        /// <summary>How what was written was changed, one change after another, each by the name that the framework gives it.</summary>
        public List<string> Changes { get; } = [];

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        /// <summary>Writes the position with the price as it was read now.</summary>
        public Task ShowAsync(WatchedPrice price) =>
            Dispatcher.InvokeAsync(() =>
            {
                _part ??= AssignRootComponentId(InstantiateComponent(typeof(LivePositionView)));

                return RenderRootComponentAsync(
                    _part.Value,
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        [nameof(LivePositionView.Position)] = Held,
                        [nameof(LivePositionView.Followed)] = LastTraded,
                        [nameof(LivePositionView.Price)] = price,
                    }));
            });

        protected override void HandleException(Exception exception) => ExceptionDispatchInfo.Capture(exception).Throw();

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
        {
            for (var at = 0; at < renderBatch.UpdatedComponents.Count; at++)
            {
                var edits = renderBatch.UpdatedComponents.Array[at].Edits;

                for (var edit = 0; edit < edits.Count; edit++)
                {
                    Changes.Add(edits.Array[edits.Offset + edit].Type.ToString());
                }
            }

            return Task.CompletedTask;
        }
    }
#pragma warning restore BL0006
}
