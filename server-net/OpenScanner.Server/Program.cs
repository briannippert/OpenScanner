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
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApiDocument(config => 
{
    config.DocumentName = "v1";
    config.Title = "OpenScanner API";
    config.Version = "v1";
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

app.MapGet("/api/channels", () => db.GetAllChannels())
    .WithSummary("Get all channels")
    .WithDescription("Retrieves the list of all configured radio channels.")
    .Produces<IEnumerable<Channel>>(StatusCodes.Status200OK);

app.MapPost("/api/channels", (Channel channel) => 
{
    var id = db.AddChannel(channel);
    channel.Id = id;
    radio.ReloadChannels();
    return Results.Created($"/api/channels/{id}", channel);
})
    .WithSummary("Add a new channel")
    .WithDescription("Adds a new radio channel to the configuration and reloads the scanner.")
    .Produces<Channel>(StatusCodes.Status201Created);

app.MapPut("/api/channels/{id}", (int id, Channel channel) => 
{
    channel.Id = id;
    db.UpdateChannel(channel);
    radio.ReloadChannels();
    return Results.Ok();
})
    .WithSummary("Update a channel")
    .WithDescription("Updates an existing channel's configuration.")
    .Produces(StatusCodes.Status200OK);

app.MapDelete("/api/channels/{id}", (int id) => 
{
    db.DeleteChannel(id);
    radio.ReloadChannels();
    return Results.Ok();
})
    .WithSummary("Delete a channel")
    .WithDescription("Removes a channel from the configuration.")
    .Produces(StatusCodes.Status200OK);

app.MapGet("/api/history", () => db.GetHistory(100))
    .WithSummary("Get call history")
    .WithDescription("Retrieves the last 100 radio transmission logs.")
    .Produces<IEnumerable<CallLog>>(StatusCodes.Status200OK);

app.MapDelete("/api/history/{id}", (string id) => 
{
    db.DeleteTransmission(id);
    return Results.Ok();
})
    .WithSummary("Delete a log entry")
    .WithDescription("Deletes a specific transmission log and its associated audio file.")
    .Produces(StatusCodes.Status200OK);

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
})
    .WithSummary("Control the scanner")
    .WithDescription("Sends control commands (start, stop, scan, hold, set_squelch) to the radio hardware.")
    .Produces(StatusCodes.Status200OK);

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

app.Run();