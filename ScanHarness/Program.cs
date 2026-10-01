using Microsoft.Extensions.Options;
using ScanHarness.Components;
using ScanHarness.Services;
using ScanHarness.Services.Llm;
using ScanHarness.Services.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Harness services.
builder.Services.AddHttpClient();

// Persistence: sessions, tool calls and log entries are saved to a local SQLite database so every
// session can be recreated after a restart. Swap the IActivityStore registration to use another store.
builder.Services.Configure<PersistenceOptions>(builder.Configuration.GetSection(PersistenceOptions.SectionName));
builder.Services.AddSingleton<IActivityStore>(sp =>
{
    var options = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
    var path = Path.IsPathRooted(options.DatabasePath)
        ? options.DatabasePath
        : Path.Combine(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath, options.DatabasePath);
    return new SqliteActivityStore(path);
});

// Scoped (per Blazor circuit): the selected user and session, the activity feed and the tool calls
// are per-user views. Everything they record is saved to the shared store and the audit log file.
builder.Services.AddScoped<SessionState>();
// Guardrails (Chat In, Chat Out, ToolRule) with each user's Off/Advise/Block settings.
builder.Services.AddScoped<ScanHarness.Services.Controls.ControlService>();
// Cross-user activity search for the admin's All activity view.
builder.Services.AddScoped<ActivityExplorer>();
builder.Services.AddScoped<LogService>();
builder.Services.AddScoped<ToolCallService>();

// Trust boundaries (docs/design/multi-agent-boundaries.md): each brain's, tool's and agent's policy
// (shared by everyone, editable on the Permissions tab), engagements and scope, the broker every
// boundary crossing goes through, and the agents.
builder.Services.Configure<ToolOptions>(builder.Configuration.GetSection(ToolOptions.SectionName));
builder.Services.Configure<ScanHarness.Services.Agents.AgentOptions>(builder.Configuration.GetSection(ScanHarness.Services.Agents.AgentOptions.SectionName));
builder.Services.Configure<ScanHarness.Services.Engagements.EngagementOptions>(builder.Configuration.GetSection(ScanHarness.Services.Engagements.EngagementOptions.SectionName));
builder.Services.AddSingleton<ScanHarness.Services.Boundaries.ComponentPolicyService>();
builder.Services.AddScoped<ScanHarness.Services.Engagements.EngagementService>();
builder.Services.AddScoped<ScanHarness.Services.Boundaries.BoundaryBroker>();
builder.Services.AddScoped<ScanHarness.Services.Agents.AgentRunner>();
// Agent "brains" that decide which tool to call: the rule-based keyword brain, plus one brain
// per model served by the OpenAI-compatible endpoint in the "Llm" config section (Ollama by default).
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection(LlmOptions.SectionName));
builder.Services.AddHttpClient<OpenAiCompatibleClient>((sp, http) =>
    OpenAiCompatibleClient.Configure(http, sp.GetRequiredService<IOptions<LlmOptions>>().Value));
builder.Services.AddSingleton<KeywordAgentBrain>();
// Scoped so each user (Blazor circuit) keeps their own brain choice.
builder.Services.AddScoped<AgentBrainSelector>();

var app = builder.Build();

// Guardrail controls come from a JSON file (Controls:DefinitionsPath, relative to the content root).
// An invalid file stops startup with a message listing every problem, rather than silently
// running without a guardrail.
var controlsPath = app.Configuration["Controls:DefinitionsPath"] ?? ScanHarness.Services.Controls.ControlCatalog.DefaultFileName;
if (!Path.IsPathRooted(controlsPath))
{
    controlsPath = Path.Combine(app.Environment.ContentRootPath, controlsPath);
}
var controls = ScanHarness.Services.Controls.ControlCatalog.Load(controlsPath);
ScanHarness.Services.Controls.ControlCatalog.Use(controls);
app.Logger.LogInformation("Loaded {Count} control(s) from {Path}.", controls.Count, controlsPath);

// Tools (tools.json) and agents (agents.json) are read the same way: an invalid file stops startup with
// every problem listed. Agents offered to the orchestrator become tools its brain can choose.
string ContentPath(string path) => Path.IsPathRooted(path) ? path : Path.Combine(app.Environment.ContentRootPath, path);
var toolOptions = app.Services.GetRequiredService<IOptions<ToolOptions>>().Value;
var agentOptions = app.Services.GetRequiredService<IOptions<ScanHarness.Services.Agents.AgentOptions>>().Value;
ToolCatalog.Use(ToolCatalog.Load(ContentPath(toolOptions.DefinitionsPath)));
ScanHarness.Services.Agents.AgentCatalog.Use(ScanHarness.Services.Agents.AgentCatalog.Load(ContentPath(agentOptions.DefinitionsPath)));
app.Logger.LogInformation("Loaded {Tools} tool(s) and {Agents} agent(s).", ToolCatalog.Tools.Count, ScanHarness.Services.Agents.AgentCatalog.Defined.Count);

// Config can override where each tool runs (Tools:Classifications, by function name); the Permissions tab can override that.
foreach (var unknown in toolOptions.Classifications.Keys.Where(name => ToolCatalog.FindByFunctionName(name) == null))
{
    app.Logger.LogWarning("Tools:Classifications names an unknown tool '{Tool}'; expected one of {Tools}.",
        unknown, string.Join(", ", ToolCatalog.Tools.Select(t => t.FunctionName)));
}
app.Services.GetRequiredService<ScanHarness.Services.Boundaries.ComponentPolicyService>().UseToolClassifications(toolOptions.Classifications);

// Create the database if needed and close out calls a previous run left unfinished.
var interrupted = app.Services.GetRequiredService<IActivityStore>().Initialize();
if (interrupted > 0)
{
    app.Logger.LogWarning("Marked {Count} unfinished tool call(s) from a previous run as interrupted.", interrupted);
}

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
