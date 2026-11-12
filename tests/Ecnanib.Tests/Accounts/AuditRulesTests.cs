using System.Reflection;
using Ecnanib.Accounts;
using Ecnanib.Tests.Architecture;
using Ecnanib.Tests.Persistence;
using Ecnanib.Tests.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// The audit trail records the security- and money-relevant actions that the requirement names
/// (NFR-LOG-7), and an event holds no secret. These tests hold what is recorded to
/// <see cref="AuditRules"/>: what each action is recorded as, which test shows it, and what an
/// action that is not recorded yet waits for.
/// </summary>
public class AuditRulesTests(EcnanibApplicationFactory application) : IClassFixture<EcnanibApplicationFactory>
{
    [Fact]
    public void The_actions_are_the_ones_the_requirement_names()
    {
        // Pinned, as the requirement lists them, so that a change shows in a review as what it
        // is: a decision about what the audit trail is for.
        Assert.Equal(
            [
                "sign-ins",
                "two-factor changes",
                "key changes",
                "agent start, pause, and stop",
                "mode switches",
                "manual closes",
                "risk setting changes",
                "operator actions",
            ],
            AuditRules.Requirement.Keys);

        // Each is recorded, or waits for something, and never both.
        Assert.All(AuditRules.Requirement, action =>
        {
            var (recorded, waits) = (action.Value.RecordedAs.Count > 0, action.Value.WaitsFor is not null);

            Assert.True(recorded != waits, $"\"{action.Key}\" must be recorded, or wait for what will do it, and not both.");
            Assert.True(recorded || !string.IsNullOrWhiteSpace(action.Value.What), $"\"{action.Key}\" does not say what is to be recorded.");
        });
    }

    [Fact]
    public void Every_action_that_is_recorded_is_one_of_the_requirement_or_says_why_it_is_recorded()
    {
        var listed = AuditRules.Requirement.Values.SelectMany(action => action.RecordedAs)
            .Concat(AuditRules.BeyondTheRequirement.Keys)
            .ToList();

        var unlisted = Enum.GetValues<AuditAction>().Except(listed).ToList();
        Assert.True(
            unlisted.Count == 0,
            $"These members of {nameof(AuditAction)} are not listed in {nameof(AuditRules)}. List each under the action of " +
            $"the requirement that it records, or in {nameof(AuditRules.BeyondTheRequirement)} with why: {string.Join(", ", unlisted)}");

        Assert.Equal(listed.Count, listed.Distinct().Count());
        Assert.All(AuditRules.BeyondTheRequirement, action =>
            Assert.False(string.IsNullOrWhiteSpace(action.Value), $"{action.Key} does not say why it is recorded."));

        // Pinned, like the list above.
        Assert.Equal([AuditAction.Reauthenticated, AuditAction.PasswordChanged], AuditRules.BeyondTheRequirement.Keys);
    }

    [Fact]
    public void Every_action_that_is_recorded_has_a_test_that_shows_it_being_recorded()
    {
        var marked = typeof(AuditRulesTests).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(method => method.GetCustomAttribute<ShowsAuditOfAttribute>() is not null)
            .ToList();

        // The mark is on a test, or it shows nothing.
        Assert.All(marked, method => Assert.True(
            method.GetCustomAttributes<FactAttribute>().Any(),
            $"{method.DeclaringType!.Name}.{method.Name} is marked as showing an audit event and is not a test."));

        var shown = marked.SelectMany(method => method.GetCustomAttribute<ShowsAuditOfAttribute>()!.Actions).ToHashSet();

        var notShown = Enum.GetValues<AuditAction>().Where(action => !shown.Contains(action)).ToList();
        Assert.True(
            notShown.Count == 0,
            "No test shows that these actions are recorded in the audit trail when they are done. Write one for each, and " +
            $"mark it [ShowsAuditOf({nameof(AuditAction)}.…)]: {string.Join(", ", notShown)}");
    }

    [Fact]
    public void An_action_that_is_not_recorded_yet_waits_for_something_that_is_not_built()
    {
        using var model = ModelOnly.Context();

        var built = new List<string>();

        foreach (var (action, coverage) in AuditRules.Requirement.Where(action => action.Value.WaitsFor is not null))
        {
            var reason = coverage.WaitsFor == AuditRules.OperatorFunctions
                ? WhyOperatorFunctionsAreBuilt()
                : model.Model.GetEntityTypes().Any(entity => IsWithin(entity.ClrType.Namespace, coverage.WaitsFor!))
                    ? $"{coverage.WaitsFor} stores rows of its own"
                    : null;

            if (reason is not null)
            {
                built.Add($"  \"{action}\": {reason}. {coverage.What}");
            }
        }

        Assert.True(
            built.Count == 0,
            "The audit trail must record these actions (NFR-LOG-7), and what does them is built now. For each: add its " +
            $"members to {nameof(AuditAction)}, record them with {nameof(AuditTrail)}.{nameof(AuditTrail.RecordAsync)} in the " +
            $"transaction of the action, mark the test that shows it, and list the members in {nameof(AuditRules)}. If one of " +
            "them is not built by this change, say there what it waits for instead. See docs/accounts.md:" +
            Environment.NewLine + string.Join(Environment.NewLine, built));
    }

    [Fact]
    public void What_tells_that_an_operator_function_is_built_can_see_one()
    {
        // The two ways an operator function asks whether its user is an operator, as
        // docs/ownership.md has them: the policy on a page or an endpoint, and IsOperator. The
        // test above looks for both, so both must be visible to it.
        //
        // A page says who may open it with an attribute, which is among what its endpoint
        // carries: the account page is for signed-in users.
        var account = Assert.Single(Endpoints(), endpoint => endpoint.RoutePattern.RawText == "/account");
        Assert.NotEmpty(account.Metadata.GetOrderedMetadata<IAuthorizeData>());

        // And a call of IsOperator is seen where there is one: the policy itself makes it.
        Assert.Contains(CallersOfIsOperator(), caller => IsWithin(caller, "Ecnanib.Accounts"));
    }

    [Fact]
    public void Nothing_that_makes_an_audit_event_takes_text()
    {
        // An event is made from an action of a fixed set, a target that is made from an id, and
        // an origin that is read from a connection. So nothing that a request brought, such as
        // a key or a password, can be written into the trail, whatever records the event.
        var takers = AuditRules.FindTextTakers(typeof(AuditEvent), typeof(AuditTrail), typeof(AuditTarget), typeof(AuditOrigin));

        Assert.True(
            takers.Count == 0,
            "An audit event holds no secret, because nothing that makes one takes text. These would:" +
            Environment.NewLine + string.Join(Environment.NewLine, takers.Select(taker => $"  {taker}")));

        // Nor can an event, a target, or an origin be made past the trail: none has a
        // constructor that anything else could call.
        const BindingFlags every = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        Assert.All(
            new[] { typeof(AuditEvent), typeof(AuditTarget), typeof(AuditOrigin) },
            type =>
            {
                Assert.True(type.IsSealed, $"{type.Name} can be derived from.");
                Assert.All(type.GetConstructors(every), constructor => Assert.True(
                    constructor.IsPrivate, $"{type.Name} has a constructor that is not private."));
            });
    }

    [Fact]
    public void A_member_that_takes_text_is_found()
    {
        Assert.Equal(
            [
                "Planted..ctor: takes String target",
                "Planted.Describe: takes Object anything",
                "Planted.Record: takes String origin",
            ],
            AuditRules.FindTextTakers(typeof(Planted)));
    }

    /// <summary>
    /// Why the operator functions count as built, or null while none is: an endpoint that
    /// requires the operator policy, or a call of <see cref="OperatorAccess.IsOperator"/> from
    /// outside Accounts.
    /// </summary>
    private string? WhyOperatorFunctionsAreBuilt()
    {
        var guarded = Endpoints()
            .Where(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(data => data.Policy == OperatorAccess.Policy))
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .FirstOrDefault();

        if (guarded is not null)
        {
            return $"{guarded} requires the operator policy";
        }

        var caller = CallersOfIsOperator().FirstOrDefault(caller => !IsWithin(caller, "Ecnanib.Accounts"));

        return caller is null ? null : $"{caller} asks whether its user is an operator";
    }

    /// <summary>The page and endpoint routes that the running application serves.</summary>
    private IEnumerable<RouteEndpoint> Endpoints() =>
        application.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

    /// <summary>The types of the application that call <see cref="OperatorAccess.IsOperator"/>.</summary>
    private static IReadOnlyList<string> CallersOfIsOperator() =>
        DependencyChecker.FindCallersOf(
            typeof(Program).Assembly, $"{typeof(OperatorAccess).FullName}::{nameof(OperatorAccess.IsOperator)}");

    /// <summary>True when a type or a namespace is in a namespace, or nested in it.</summary>
    private static bool IsWithin(string? name, string @namespace) =>
        name is not null && (name == @namespace || name.StartsWith(@namespace + ".", StringComparison.Ordinal));

    /// <summary>A type with members that take text, and with members that the rule leaves alone.</summary>
    private sealed class Planted
    {
        // Not fine: anything could call these with what a request brought.
        public Planted(string target) => _ = target;

        internal static void Record(Guid userId, string origin) => _ = (userId, origin);

        public string Describe(object anything) => anything.ToString()!;

        // Fine: private, or without text.
        private Planted(string kind, Guid id) => _ = (kind, id);

        public static Planted Of(Guid id) => new("planted", id);

        private static string Text(string of) => of;

        public string Kind() => Text("planted");
    }
}
