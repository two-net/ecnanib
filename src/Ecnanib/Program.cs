using Ecnanib.Accounts;
using Ecnanib.Ai;
using Ecnanib.Components;
using Ecnanib.Database;
using Ecnanib.Exchange;
using Ecnanib.Keys;
using Ecnanib.ModelSync;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddExchange(builder.Configuration);
builder.Services.AddAiProviders(builder.Configuration);
builder.Services.AddDatabase(builder.Configuration);
builder.Services.AddAccounts(builder.Configuration);
builder.Services.AddModelSync(builder.Configuration);
builder.Services.AddKeys(builder.Configuration);

var app = builder.Build();

// DB-02, NFR-ARCH-03: bring the schema up to date before anything else runs; a failure exits the process.
await app.MigrateDatabaseAsync();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// NFR-SEC-01: antiforgery tokens are bound to the signed-in identity, so authentication runs first. The explicit calls
// also keep WebApplication from inserting these two ahead of the HTTPS redirect.
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapAccountEndpoints();

app.Run();
