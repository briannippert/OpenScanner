using Microsoft.Extensions.FileProviders;
using OpenScanner.Server;
using OpenScanner.Server.Services;
using OpenScanner.Server.Interfaces;
using OpenScanner.Server.Devices;
using OpenScanner.Server.Models;

var builder = WebApplication.CreateBuilder(args);

// Add Services
builder.Services.AddControllers(); // Added for Controllers

// Setup Memory Logging
var memoryLoggerProvider = new MemoryLoggerProvider();
builder.Logging.AddProvider(memoryLoggerProvider);
builder.Services.AddSingleton<ILoggerProvider>(memoryLoggerProvider);

builder.Services.AddSingleton<IDatabase, Database>();
builder.Services.AddSingleton<GpsService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GpsService>());
builder.Services.AddSingleton<ToneDetector>();

var radioProvider = builder.Configuration["Radio:Provider"] ?? "RTL-SDR";
if (radioProvider == "Mock")
{
    builder.Services.AddSingleton<IRadioSource, MockRadioSource>();
    builder.Services.AddHostedService(sp => (MockRadioSource)sp.GetRequiredService<IRadioSource>());
}
else
{
    builder.Services.AddSingleton<IRadioSource, RtlDevice>();
    builder.Services.AddHostedService(sp => (RtlDevice)sp.GetRequiredService<IRadioSource>());
}

builder.Services.AddSingleton<ISupportService, SupportService>();
builder.Services.AddSingleton<WebSocketBroadcaster>();
builder.Services.AddCors();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApiDocument(config => 
{
    config.DocumentName = "v1";
    config.Title = "OpenScanner API";
    config.Version = "v1.0.0";
    config.Description = "API for controlling the OpenScanner radio system, managing channels, and accessing recording history.";
});

var app = builder.Build();

app.UseCors(c => c.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
app.UseWebSockets();
app.UseOpenApi();
app.UseSwaggerUi(config => 
{
    config.DocumentTitle = "OpenScanner API";
    config.Path = "/swagger";
    config.DocumentPath = "/swagger/{documentName}/swagger.json";
});

// Static Files (Frontend)
var cwd = Directory.GetCurrentDirectory();
var clientDistPath = Path.GetFullPath(Path.Combine(cwd, "../../client/dist"));
if (Directory.Exists(clientDistPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(clientDistPath),
        RequestPath = ""
    });
}

// Static Files (Audio Recordings)
var recordingsPath = Path.GetFullPath(Path.Combine(cwd, "../../data/recordings"));
if (!Directory.Exists(recordingsPath)) Directory.CreateDirectory(recordingsPath);

var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
provider.Mappings[".raw"] = "application/octet-stream";

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(recordingsPath),
    RequestPath = "/audio",
    ContentTypeProvider = provider
});

app.MapControllers(); // Map the controllers

// --- WebSockets ---
var wsBroadcaster = app.Services.GetRequiredService<WebSocketBroadcaster>();

app.Map("/ws/control", async (HttpContext context) =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        await wsBroadcaster.HandleControlConnection(ws);
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});

app.Map("/ws/audio", async (HttpContext context) =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        await wsBroadcaster.HandleAudioConnection(ws);
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});

// SPA Fallback
app.MapFallback(async context =>
{
    var indexPath = Path.Combine(cwd, "../../client/dist/index.html");
    if (File.Exists(indexPath))
    {
        await context.Response.SendFileAsync(indexPath);
    }
    else
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync("Client not found");
    }
});

app.Run();

public partial class Program { }