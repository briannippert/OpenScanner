using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using OpenScanner.Server;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add Services
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<GpsService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GpsService>());
builder.Services.AddSingleton<RtlDevice>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RtlDevice>());
builder.Services.AddSingleton<WebSocketBroadcaster>();
builder.Services.AddCors();

var app = builder.Build();

app.UseCors(c => c.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
app.UseWebSockets();

// Static Files (Frontend)
var clientDistPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "../../client/dist"));
if (Directory.Exists(clientDistPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(clientDistPath),
        RequestPath = ""
    });
}

// Static Files (Audio Recordings)
var recordingsPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "../../server/data/recordings"));
if (!Directory.Exists(recordingsPath)) Directory.CreateDirectory(recordingsPath);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(recordingsPath),
    RequestPath = "/audio"
});

var db = app.Services.GetRequiredService<Database>();
var radio = app.Services.GetRequiredService<RtlDevice>();
var wsBroadcaster = app.Services.GetRequiredService<WebSocketBroadcaster>();

// --- API Routes ---

app.MapGet("/api/channels", () => db.GetAllChannels());

app.MapPost("/api/channels", (Channel channel) => 
{
    var id = db.AddChannel(channel);
    radio.ReloadChannels();
    return Results.Created($"/api/channels/{id}", channel with { Id = id });
});

app.MapPut("/api/channels/{id}", (int id, Channel channel) => 
{
    db.UpdateChannel(channel with { Id = id });
    radio.ReloadChannels();
    return Results.Ok();
});

app.MapDelete("/api/channels/{id}", (int id) => 
{
    db.DeleteChannel(id);
    radio.ReloadChannels();
    return Results.Ok();
});

app.MapGet("/api/history", () => db.GetHistory(100));

app.MapDelete("/api/history/{id}", (string id) => 
{
    db.DeleteTransmission(id);
    return Results.Ok();
});

app.MapPost("/api/control", ([FromBody] JsonElement body) => 
{
    var action = body.GetProperty("action").GetString();
    
    switch (action)
    {
        case "start": radio.Start(); break;
        case "stop": radio.Stop(); break;
        case "scan": radio.ResumeScan(); break;
        case "hold": 
            if (body.TryGetProperty("frequency", out var f))
                radio.HoldFrequency(double.Parse(f.ToString()));
            break;
        case "set_squelch":
             if (body.TryGetProperty("value", out var v))
                radio.SetSquelch(double.Parse(v.ToString()));
            break;
    }
    return Results.Ok();
});

// --- WebSockets ---
app.Map("/ws", async (HttpContext context) =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        await wsBroadcaster.HandleConnection(ws);
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});

// SPA Fallback
app.MapFallback(async context =>
{
    if (File.Exists(Path.Combine(clientDistPath, "index.html")))
    {
        await context.Response.SendFileAsync(Path.Combine(clientDistPath, "index.html"));
    }
    else
    {
        context.Response.StatusCode = 404;
        await context.Response.WriteAsync("Client not found");
    }
});

app.Run("http://0.0.0.0:5000");