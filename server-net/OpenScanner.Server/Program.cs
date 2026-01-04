using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using OpenScanner.Server;
using OpenScanner.Server.Models;
using OpenScanner.Server.Services;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add Services
builder.Services.AddSingleton<IDatabase, Database>();
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

var db = app.Services.GetRequiredService<IDatabase>();
var radio = app.Services.GetRequiredService<RtlDevice>();
var wsBroadcaster = app.Services.GetRequiredService<WebSocketBroadcaster>();

// --- API Routes ---

app.MapGet("/api/channels", async () => await db.GetAllChannelsAsync())
    .WithSummary("Get all channels")
    .WithDescription("Retrieves the list of all configured radio channels.")
    .Produces<IEnumerable<Channel>>(StatusCodes.Status200OK);

app.MapPost("/api/channels", async (Channel channel) => 
{
    var id = await db.AddChannelAsync(channel);
    channel.Id = id;
    radio.ReloadChannels();
    return Results.Created($"/api/channels/{id}", channel);
})
    .WithSummary("Add a new channel")
    .WithDescription("Adds a new radio channel to the configuration and reloads the scanner.")
    .Produces<Channel>(StatusCodes.Status201Created);

app.MapPut("/api/channels/{id}", async (int id, Channel channel) => 
{
    channel.Id = id;
    await db.UpdateChannelAsync(channel);
    radio.ReloadChannels();
    return Results.Ok();
})
    .WithSummary("Update a channel")
    .WithDescription("Updates an existing channel's configuration.")
    .Produces(StatusCodes.Status200OK);

app.MapDelete("/api/channels/{id}", async (int id) => 
{
    await db.DeleteChannelAsync(id);
    radio.ReloadChannels();
    return Results.Ok();
})
    .WithSummary("Delete a channel")
    .WithDescription("Removes a channel from the configuration.")
    .Produces(StatusCodes.Status200OK);

app.MapGet("/api/history", async () => await db.GetHistoryAsync(100))
    .WithSummary("Get call history")
    .WithDescription("Retrieves the last 100 radio transmission logs.")
    .Produces<IEnumerable<CallLog>>(StatusCodes.Status200OK);

app.MapGet("/api/history/years", async () => await db.GetTransmissionYearsAsync())
    .WithSummary("Get available years")
    .Produces<IEnumerable<string>>(StatusCodes.Status200OK);

app.MapGet("/api/history/{year}/months", async (string year) => await db.GetTransmissionMonthsAsync(year))
    .WithSummary("Get available months for a year")
    .Produces<IEnumerable<string>>(StatusCodes.Status200OK);

app.MapGet("/api/history/{year}/{month}/days", async (string year, string month) => await db.GetTransmissionDaysAsync(year, month))
    .WithSummary("Get available days for a month")
    .Produces<IEnumerable<string>>(StatusCodes.Status200OK);

app.MapGet("/api/history/{year}/{month}/{day}/channels", async (string year, string month, string day) => await db.GetTransmissionChannelsAsync(year, month, day))
    .WithSummary("Get available channels for a day")
    .Produces<IEnumerable<dynamic>>(StatusCodes.Status200OK);

app.MapGet("/api/history/filter", async (string year, string month, string day, string alphaTag, double frequency) => 
    await db.GetTransmissionsAsync(year, month, day, alphaTag, frequency))
    .WithSummary("Get filtered transmissions")
    .Produces<IEnumerable<CallLog>>(StatusCodes.Status200OK);

app.MapGet("/api/history/search", async (string q) => await db.SearchTransmissionsAsync(q))
    .WithSummary("Search transmissions")
    .Produces<IEnumerable<CallLog>>(StatusCodes.Status200OK);

app.MapDelete("/api/history/{id}", async (string id) => 
{
    await db.DeleteTransmissionAsync(id);
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
            if (body.TryGetProperty("frequency", out var f) && f.ValueKind == JsonValueKind.Number)
                radio.HoldFrequency(f.GetDouble());
            else if (body.TryGetProperty("frequency", out var fs) && double.TryParse(fs.GetString(), out var fd))
                 radio.HoldFrequency(fd);
            break;
        case "set_squelch":
             if (body.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number)
                radio.SetSquelch(v.GetDouble());
             else if (body.TryGetProperty("value", out var vs) && double.TryParse(vs.GetString(), out var vd))
                radio.SetSquelch(vd);
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

app.Run();public partial class Program { }
