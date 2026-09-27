using ScanHarness.Components;
using ScanHarness.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Harness services.
builder.Services.AddHttpClient();
// Scoped (per Blazor circuit): the activity feed is a per-user view, and a scoped
// service avoids stale component subscriptions leaking across circuits. The audit
// log file that LogService writes remains shared across all users.
builder.Services.AddScoped<LogService>();
builder.Services.AddScoped<ToolCallService>();
// The agent "brain" that decides which tool to call. Swap this registration to use an LLM-backed brain.
builder.Services.AddScoped<IAgentBrain, KeywordAgentBrain>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
