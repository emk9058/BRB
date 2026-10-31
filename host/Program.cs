var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5105");
var webRoot = PhoneDirectory(builder.Environment.ContentRootPath);
var webFiles = webRoot is null ? null : new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod());
});

var app = builder.Build();

app.UseCors();
if (webFiles is not null)
{
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webFiles });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = webFiles });
}
app.MapGet("/api/state", () => LeagueClient.Current());
app.MapPost("/api/queue", async () =>
    await LeagueClient.StartQueue() ? Results.NoContent() : Results.StatusCode(StatusCodes.Status409Conflict));
app.MapDelete("/api/queue", async () =>
    await LeagueClient.CancelQueue() ? Results.NoContent() : Results.StatusCode(StatusCodes.Status409Conflict));
app.MapPost("/api/ready-check", async () =>
    await LeagueClient.AcceptReadyCheck() ? Results.NoContent() : Results.StatusCode(StatusCodes.Status409Conflict));
app.MapPost("/api/ready-check/decline", async () =>
    await LeagueClient.DeclineReadyCheck() ? Results.NoContent() : Results.StatusCode(StatusCodes.Status409Conflict));
app.MapPost("/api/champ-select", async (ChampPick pick) =>
    await LeagueClient.ChooseChampion(pick.ChampionId, pick.Lock) ? Results.NoContent() : Results.StatusCode(StatusCodes.Status409Conflict));
app.MapGet("/api/loadout", async () =>
{
    var loadout = await LeagueClient.ReadLoadout();
    return loadout is null ? Results.StatusCode(StatusCodes.Status409Conflict) : Results.Ok(loadout);
});
app.MapPost("/api/spells", async (SpellChange change) =>
    await LeagueClient.SetSpell(change.Slot, change.SpellId) ? Results.NoContent() : Results.StatusCode(StatusCodes.Status409Conflict));
app.MapPut("/api/runes/current", async (RunePick pick) =>
    await LeagueClient.SetCurrentRune(pick.Id) ? Results.NoContent() : Results.StatusCode(StatusCodes.Status409Conflict));
app.MapPut("/api/runes", async (RuneEdit edit) =>
    await LeagueClient.SaveRunes(edit.Id, edit.PrimaryStyleId, edit.SubStyleId, edit.Perks) ? Results.NoContent() : Results.StatusCode(StatusCodes.Status409Conflict));
app.MapPost("/api/runes", async () =>
    await LeagueClient.CreateRunePage() ? Results.NoContent() : Results.StatusCode(StatusCodes.Status409Conflict));
app.MapDelete("/api/runes/{id:int}", async (int id) =>
    await LeagueClient.DeleteRunePage(id) ? Results.NoContent() : Results.StatusCode(StatusCodes.Status409Conflict));
app.MapGet("/api/modes", async () =>
{
    var modes = await LeagueClient.Modes();
    return modes is null ? Results.StatusCode(StatusCodes.Status409Conflict) : Results.Ok(modes);
});
app.MapGet("/api/friends", async () =>
{
    var friends = await LeagueClient.Friends();
    return friends is null ? Results.StatusCode(StatusCodes.Status409Conflict) : Results.Ok(friends);
});
app.MapPost("/api/lobby", async (QueuePick pick) =>
    await LeagueClient.CreateParty(pick.QueueId) ? Results.NoContent() : Results.StatusCode(StatusCodes.Status409Conflict));
app.MapPut("/api/lobby", async (QueuePick pick) =>
    await LeagueClient.ChangeParty(pick.QueueId) ? Results.NoContent() : Results.StatusCode(StatusCodes.Status409Conflict));
app.MapDelete("/api/lobby", async () =>
    await LeagueClient.LeaveParty() ? Results.NoContent() : Results.StatusCode(StatusCodes.Status409Conflict));
app.MapPost("/api/lobby/invite", async (InvitePick pick) =>
    await LeagueClient.Invite(pick.SummonerId, pick.Name) ? Results.NoContent() : Results.StatusCode(StatusCodes.Status409Conflict));
app.MapPost("/api/lobby/kick", async (KickPick pick) =>
    await LeagueClient.Kick(pick.SummonerId) ? Results.NoContent() : Results.StatusCode(StatusCodes.Status409Conflict));
app.MapPut("/api/lobby/roles", async (RoleChoice roles) =>
    await LeagueClient.SetRoles(roles.First, roles.Second) ? Results.NoContent() : Results.StatusCode(StatusCodes.Status409Conflict));
app.MapGet("/api/swift/champions", async () =>
{
    var champions = await LeagueClient.SwiftChampions();
    return champions is null ? Results.StatusCode(StatusCodes.Status409Conflict) : Results.Ok(champions);
});
app.MapPut("/api/lobby/swift", async (SwiftEdit edit) =>
    await LeagueClient.SetSwiftSlot(edit.Index, edit.ChampionId, edit.Position, edit.SpellSlot, edit.SpellId, edit.PrimaryStyleId, edit.SubStyleId, edit.Perks)
        ? Results.NoContent()
        : Results.StatusCode(StatusCodes.Status409Conflict));

var deskPage = Path.Combine(app.Environment.WebRootPath, "desk.html");
app.MapGet("/host", () => Results.File(deskPage, "text/html"));
app.MapGet("/host/qr", (HttpRequest request) =>
{
    var urls = HostDesk.PhoneUrls(request.Host.Port ?? 5105);
    var png = HostDesk.Qr(urls.FirstOrDefault() ?? "");
    return png is null ? Results.NotFound() : Results.File(png, "image/png");
});
app.MapGet("/api/host", async (HttpRequest request) =>
{
    var urls = HostDesk.PhoneUrls(request.Host.Port ?? 5105);
    var screen = await LeagueClient.Current();
    return Results.Ok(new
    {
        url = urls.FirstOrDefault(),
        urls,
        pageReady = webFiles is not null,
        phase = screen.Phase,
        league = screen.Phase == "closed" ? "Closed" : screen.Title,
        detail = screen.Detail
    });
});

app.Lifetime.ApplicationStarted.Register(() => TrayHost.Open(app, webFiles is not null));

if (webFiles is not null)
{
    app.MapFallback(async context =>
    {
        context.Response.ContentType = "text/html";
        await context.Response.SendFileAsync(Path.Combine(webRoot!, "index.html"));
    });
}

app.Run();

static string? PhoneDirectory(string contentRoot)
{
    foreach (var candidate in new[]
    {
        Path.Combine(contentRoot, "phone"),
        Path.GetFullPath(Path.Combine(contentRoot, "..", "web", "dist"))
    })
    {
        if (File.Exists(Path.Combine(candidate, "index.html")))
            return candidate;
    }

    return null;
}

record ChampPick(int ChampionId, bool Lock = false);
record SpellChange(int Slot, int SpellId);
record RunePick(int Id);
record RuneEdit(int Id, int PrimaryStyleId, int SubStyleId, int[] Perks);
record QueuePick(int QueueId);
record InvitePick(long? SummonerId, string? Name);
record KickPick(long SummonerId);
record RoleChoice(string? First, string? Second);
record SwiftEdit(int Index, int? ChampionId, string? Position, int? SpellSlot, int? SpellId, int? PrimaryStyleId, int? SubStyleId, int[]? Perks);
