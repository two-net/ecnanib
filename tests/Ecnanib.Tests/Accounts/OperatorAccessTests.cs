using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using Ecnanib.Accounts;
using Ecnanib.Tests.BackgroundWork;
using Ecnanib.Tests.Logging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ecnanib.Tests.Accounts;

/// <summary>
/// Operator functions are checked separately from ownership (NFR-SEC-3). An operator is a user
/// whose verified address the configuration file lists (FR-OPS-1). The check opens nothing that
/// belongs to another user, and owning something satisfies no part of it. The functions
/// themselves are not built yet, so a handler of the tests stands for them: it requires the
/// policy, as they will.
/// </summary>
public class OperatorAccessTests
{
    private const string Ada = "ada@example.com";
    private const string Olive = "olive@example.com";

    private const string RefusedTemplate = "User {UserId} was refused {Path}, which is not for them";

    private static readonly DateTimeOffset Morning = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_operator_function_is_done_for_a_user_whose_verified_address_the_file_lists()
    {
        var functions = new OperatorFunctions();
        using var application = Application(functions);
        using var client = application.CreateClient();
        var olive = await application.RegisterVerifiedAsync(Olive);
        await client.SignInAsync(Olive);

        // The file writes the address with capitals. An account stores it without.
        Assert.Equal("Done", await client.PageAsync(OperatorFunctions.Path));
        Assert.Equal([olive.Id], functions.DoneFor);
        Assert.DoesNotContain(application.Events(), written => written.Template == RefusedTemplate);
    }

    [Fact]
    public async Task It_is_refused_to_a_user_whom_the_file_does_not_list()
    {
        var functions = new OperatorFunctions();
        using var application = Application(functions);
        using var client = application.CreateClient();

        // A user like any other, with an account and everything in it that is theirs.
        var ada = await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Ada);

        using var answer = await client.GetAsync(OperatorFunctions.Path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Empty(functions.DoneFor);

        // They stay signed in, and the log says who was refused what.
        Assert.Contains("You are signed in as ada@example.com.", await client.PageAsync("/account"));

        var refused = Assert.Single(application.Events(), written => written.Template == RefusedTemplate);
        Assert.Equal("Warning", refused.Level);
        Assert.Equal(ada.Id.ToString(), refused.Text("UserId"));
        Assert.Equal(OperatorFunctions.Path, refused.Text("Path"));
    }

    [Fact]
    public async Task A_visitor_is_sent_to_sign_in_and_is_an_operator_or_not_when_they_come_back()
    {
        var functions = new OperatorFunctions();
        using var application = Application(functions);
        using var operators = application.CreateClient();
        using var others = application.CreateClient();
        await application.RegisterVerifiedAsync(Olive);
        await application.RegisterVerifiedAsync(Ada);

        var (asked, _) = await operators.OpenAsync(OperatorFunctions.Path);
        Assert.Equal(SessionAuthentication.PageWith(SessionAuthentication.SignInPath, OperatorFunctions.Path), asked);
        Assert.Empty(functions.DoneFor);

        Assert.Equal(OperatorFunctions.Path, (await operators.SignInAsync(Olive, page: asked)).Path);
        Assert.Single(functions.DoneFor);

        // Someone else comes back to a refusal.
        await others.SignInAsync(Ada, page: asked);
        Assert.Single(functions.DoneFor);
    }

    [Fact]
    public async Task An_address_that_is_listed_and_not_verified_is_no_operator()
    {
        var functions = new OperatorFunctions();
        using var application = Application(functions);
        using var client = application.CreateClient();

        // Anyone can register the address of an operator, with a password of their own, and
        // sign in with it. Only whoever reads the mail of the address can verify it.
        await application.RegisterAsync(Olive);
        Assert.Equal("/", (await client.SignInAsync(Olive)).Path);

        using var answer = await client.GetAsync(OperatorFunctions.Path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Empty(functions.DoneFor);

        // Verified, the same session is an operator's from its next request on.
        Assert.True(await application.VerifyAsync(AccountsApplication.TokenIn(application.Email.Sent[^1])));

        Assert.Equal("Done", await client.PageAsync(OperatorFunctions.Path));
    }

    [Fact]
    public async Task Being_an_operator_opens_nothing_that_belongs_to_another_user()
    {
        var functions = new OperatorFunctions();
        var clock = new TestClock(Morning);
        using var application = Application(functions, clock);
        var (ada, _, adasCodes) = await application.RegisterWithTwoFactorAsync(Ada, clock);
        var (_, app, _) = await application.RegisterWithTwoFactorAsync(Olive, clock);
        using var client = application.CreateClient();
        using var adas = application.CreateClient();
        await adas.SignInAsync(Ada);

        await client.SignInAsync(Olive);
        Assert.Equal("/", (await client.EnterCodeAsync(app.CodeAt(clock.GetUtcNow()))).Path);
        Assert.Equal("Done", await client.PageAsync(OperatorFunctions.Path));

        // What is asked for a user is asked for the operator as for anyone: a recovery code of
        // Ada is a wrong code, and the page of the account is the operator's own.
        Assert.Contains("That code is not right.", (await client.ConfirmIdentityAsync(code: adasCodes[0])).Page);
        Assert.Contains(
            "That code is not right.",
            (await client.SubmitAsync("/account/two-factor/turn-off", "turn-off", ("Code", adasCodes[0]))).Page);
        Assert.Contains("You are signed in as olive@example.com.", await client.PageAsync("/account"));

        Assert.Equal(10, StoredRecoveryCode.All(application.Database).Count(code => code.UserId == ada.Id));
        Assert.NotNull(StoredTwoFactor.Of(application.Database, Ada).EnabledAt);
        Assert.Equal(ada.Id, Assert.Single(StoredPendingSignIn.All(application.Database)).UserId);
    }

    [Fact]
    public async Task Who_is_an_operator_is_stored_nowhere()
    {
        var functions = new OperatorFunctions();
        using var application = Application(functions);
        using var client = application.CreateClient();
        await application.RegisterVerifiedAsync(Olive);
        await application.RegisterVerifiedAsync(Ada);
        await client.SignInAsync(Olive);
        await client.PageAsync(OperatorFunctions.Path);

        // The two accounts differ in nothing but what any two accounts differ in: no column
        // says which of them is an operator. The file says it, and is asked on every request.
        var columns = application.Database.Column(
            "SELECT column_name::text FROM information_schema.columns WHERE table_name = 'users' ORDER BY ordinal_position");

        Assert.Contains("email", columns);
        Assert.DoesNotContain(columns, column =>
            column.Contains("operator", StringComparison.OrdinalIgnoreCase)
            || column.Contains("role", StringComparison.OrdinalIgnoreCase)
            || column.Contains("type", StringComparison.OrdinalIgnoreCase)
            || column.Contains("kind", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("operator", application.EverythingStored(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Someone_who_is_not_signed_in_is_no_operator()
    {
        Assert.False(OperatorAccess.IsOperator(new ClaimsPrincipal()));
        Assert.False(OperatorAccess.IsOperator(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.False(OperatorAccess.IsOperator(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString())], SessionAuthentication.Scheme))));
    }

    /// <summary>The application with one operator in its configuration file, written as nobody stores an address.</summary>
    private static AccountsApplication Application(OperatorFunctions functions, TestClock? clock = null) =>
        new(
            clock: clock,
            settings: file => file.WithJson("Application:Operators", "[ \"Olive@Example.COM\" ]"),
            host: host => host.HandleAtTheEnd(functions.HandleAsync));

    /// <summary>
    /// Stands for the operator functions, which are not built yet. It requires the policy the way
    /// the framework does for a page or an endpoint that carries it: a visitor is asked to sign
    /// in, and a user whom the policy does not let through is refused. It keeps whom it served.
    /// </summary>
    private sealed class OperatorFunctions
    {
        public const string Path = "/does-something-for-operators";

        private readonly ConcurrentQueue<Guid> _doneFor = new();

        /// <summary>The users the function was done for, in order.</summary>
        public IReadOnlyList<Guid> DoneFor => [.. _doneFor];

        public async Task<bool> HandleAsync(HttpContext request)
        {
            if (!request.Request.Path.StartsWithSegments(Path))
            {
                return false;
            }

            var authorization = request.RequestServices.GetRequiredService<IAuthorizationService>();
            if (!(await authorization.AuthorizeAsync(request.User, OperatorAccess.Policy)).Succeeded)
            {
                if (request.User.Identity is { IsAuthenticated: true })
                {
                    await request.ForbidAsync();
                }
                else
                {
                    await request.ChallengeAsync();
                }

                return true;
            }

            // The policy let the user through, and the check for code that has no policy agrees.
            Assert.True(OperatorAccess.IsOperator(request.User));

            _doneFor.Enqueue(SignedInUser.RequiredIdOf(request));
            await request.Response.WriteAsync("Done");

            return true;
        }
    }
}
