var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddSingleton<DemoToolAPI_1.Services.DummyResponseService>();

// The scanner's live mode (off unless Scanner:LiveFetch is set): real requests, through the egress proxy.
builder.Services.Configure<DemoToolAPI_1.Services.ScannerOptions>(builder.Configuration.GetSection(DemoToolAPI_1.Services.ScannerOptions.SectionName));
builder.Services.AddSingleton(sp => new DemoToolAPI_1.Services.LiveScanner(
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DemoToolAPI_1.Services.ScannerOptions>>()));

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
