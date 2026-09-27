using Microsoft.Extensions.Options;
using ScanHarness.Components;
using ScanHarness.Services;
using ScanHarness.Services.Llm;

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
// Agent "brains" that decide which tool to call: the rule-based keyword brain, plus one brain
// per model served by the OpenAI-compatible endpoint in the "Llm" config section (Ollama by default).
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection(LlmOptions.SectionName));
builder.Services.AddHttpClient<OpenAiCompatibleClient>((sp, http) =>
    OpenAiCompatibleClient.Configure(http, sp.GetRequiredService<IOptions<LlmOptions>>().Value));
builder.Services.AddSingleton<KeywordAgentBrain>();
// Scoped so each user (Blazor circuit) keeps their own brain choice.
builder.Services.AddScoped<AgentBrainSelector>();

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
