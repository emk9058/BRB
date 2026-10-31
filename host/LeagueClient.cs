using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class LeagueClient
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    public static async Task<Screen> Current()
    {
        var connection = LeagueLockfile.Connect();
        if (connection is null)
            return new Screen("closed", "League closed", null, null);

        try
        {
            using var phaseDoc = await Get(connection, "/lol-gameflow/v1/gameflow-phase");
            var phase = phaseDoc?.RootElement.GetString() ?? "None";
            var detail = await QueueName(connection, phase);

            var (id, title, action) = phase switch
            {
                "None" => ("home", "Home", null),
                "Lobby" => ("party", "Party", "start"),
                "Matchmaking" => ("queue", "In queue", "cancel"),
                "ReadyCheck" => ("ready", "Ready check", "accept"),
                "ChampSelect" => ("champ", "Champ select", null),
                "GameStart" or "InProgress" => ("game", "In game", null),
                "Reconnect" => ("reconnect", "Reconnecting", null),
                _ => ("other", phase, null)
            };

            ChampSelectView? champSelect = null;
            int? clockMs = null;
            var clockDown = true;
            string? clockPhase = null;
            int? clockTotalMs = null;
            string? readyResponse = null;
            if (id == "champ")
            {
                (champSelect, clockMs, clockPhase) = await ReadChampSelect(connection);
            }
            else if (id is "ready" or "queue")
            {
                (clockMs, clockDown, clockPhase, clockTotalMs, readyResponse) = await PhaseClock(connection, id);
            }

            LobbyView? lobby = null;
            if (id == "party")
                lobby = await ReadLobby(connection);

            return new Screen(id, title, detail, action, champSelect, clockMs, clockDown, clockPhase, lobby, clockTotalMs, readyResponse);
        }
        catch
        {
            return new Screen("open", "League open", null, null);
        }
    }

    public static Task<bool> StartQueue() =>
        Send(HttpMethod.Post, "/lol-lobby/v2/lobby/matchmaking/search");

    public static Task<bool> CancelQueue() =>
        Send(HttpMethod.Delete, "/lol-lobby/v2/lobby/matchmaking/search");

    public static Task<bool> AcceptReadyCheck() =>
        Send(HttpMethod.Post, "/lol-matchmaking/v1/ready-check/accept");

    public static Task<bool> DeclineReadyCheck() =>
        Send(HttpMethod.Post, "/lol-matchmaking/v1/ready-check/decline");

    public static async Task<bool> ChooseChampion(int championId, bool lockIn)
    {
        var connection = LeagueLockfile.Connect();
        if (connection is null)
            return false;

        using var session = await Get(connection, "/lol-champ-select/v1/session");
        if (session is null || !session.RootElement.TryGetProperty("localPlayerCellId", out var localCellEl))
            return false;

        var localCell = localCellEl.GetInt32();
        var turn = FindTurn(session.RootElement, localCell);
        var actionId = turn.ActionId;
        if (!lockIn && actionId is null)
            actionId = FindIntent(session.RootElement, localCell).ActionId;
        if (actionId is null)
            return false;

        var id = championId > 0 ? championId : turn.Hovered ?? FindIntent(session.RootElement, localCell).ChampionId ?? 0;
        if (id <= 0)
            return false;

        var body = lockIn
            ? JsonSerializer.Serialize(new { championId = id, completed = true })
            : JsonSerializer.Serialize(new { championId = id });
        using var request = Request(connection, HttpMethod.Patch, $"/lol-champ-select/v1/session/actions/{actionId}", body);
        using var response = await Http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    private static async Task<string?> QueueName(LeagueConnection connection, string phase)
    {
        if (phase is "None")
            return null;

        using var lobby = await Get(connection, "/lol-lobby/v2/lobby");
        if (lobby is null
            || !lobby.RootElement.TryGetProperty("gameConfig", out var config)
            || !config.TryGetProperty("queueId", out var queueIdEl)
            || !queueIdEl.TryGetInt32(out var queueId)
            || queueId <= 0)
            return null;

        using var queue = await Get(connection, $"/lol-game-queues/v1/queues/{queueId}");
        if (queue is null || !queue.RootElement.TryGetProperty("name", out var name))
            return null;

        return name.GetString();
    }

    private static Task<JsonDocument?> Get(LeagueConnection connection, string path) =>
        SendForJson(connection, HttpMethod.Get, path);

    private static async Task<bool> Send(HttpMethod method, string path, string? body = null, string contentType = "application/json")
    {
        var connection = LeagueLockfile.Connect();
        if (connection is null)
            return false;

        using var request = Request(connection, method, path, body, contentType);
        using var response = await Http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    private static async Task<JsonDocument?> SendJson(HttpMethod method, string path, string? body = null, string contentType = "application/json")
    {
        var connection = LeagueLockfile.Connect();
        if (connection is null)
            return null;

        using var request = Request(connection, method, path, body, contentType);
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
    }

    private static async Task<JsonDocument?> SendForJson(LeagueConnection connection, HttpMethod method, string path)
    {
        using var request = Request(connection, method, path);
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static HttpRequestMessage Request(LeagueConnection connection, HttpMethod method, string path, string? body = null, string contentType = "application/json")
    {
        var request = new HttpRequestMessage(method, $"https://127.0.0.1:{connection.Port}{path}");
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($"riot:{connection.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        return request;
    }

    private static async Task<(ChampSelectView? View, int? TimeLeftMs, string? PhaseName)> ReadChampSelect(LeagueConnection connection)
    {
        using var session = await Get(connection, "/lol-champ-select/v1/session");
        if (session is null || !session.RootElement.TryGetProperty("localPlayerCellId", out var localCellEl))
            return (null, null, null);

        var localCell = localCellEl.GetInt32();
        var champions = await ChampionCatalog(connection);
        var spells = await SpellIcons(connection);
        var turn = FindTurn(session.RootElement, localCell);
        var intent = FindIntent(session.RootElement, localCell);
        var taken = TakenChampions(session.RootElement);
        var listKind = turn.Turn ?? (intent.ActionId is null ? null : "pick");
        var options = await ChampionOptions(connection, listKind, champions, taken);
        var acting = ActingPlayers(session.RootElement);
        int? timeLeft = session.RootElement.TryGetProperty("timer", out var timer) ? ReadPhaseMs(timer) : null;

        var (myBans, theirBans) = FinishedBans(session.RootElement);
        var view = new ChampSelectView(
            turn.Turn,
            turn.ActionId ?? intent.ActionId,
            turn.Hovered ?? intent.ChampionId,
            await ReadTeam(connection, session.RootElement, "myTeam", champions, spells, acting, localCell, includeSpells: true),
            await ReadTeam(connection, session.RootElement, "theirTeam", champions, spells, acting, localCell, includeSpells: false),
            options,
            myBans,
            theirBans);
        return (view, timeLeft, PhaseName(session.RootElement));
    }

    private static string PhaseName(JsonElement session)
    {
        var phase = session.TryGetProperty("timer", out var timer) && timer.TryGetProperty("phase", out var phaseEl)
            ? phaseEl.GetString()
            : null;

        if (phase == "PLANNING")
            return "Pick your champion";
        if (phase == "FINALIZATION")
            return "Choose loadout";
        if (phase == "GAME_STARTING")
            return "Game starting";

        var kinds = ActingPlayers(session).Values.ToHashSet();
        if (kinds.Contains("ban") && !kinds.Contains("pick"))
            return "Ban phase";
        if (kinds.Contains("pick"))
            return "Lock in";
        if (phase == "BAN_PICK")
            return "Champ select";

        return "Champ select";
    }

    private static async Task<(int? Ms, bool Down, string? Label, int? TotalMs, string? ReadyResponse)> PhaseClock(LeagueConnection connection, string phase)
    {
        if (phase == "ready")
        {
            using var ready = await Get(connection, "/lol-matchmaking/v1/ready-check");
            if (ready is not null && ready.RootElement.TryGetProperty("timer", out var timer) && TryReadNumber(timer, out var elapsed))
            {
                const double totalSeconds = 12;
                var remaining = (int)Math.Clamp((totalSeconds - elapsed) * 1000, 0, totalSeconds * 1000);
                var response = Text(ready.RootElement, "playerResponse");
                if (response is "None" or "")
                    response = null;
                return (remaining, true, "Accept", (int)(totalSeconds * 1000), response);
            }
        }

        if (phase == "queue")
        {
            using var search = await Get(connection, "/lol-matchmaking/v1/search");
            if (search is not null && search.RootElement.TryGetProperty("timeInQueue", out var elapsed) && TryReadNumber(elapsed, out var seconds))
                return ((int)Math.Max(0, seconds * 1000), false, "In queue", null, null);
        }

        return (null, true, null, null, null);
    }

    private static int? ReadPhaseMs(JsonElement timer)
    {
        if (timer.TryGetProperty("isInfinite", out var infinite) && infinite.ValueKind == JsonValueKind.True)
            return null;
        if (!timer.TryGetProperty("adjustedTimeLeftInPhase", out var leftEl) || !TryReadLong(leftEl, out var left))
            return null;

        // League stamps the remaining time once, at internalNowInEpochMs, and leaves that number in place.
        var corrected = left;
        if (timer.TryGetProperty("internalNowInEpochMs", out var stampEl) && TryReadLong(stampEl, out var stamp))
        {
            var elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - stamp;
            if (elapsed is > -5_000 and < 300_000)
                corrected = left - elapsed;
        }

        if (timer.TryGetProperty("totalTimeInPhase", out var totalEl) && TryReadLong(totalEl, out var total) && total > 0 && corrected > total + 3_000)
            corrected = left;

        return (int)Math.Clamp(corrected, 0, int.MaxValue);
    }

    private static bool TryReadLong(JsonElement element, out long value)
    {
        if (element.TryGetInt64(out value))
            return true;
        if (TryReadNumber(element, out var number))
        {
            value = (long)number;
            return true;
        }

        value = 0;
        return false;
    }

    private static (IReadOnlyList<int?> Mine, IReadOnlyList<int?> Theirs) FinishedBans(JsonElement session)
    {
        var mine = new List<int?>();
        var theirs = new List<int?>();
        if (!session.TryGetProperty("actions", out var groups))
            return (mine, theirs);

        var myCells = TeamCells(session, "myTeam");
        var theirCells = TeamCells(session, "theirTeam");
        var sawBan = false;
        foreach (var group in groups.EnumerateArray())
        {
            foreach (var action in group.EnumerateArray())
            {
                if (!action.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "ban")
                    continue;

                sawBan = true;
                if (!action.TryGetProperty("completed", out var completedEl) || !completedEl.GetBoolean())
                    return ([], []);

                int? champion = ActionChampion(action) > 0 ? ActionChampion(action) : null;
                var cell = ActorCell(action);
                if (myCells.Contains(cell))
                    mine.Add(champion);
                else if (theirCells.Contains(cell))
                    theirs.Add(champion);
            }
        }

        return sawBan ? (mine, theirs) : ([], []);
    }

    private static HashSet<int> TeamCells(JsonElement session, string team)
    {
        var cells = new HashSet<int>();
        if (!session.TryGetProperty(team, out var players))
            return cells;

        foreach (var player in players.EnumerateArray())
        {
            if (player.TryGetProperty("cellId", out var cellEl) && cellEl.TryGetInt32(out var cell))
                cells.Add(cell);
        }

        return cells;
    }

    private static bool TryReadNumber(JsonElement element, out double value)
    {
        if (element.TryGetDouble(out value))
            return true;
        if (element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
            return true;
        value = 0;
        return false;
    }

    private static Dictionary<int, string> ActingPlayers(JsonElement session)
    {
        var acting = new Dictionary<int, string>();
        if (!session.TryGetProperty("actions", out var groups))
            return acting;

        foreach (var group in groups.EnumerateArray())
        {
            var pending = false;
            foreach (var action in group.EnumerateArray())
            {
                if (!IsPendingChoice(action) || !IsInProgress(action))
                    continue;

                pending = true;
                var cell = ActorCell(action);
                var type = action.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                if (cell >= 0 && type is "ban" or "pick")
                    acting[cell] = type;
            }

            if (pending)
                return acting;
        }

        return acting;
    }

    private static (string? Turn, int? ActionId, int? Hovered) FindTurn(JsonElement session, int localCell)
    {
        if (!session.TryGetProperty("actions", out var groups))
            return (null, null, null);

        foreach (var group in groups.EnumerateArray())
        {
            JsonElement? mine = null;
            var pending = false;
            foreach (var action in group.EnumerateArray())
            {
                if (!action.TryGetProperty("type", out var typeEl) || !action.TryGetProperty("completed", out var completedEl))
                    continue;
                var type = typeEl.GetString();
                if (type is not ("ban" or "pick") || completedEl.GetBoolean())
                    continue;

                pending = true;
                if (IsInProgress(action) && action.TryGetProperty("actorCellId", out var cellEl) && cellEl.GetInt32() == localCell)
                    mine = action;
            }

            if (!pending)
                continue;

            var started = false;
            foreach (var action in group.EnumerateArray())
            {
                if (IsPendingChoice(action) && IsInProgress(action))
                    started = true;
            }

            if (!started)
                continue;

            if (mine is null)
                return (null, null, null);

            var chosen = mine.Value;
            var actionId = chosen.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : (int?)null;
            int? hovered = chosen.TryGetProperty("championId", out var champEl) && champEl.TryGetInt32(out var champId) && champId > 0
                ? champId
                : null;
            return (chosen.GetProperty("type").GetString(), actionId, hovered);
        }

        return (null, null, null);
    }

    private static (int? ActionId, int? ChampionId) FindIntent(JsonElement session, int localCell)
    {
        if (!session.TryGetProperty("actions", out var groups))
            return (null, null);

        foreach (var group in groups.EnumerateArray())
        {
            foreach (var action in group.EnumerateArray())
            {
                if (!action.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "pick")
                    continue;
                if (action.TryGetProperty("completed", out var completedEl) && completedEl.GetBoolean())
                    continue;
                if (ActorCell(action) != localCell)
                    continue;

                var actionId = action.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id) ? id : (int?)null;
                var champion = ActionChampion(action);
                return (actionId, champion > 0 ? champion : null);
            }
        }

        return (null, null);
    }

    private static int DeclaredPick(JsonElement session, int cell) =>
        FindIntent(session, cell).ChampionId ?? 0;

    private static bool IsInProgress(JsonElement action) =>
        !action.TryGetProperty("isInProgress", out var progress) || progress.ValueKind == JsonValueKind.True;

    private static HashSet<int> TakenChampions(JsonElement session)
    {
        var taken = new HashSet<int>();
        if (!session.TryGetProperty("actions", out var groups))
            return taken;

        foreach (var group in groups.EnumerateArray())
        {
            foreach (var action in group.EnumerateArray())
            {
                if (!action.TryGetProperty("completed", out var completedEl) || !completedEl.GetBoolean())
                    continue;
                if (!action.TryGetProperty("championId", out var champEl) || !champEl.TryGetInt32(out var champId) || champId <= 0)
                    continue;
                var type = action.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                if (type is "ban" or "pick")
                    taken.Add(champId);
            }
        }

        return taken;
    }

    private static async Task<IReadOnlyList<ChampOption>> ChampionOptions(
        LeagueConnection connection,
        string? turn,
        Dictionary<int, ChampionInfo> champions,
        HashSet<int> taken)
    {
        if (turn is null)
            return [];

        var path = turn == "ban"
            ? "/lol-champ-select/v1/bannable-champion-ids"
            : "/lol-champ-select/v1/pickable-champion-ids";
        var source = new HashSet<int>();
        using (var ids = await Get(connection, path))
        {
            if (ids is not null && ids.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ids.RootElement.EnumerateArray())
                {
                    if (TryReadInt(item, out var id))
                        source.Add(id);
                }
            }
        }

        var pool = source.Count > 0
            ? source.Where(champions.ContainsKey).Select(id => champions[id])
            : champions.Values;

        var options = DistinctChampions(pool, taken);
        if (options.Count > 0)
            return options;

        return DistinctChampions(champions.Values, taken);
    }

    private static List<ChampOption> DistinctChampions(IEnumerable<ChampionInfo> pool, HashSet<int> taken)
    {
        return pool
            .Where(champ => champ.Id > 0 && !taken.Contains(champ.Id))
            .GroupBy(champ => champ.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(champ => champ.Classic).ThenBy(champ => champ.Id).First())
            .OrderBy(champ => champ.Name, StringComparer.OrdinalIgnoreCase)
            .Select(champ => new ChampOption(champ.Id, champ.Name))
            .ToList();
    }

    private static bool TryReadInt(JsonElement element, out int id)
    {
        if (element.TryGetInt32(out id))
            return true;
        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out id))
            return true;
        id = 0;
        return false;
    }

    private static async Task<List<ChampSlot>> ReadTeam(
        LeagueConnection connection,
        JsonElement session,
        string team,
        Dictionary<int, ChampionInfo> champions,
        Dictionary<int, string> spells,
        Dictionary<int, string> acting,
        int localCell,
        bool includeSpells)
    {
        var slots = new List<ChampSlot>();
        if (!session.TryGetProperty(team, out var players))
            return slots;

        foreach (var player in players.EnumerateArray())
        {
            var cell = player.TryGetProperty("cellId", out var cellEl) ? cellEl.GetInt32() : -1;
            var locked = player.TryGetProperty("championId", out var champEl) && champEl.TryGetInt32(out var parsed) && parsed > 0
                ? parsed
                : CompletedPick(session, cell);
            var turn = acting.TryGetValue(cell, out var kind) ? kind : null;
            var hover = turn == "ban" ? 0 : ActiveHover(session, cell);
            var intent = player.TryGetProperty("championPickIntent", out var intentEl) && intentEl.TryGetInt32(out var intentId) ? intentId : 0;
            var declared = DeclaredPick(session, cell);
            var champId = hover > 0 ? hover : locked > 0 ? locked : declared > 0 ? declared : intent;
            var banHover = team == "myTeam" ? CurrentBanHover(session, cell) : 0;
            var you = cell == localCell;
            var name = await ChampSelectName(connection, player, cell, you);
            var position = player.TryGetProperty("assignedPosition", out var positionEl) ? positionEl.GetString() : null;
            var champion = champId > 0 && champions.TryGetValue(champId, out var info) ? info.Name : null;
            var spell1 = includeSpells ? SpellIcon(player, "spell1Id", spells) : null;
            var spell2 = includeSpells ? SpellIcon(player, "spell2Id", spells) : null;
            slots.Add(new ChampSlot(
                name,
                PositionName(position),
                champion,
                champId > 0 ? champId : null,
                spell1,
                spell2,
                champId > 0 && champId != locked,
                turn,
                banHover > 0 ? banHover : null,
                you));
        }

        return slots;
    }

    private static int ActiveHover(JsonElement session, int cell)
    {
        if (!session.TryGetProperty("actions", out var groups))
            return 0;

        foreach (var group in groups.EnumerateArray())
        {
            var pending = false;
            var hover = 0;
            foreach (var action in group.EnumerateArray())
            {
                if (!IsPendingChoice(action) || !IsInProgress(action))
                    continue;
                pending = true;
                if (ActorCell(action) == cell && ActionChampion(action) is > 0 and var champId)
                    hover = champId;
            }

            if (pending)
                return hover;
        }

        return 0;
    }

    private static int CurrentBanHover(JsonElement session, int cell)
    {
        if (!session.TryGetProperty("actions", out var groups))
            return 0;

        foreach (var group in groups.EnumerateArray())
        {
            var started = false;
            var hovered = 0;
            foreach (var action in group.EnumerateArray())
            {
                if (!action.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "ban")
                    continue;
                if (action.TryGetProperty("completed", out var completedEl) && completedEl.GetBoolean())
                    continue;
                if (!IsInProgress(action))
                    continue;

                started = true;
                if (ActorCell(action) == cell && ActionChampion(action) is > 0 and var champId)
                    hovered = champId;
            }

            if (started)
                return hovered;
        }

        return 0;
    }

    private static int CompletedPick(JsonElement session, int cell)
    {
        if (!session.TryGetProperty("actions", out var groups))
            return 0;

        var locked = 0;
        foreach (var group in groups.EnumerateArray())
        {
            foreach (var action in group.EnumerateArray())
            {
                if (!action.TryGetProperty("completed", out var completedEl) || !completedEl.GetBoolean())
                    continue;
                if (action.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "pick"
                    && ActorCell(action) == cell
                    && ActionChampion(action) is > 0 and var champId)
                    locked = champId;
            }
        }

        return locked;
    }

    private static bool IsPendingChoice(JsonElement action)
    {
        if (!action.TryGetProperty("type", out var typeEl) || !action.TryGetProperty("completed", out var completedEl))
            return false;
        var type = typeEl.GetString();
        return (type is "ban" or "pick") && !completedEl.GetBoolean();
    }

    private static int ActorCell(JsonElement action) =>
        action.TryGetProperty("actorCellId", out var cellEl) && cellEl.TryGetInt32(out var cell) ? cell : -1;

    private static int ActionChampion(JsonElement action) =>
        action.TryGetProperty("championId", out var champEl) && champEl.TryGetInt32(out var champId) ? champId : 0;

    private static string? SpellIcon(JsonElement player, string property, Dictionary<int, string> spells)
    {
        if (!player.TryGetProperty(property, out var spellEl) || !spellEl.TryGetInt32(out var spellId) || spellId <= 0)
            return null;
        return spells.TryGetValue(spellId, out var icon) ? icon : null;
    }

    private static readonly string[] CampNames =
    [
        "Gromp", "Krug", "Murk Wolf", "Raptor", "Scuttle Crab", "Minion", "Void Grub", "Sentinel", "Brambleback", "Wraith",
        "Poro", "Spiderling", "Meep", "Stellacorn", "Sprite", "Silverwing", "Drake-Hound", "Basilisk", "Tuskbeast", "Wyvern"
    ];

    private static string CampName(int cell)
    {
        var index = cell < 0 ? 0 : cell % CampNames.Length;
        return CampNames[index];
    }

    private static async Task<string> ChampSelectName(LeagueConnection connection, JsonElement player, int cell, bool you)
    {
        var visibility = Text(player, "nameVisibilityType");
        if (!you && string.Equals(visibility, "HIDDEN", StringComparison.OrdinalIgnoreCase))
            return CampName(cell);

        var full = await DisplayName(connection, player);
        var hash = full.IndexOf('#');
        var game = hash > 0 ? full[..hash] : full;
        return game.Length > 0 ? game : CampName(cell);
    }

    private static readonly Dictionary<long, string> summonerNames = new();

    private static async Task<string> DisplayName(LeagueConnection connection, JsonElement player)
    {
        var named = PlayerName(player, "gameName", "tagLine", "summonerName");
        if (named != "Player")
            return named;
        TryLong(player, "summonerId", out var id);
        if (id > 0 && summonerNames.TryGetValue(id, out var cached))
            return cached;

        var puuid = Text(player, "puuid");
        using var summoner = id > 0
            ? await Get(connection, $"/lol-summoner/v1/summoners/{id}")
            : puuid is null ? null : await Get(connection, $"/lol-summoner/v2/summoners/puuid/{Uri.EscapeDataString(puuid)}");
        var resolved = summoner is null ? "" : PlayerName(summoner.RootElement, "gameName", "tagLine", "displayName");
        if (resolved == "Player")
            resolved = "";
        if (resolved.Length > 0 && summoner is not null && TryLong(summoner.RootElement, "summonerId", out var resolvedId) && resolvedId > 0)
            summonerNames[resolvedId] = resolved;
        return resolved;
    }

    private static string? PositionName(string? position) => position?.ToLowerInvariant() switch
    {
        "top" => "Top",
        "jungle" => "Jungle",
        "middle" => "Mid",
        "bottom" => "Bot",
        "utility" => "Support",
        "fill" => "Fill",
        null or "" => null,
        _ => position
    };

    private static Dictionary<int, ChampionInfo>? championCatalog;
    private static Dictionary<int, string>? spellIcons;

    private static async Task<Dictionary<int, ChampionInfo>> ChampionCatalog(LeagueConnection connection)
    {
        if (championCatalog is { Count: > 0 })
            return championCatalog;

        using var doc = await Get(connection, "/lol-game-data/assets/v1/champion-summary.json");
        var map = new Dictionary<int, ChampionInfo>();
        if (doc is not null)
        {
            foreach (var champ in doc.RootElement.EnumerateArray())
            {
                if (!champ.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id) || id <= 0)
                    continue;
                if (champ.TryGetProperty("name", out var nameEl) && nameEl.GetString() is { Length: > 0 } name)
                {
                    var alias = champ.TryGetProperty("alias", out var aliasEl) ? aliasEl.GetString() : null;
                    map[id] = new ChampionInfo(id, name, alias is not null && alias.Contains('_'));
                }
            }
        }

        if (map.Count > 0)
            championCatalog = map;
        return map;
    }

    private static async Task<Dictionary<int, string>> SpellIcons(LeagueConnection connection)
    {
        if (spellIcons is { Count: > 0 })
            return spellIcons;

        using var doc = await Get(connection, "/lol-game-data/assets/v1/summoner-spells.json");
        var map = new Dictionary<int, string>();
        if (doc is not null)
        {
            foreach (var spell in doc.RootElement.EnumerateArray())
            {
                if (!spell.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id) || id <= 0)
                    continue;
                if (!spell.TryGetProperty("iconPath", out var pathEl) || pathEl.GetString() is not { Length: > 0 } path)
                    continue;
                const string prefix = "/lol-game-data/assets/";
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                map[id] = "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/"
                    + path[prefix.Length..].ToLowerInvariant();
            }
        }

        if (map.Count > 0)
            spellIcons = map;
        return map;
    }

    public static async Task<Loadout?> ReadLoadout()
    {
        var connection = LeagueLockfile.Connect();
        if (connection is null)
            return null;

        var styles = await RuneStyles(connection);
        var mode = await GameMode(connection);
        var (spell1, spell2) = await LocalSpells(connection);
        var pages = await RunePages(connection);
        var active = pages.FirstOrDefault(page => page.Active)?.Id;
        return new Loadout(spell1, spell2, await SpellChoices(connection, mode), pages, active, styles);
    }

    public static async Task<bool> SetSpell(int slot, int spellId)
    {
        if (spellId <= 0 || slot is not (1 or 2))
            return false;

        var connection = LeagueLockfile.Connect();
        if (connection is null)
            return false;

        var (first, second) = await LocalSpells(connection);
        if (first <= 0 || second <= 0)
            return false;

        if (slot == 1 && spellId == second)
            (first, second) = (spellId, first);
        else if (slot == 2 && spellId == first)
            (first, second) = (second, spellId);
        else if (slot == 1)
            first = spellId;
        else
            second = spellId;

        if (first == second)
            return false;

        var body = JsonSerializer.Serialize(new { spell1Id = first, spell2Id = second });
        return await Send(HttpMethod.Patch, "/lol-champ-select/v1/session/my-selection", body);
    }

    public static Task<bool> SetCurrentRune(int id) =>
        Send(HttpMethod.Put, "/lol-perks/v1/currentpage", id.ToString(), "application/json");

    public static async Task<bool> SaveRunes(int id, int primaryStyleId, int subStyleId, int[] perks)
    {
        if (perks.Length < 9)
            return false;

        var connection = LeagueLockfile.Connect();
        if (connection is null)
            return false;

        using var doc = await Get(connection, $"/lol-perks/v1/pages/{id}");
        if (doc is null)
            return false;

        var node = JsonNode.Parse(doc.RootElement.GetRawText())!.AsObject();
        if (node["isEditable"] is JsonValue editable && editable.TryGetValue<bool>(out var canEdit) && !canEdit)
            return false;

        node["primaryStyleId"] = primaryStyleId;
        node["subStyleId"] = subStyleId;
        var selected = new JsonArray();
        foreach (var perk in perks.Take(9))
            selected.Add(perk);
        node["selectedPerkIds"] = selected;
        return await Send(HttpMethod.Put, $"/lol-perks/v1/pages/{id}", node.ToJsonString());
    }

    public static async Task<bool> CreateRunePage()
    {
        var connection = LeagueLockfile.Connect();
        if (connection is null)
            return false;

        var styles = await RuneStyles(connection);
        if (styles.Count < 2)
            return false;

        var primary = styles[0];
        var secondary = styles.First(style => style.Id != primary.Id);
        var perks = new int[9];
        for (var i = 0; i < primary.Stats.Count && i < 3; i++)
        {
            if (primary.Stats[i].Runes.Count > 0)
                perks[6 + i] = primary.Stats[i].Runes[0].Id;
        }

        var pages = await RunePages(connection);
        var body = JsonSerializer.Serialize(new
        {
            name = "BRB " + (pages.Count + 1),
            primaryStyleId = primary.Id,
            subStyleId = secondary.Id,
            selectedPerkIds = perks
        });
        using var created = await SendJson(HttpMethod.Post, "/lol-perks/v1/pages", body);
        if (created is null || !created.RootElement.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id))
            return false;

        return await SetCurrentRune(id);
    }

    public static Task<bool> DeleteRunePage(int id) =>
        Send(HttpMethod.Delete, $"/lol-perks/v1/pages/{id}");

    public static async Task<IReadOnlyList<GameModeView>?> Modes()
    {
        var connection = LeagueLockfile.Connect();
        return connection is null ? null : await SummonersRiftModes(connection);
    }

    public static async Task<bool> CreateParty(int queueId)
    {
        var connection = LeagueLockfile.Connect();
        if (connection is null || !await IsSummonersRiftMode(connection, queueId))
            return false;

        return await Send(HttpMethod.Post, "/lol-lobby/v2/lobby", JsonSerializer.Serialize(new { queueId }));
    }

    public static async Task<bool> ChangeParty(int queueId)
    {
        var connection = LeagueLockfile.Connect();
        if (connection is null || !await IsSummonersRiftMode(connection, queueId))
            return false;

        // League has no update method. Posting the queue again switches the open party.
        return await Send(HttpMethod.Post, "/lol-lobby/v2/lobby", JsonSerializer.Serialize(new { queueId }));
    }

    public static Task<bool> LeaveParty() =>
        Send(HttpMethod.Delete, "/lol-lobby/v2/lobby");

    public static async Task<bool> Invite(long? summonerId, string? name)
    {
        var connection = LeagueLockfile.Connect();
        if (connection is null)
            return false;

        var id = summonerId ?? 0;
        if (id <= 0 && !string.IsNullOrWhiteSpace(name))
            id = await LookupSummoner(connection, name.Trim());
        if (id <= 0)
            return false;

        var body = JsonSerializer.Serialize(new[] { new { toSummonerId = id } });
        return await Send(HttpMethod.Post, "/lol-lobby/v2/lobby/invitations", body);
    }

    public static Task<bool> Kick(long summonerId) =>
        summonerId <= 0
            ? Task.FromResult(false)
            : Send(HttpMethod.Post, $"/lol-lobby/v2/lobby/members/{summonerId}/kick");

    public static Task<bool> SetRoles(string? first, string? second)
    {
        var primary = RoleName(first);
        var secondary = RoleName(second);
        if (primary is null || secondary is null)
            return Task.FromResult(false);
        if (primary == "FILL")
            secondary = "UNSELECTED";

        var body = JsonSerializer.Serialize(new { firstPreference = primary, secondPreference = secondary });
        return Send(HttpMethod.Put, "/lol-lobby/v2/lobby/members/localMember/position-preferences", body);
    }

    public static async Task<IReadOnlyList<FriendView>?> Friends()
    {
        var connection = LeagueLockfile.Connect();
        if (connection is null)
            return null;

        using var doc = await Get(connection, "/lol-chat/v1/friends");
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        var friends = new List<FriendView>();
        foreach (var friend in doc.RootElement.EnumerateArray())
        {
            if (!TryLong(friend, "summonerId", out var id) || id <= 0)
                continue;
            var name = PlayerName(friend, "gameName", "gameTag", "name");
            if (name == "Player")
                continue;
            friends.Add(new FriendView(id, name, FriendStatus(Text(friend, "availability")), IconOf(friend)));
        }

        return friends
            .OrderBy(friend => friend.Status switch
            {
                "Online" => 0,
                "Mobile" => 1,
                "Away" => 2,
                "Busy" => 3,
                _ => 4
            })
            .ThenBy(friend => friend.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<string> GameMode(LeagueConnection connection)
    {
        using var doc = await Get(connection, "/lol-gameflow/v1/session");
        if (doc is not null
            && doc.RootElement.TryGetProperty("gameData", out var data)
            && data.TryGetProperty("queue", out var queue)
            && queue.TryGetProperty("gameMode", out var mode)
            && mode.GetString() is { Length: > 0 } name)
            return name;

        return "CLASSIC";
    }

    private static async Task<(int First, int Second)> LocalSpells(LeagueConnection connection)
    {
        using var session = await Get(connection, "/lol-champ-select/v1/session");
        if (session is null
            || !session.RootElement.TryGetProperty("localPlayerCellId", out var localEl)
            || !session.RootElement.TryGetProperty("myTeam", out var team))
            return (0, 0);

        var local = localEl.GetInt32();
        foreach (var player in team.EnumerateArray())
        {
            if (!player.TryGetProperty("cellId", out var cellEl) || cellEl.GetInt32() != local)
                continue;
            var first = player.TryGetProperty("spell1Id", out var firstEl) && firstEl.TryGetInt32(out var spell1) ? spell1 : 0;
            var second = player.TryGetProperty("spell2Id", out var secondEl) && secondEl.TryGetInt32(out var spell2) ? spell2 : 0;
            return (first, second);
        }

        return (0, 0);
    }

    private static async Task<List<SpellChoice>> SpellChoices(LeagueConnection connection, string mode)
    {
        var catalog = await SpellCatalog(connection);
        var matched = catalog.Where(spell => spell.Modes.Contains(mode, StringComparer.OrdinalIgnoreCase)).ToList();
        var source = matched.Count > 0 ? matched : catalog;
        return source
            .OrderBy(spell => spell.Name, StringComparer.OrdinalIgnoreCase)
            .Select(spell => new SpellChoice(spell.Id, spell.Name, spell.Icon))
            .ToList();
    }

    private static async Task<List<RunePageView>> RunePages(LeagueConnection connection)
    {
        using var pagesDoc = await Get(connection, "/lol-perks/v1/pages");
        using var currentDoc = await Get(connection, "/lol-perks/v1/currentpage");
        int? activeId = currentDoc is not null && currentDoc.RootElement.TryGetProperty("id", out var activeEl) && activeEl.TryGetInt32(out var parsed)
            ? parsed
            : null;

        var pages = new List<RunePageView>();
        if (pagesDoc is null || pagesDoc.RootElement.ValueKind != JsonValueKind.Array)
            return pages;

        foreach (var page in pagesDoc.RootElement.EnumerateArray())
        {
            if (!page.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id))
                continue;
            var name = page.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            var editable = page.TryGetProperty("isEditable", out var editEl) && editEl.ValueKind == JsonValueKind.True;
            var deletable = page.TryGetProperty("isDeletable", out var deleteEl) && deleteEl.ValueKind == JsonValueKind.True;
            var primary = page.TryGetProperty("primaryStyleId", out var primaryEl) && primaryEl.TryGetInt32(out var primaryId) ? primaryId : 0;
            var sub = page.TryGetProperty("subStyleId", out var subEl) && subEl.TryGetInt32(out var subId) ? subId : 0;
            var perks = new int[9];
            if (page.TryGetProperty("selectedPerkIds", out var perkEl) && perkEl.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var perk in perkEl.EnumerateArray())
                {
                    if (index >= perks.Length)
                        break;
                    if (perk.TryGetInt32(out var perkId))
                        perks[index] = perkId;
                    index++;
                }
            }

            pages.Add(new RunePageView(
                id,
                string.IsNullOrWhiteSpace(name) ? "Page" : name,
                editable,
                deletable,
                id == activeId,
                primary,
                sub,
                perks));
        }

        return pages;
    }

    private static List<SpellInfo>? spellCatalog;
    private static List<RuneStyleView>? runeStyles;

    private static async Task<List<SpellInfo>> SpellCatalog(LeagueConnection connection)
    {
        if (spellCatalog is { Count: > 0 })
            return spellCatalog;

        using var doc = await Get(connection, "/lol-game-data/assets/v1/summoner-spells.json");
        var list = new List<SpellInfo>();
        if (doc is not null)
        {
            foreach (var spell in doc.RootElement.EnumerateArray())
            {
                if (!spell.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id) || id <= 0)
                    continue;
                if (!spell.TryGetProperty("name", out var nameEl) || nameEl.GetString() is not { Length: > 0 } name)
                    continue;
                var icon = spell.TryGetProperty("iconPath", out var pathEl) ? AssetUrl(pathEl.GetString()) : null;
                if (icon is null)
                    continue;
                var modes = new List<string>();
                if (spell.TryGetProperty("gameModes", out var modeEl) && modeEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var mode in modeEl.EnumerateArray())
                    {
                        if (mode.GetString() is { Length: > 0 } modeName)
                            modes.Add(modeName);
                    }
                }

                list.Add(new SpellInfo(id, name, icon, modes));
            }
        }

        if (list.Count > 0)
            spellCatalog = list;
        return list;
    }

    private static async Task<IReadOnlyList<RuneStyleView>> RuneStyles(LeagueConnection connection)
    {
        if (runeStyles is { Count: > 0 })
            return runeStyles;

        using var perkDoc = await Get(connection, "/lol-game-data/assets/v1/perks.json");
        using var styleDoc = await Get(connection, "/lol-game-data/assets/v1/perkstyles.json")
            ?? await Get(connection, "/lol-game-data/assets/v1/perk-styles.json");
        if (perkDoc is null || styleDoc is null || !styleDoc.RootElement.TryGetProperty("styles", out var styles))
            return [];

        var perks = new Dictionary<int, RuneChoice>();
        foreach (var perk in perkDoc.RootElement.EnumerateArray())
        {
            if (!perk.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id))
                continue;
            var name = perk.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            var icon = perk.TryGetProperty("iconPath", out var pathEl) ? AssetUrl(pathEl.GetString()) : null;
            if (string.IsNullOrWhiteSpace(name) || icon is null)
                continue;
            perks[id] = new RuneChoice(id, name, icon);
        }

        var built = new List<RuneStyleView>();
        foreach (var style in styles.EnumerateArray())
        {
            if (!style.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id))
                continue;
            var name = style.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            var icon = style.TryGetProperty("iconPath", out var pathEl) ? AssetUrl(pathEl.GetString()) : null;
            if (string.IsNullOrWhiteSpace(name) || icon is null || !style.TryGetProperty("slots", out var slots))
                continue;

            var subs = new List<int>();
            if (style.TryGetProperty("allowedSubStyles", out var subEl) && subEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var sub in subEl.EnumerateArray())
                {
                    if (sub.TryGetInt32(out var subId))
                        subs.Add(subId);
                }
            }

            var rows = new List<RuneRow>();
            var stats = new List<RuneRow>();
            foreach (var slot in slots.EnumerateArray())
            {
                var type = slot.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                var row = new List<RuneChoice>();
                if (slot.TryGetProperty("perks", out var perkEl) && perkEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var perk in perkEl.EnumerateArray())
                    {
                        if (perk.TryGetInt32(out var perkId) && perks.TryGetValue(perkId, out var choice))
                            row.Add(choice);
                    }
                }

                if (row.Count == 0)
                    continue;
                if (type is not null && type.Contains("StatMod", StringComparison.Ordinal))
                    stats.Add(new RuneRow(row));
                else
                    rows.Add(new RuneRow(row));
            }

            built.Add(new RuneStyleView(id, name, icon, subs, rows, stats));
        }

        if (built.Count > 0)
            runeStyles = built;
        return built;
    }

    private static List<GameModeView>? modeCache;
    private static DateTime modeCacheAt;

    private static async Task<IReadOnlyList<GameModeView>> SummonersRiftModes(LeagueConnection connection)
    {
        if (modeCache is not null && DateTime.UtcNow - modeCacheAt < TimeSpan.FromSeconds(20))
            return modeCache;

        using var doc = await Get(connection, "/lol-game-queues/v1/queues");
        var modes = new List<(int Priority, GameModeView Mode)>();
        if (doc is not null && doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var queue in doc.RootElement.EnumerateArray())
            {
                if (!queue.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id))
                    continue;
                if (Text(queue, "category") != "PvP" || Text(queue, "queueAvailability") != "Available" || !Flag(queue, "isVisible"))
                    continue;
                if (!queue.TryGetProperty("mapId", out var mapEl) || !mapEl.TryGetInt32(out var mapId) || mapId != 11)
                    continue;
                if ((Text(queue, "type") ?? "").StartsWith("TUTORIAL", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = Text(queue, "description") ?? Text(queue, "name") ?? "Mode";
                var priority = queue.TryGetProperty("gameSelectPriority", out var priorityEl) && priorityEl.TryGetInt32(out var parsed)
                    ? parsed
                    : 0;
                modes.Add((priority, new GameModeView(id, name, Flag(queue, "showPositionSelector"))));
            }
        }

        var list = modes
            .OrderByDescending(mode => mode.Priority)
            .ThenBy(mode => mode.Mode.Name, StringComparer.OrdinalIgnoreCase)
            .Select(mode => mode.Mode)
            .ToList();
        modeCache = list;
        modeCacheAt = DateTime.UtcNow;
        return list;
    }

    private static async Task<bool> IsSummonersRiftMode(LeagueConnection connection, int queueId) =>
        queueId > 0 && (await SummonersRiftModes(connection)).Any(mode => mode.Id == queueId);

    private static async Task<LobbyView?> ReadLobby(LeagueConnection connection)
    {
        using var lobby = await Get(connection, "/lol-lobby/v2/lobby");
        if (lobby is null || !lobby.RootElement.TryGetProperty("gameConfig", out var config))
            return null;
        if (!config.TryGetProperty("queueId", out var queueEl) || !queueEl.TryGetInt32(out var queueId))
            return null;

        var queueName = "Party";
        using (var queue = await Get(connection, $"/lol-game-queues/v1/queues/{queueId}"))
        {
            if (queue is not null)
                queueName = Text(queue.RootElement, "description") ?? Text(queue.RootElement, "name") ?? queueName;
        }

        var localId = 0L;
        var isLeader = false;
        var canInvite = false;
        var firstRole = "UNSELECTED";
        var secondRole = "UNSELECTED";
        if (lobby.RootElement.TryGetProperty("localMember", out var local))
        {
            TryLong(local, "summonerId", out localId);
            isLeader = Flag(local, "isLeader");
            canInvite = local.TryGetProperty("allowedInviteOthers", out var allow)
                ? allow.ValueKind == JsonValueKind.True
                : Flag(local, "canInviteOthers") || isLeader;
            firstRole = RoleName(Text(local, "firstPositionPreference")) ?? "UNSELECTED";
            secondRole = RoleName(Text(local, "secondPositionPreference")) ?? "UNSELECTED";
        }

        var members = new List<LobbyMemberView>();
        if (lobby.RootElement.TryGetProperty("members", out var memberEl) && memberEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var member in memberEl.EnumerateArray())
            {
                if (Flag(member, "isSpectator") || !TryLong(member, "summonerId", out var id) || id <= 0)
                    continue;
                members.Add(new LobbyMemberView(
                    id,
                    await DisplayName(connection, member),
                    Flag(member, "isLeader"),
                    id == localId,
                    RoleName(Text(member, "firstPositionPreference")) ?? "UNSELECTED",
                    RoleName(Text(member, "secondPositionPreference")) ?? "UNSELECTED",
                    IconOf(member)));
            }
        }

        members.Sort((left, right) => left.You == right.You ? 0 : left.You ? -1 : 1);

        var invites = new List<LobbyInviteView>();
        if (lobby.RootElement.TryGetProperty("invitations", out var inviteEl) && inviteEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var invite in inviteEl.EnumerateArray())
            {
                var state = Text(invite, "state");
                if (state is null || state.Equals("Accepted", StringComparison.OrdinalIgnoreCase))
                    continue;
                TryLong(invite, "toSummonerId", out var id);
                var name = PlayerName(invite, "toGameName", "toTagLine", "toSummonerName");
                invites.Add(new LobbyInviteView(id, name == "Player" ? "Invited" : name, state));
            }
        }

        var maxSize = config.TryGetProperty("maxLobbySize", out var maxEl) && maxEl.TryGetInt32(out var max) ? max : 0;
        var showRoles = Flag(config, "showPositionSelector");
        var errors = new List<string>();
        var warnings = new List<string>();
        var copy = await PartyCopy();
        var names = new Dictionary<long, string>();
        foreach (var member in members)
            names[member.SummonerId] = member.Name;
        if (lobby.RootElement.TryGetProperty("restrictions", out var restrictionEl))
            CollectNotices(restrictionEl, errors, copy, names);
        if (lobby.RootElement.TryGetProperty("warnings", out var warningEl))
            CollectNotices(warningEl, warnings, copy, names);

        using (var search = await Get(connection, "/lol-lobby/v2/lobby/matchmaking/search-state"))
        {
            if (search is not null && search.RootElement.TryGetProperty("errors", out var searchErrors))
                CollectNotices(searchErrors, errors, copy, names);
            if (search is not null
                && search.RootElement.TryGetProperty("lowPriorityData", out var low)
                && low.TryGetProperty("penaltyTimeRemaining", out var penaltyEl)
                && penaltyEl.TryGetDouble(out var penalty)
                && penalty > 1
                && copy.TryGetValue("parties_queue_error_queue_dodge_myself_body", out var dodge))
            {
                var total = (int)Math.Ceiling(penalty);
                var timer = copy.TryGetValue("queue_penalty_timer_ms", out var timerText)
                    ? timerText
                    : "{{minutes}}:{{seconds}}";
                timer = timer.Replace("{{minutes}}", (total / 60).ToString(), StringComparison.OrdinalIgnoreCase)
                    .Replace("{{seconds}}", (total % 60).ToString("00"), StringComparison.OrdinalIgnoreCase);
                errors.RemoveAll(line => line.Equals("Currently unable to join this queue", StringComparison.OrdinalIgnoreCase));
                var line = $"{dodge} {timer}";
                if (!errors.Contains(line))
                    errors.Add(line);
            }
        }

        var canStart = !lobby.RootElement.TryGetProperty("canStartActivity", out var startEl) || startEl.ValueKind != JsonValueKind.False;
        if (!canStart && errors.Count == 0 && showRoles)
        {
            var line = ClientNotice(copy, "canStartMatchmaking", new Dictionary<string, string>());
            if (line.Length > 0)
                errors.Add(line);
        }

        var swiftplay = Flag(config, "showQuickPlaySlotSelection");
        JsonElement? embeddedSlots = null;
        if (lobby.RootElement.TryGetProperty("localMember", out var localMember)
            && localMember.TryGetProperty("playerSlots", out var slotEl)
            && slotEl.ValueKind == JsonValueKind.Array)
            embeddedSlots = slotEl;

        var slots = swiftplay ? await ReadSwiftSlots(connection, embeddedSlots) : [];
        return new LobbyView(
            queueId,
            queueName,
            canInvite,
            isLeader,
            showRoles,
            firstRole,
            secondRole,
            maxSize,
            members,
            invites,
            swiftplay,
            slots,
            canStart && errors.Count == 0,
            errors,
            warnings);
    }

    public static async Task<IReadOnlyList<ChampOption>?> SwiftChampions()
    {
        var connection = LeagueLockfile.Connect();
        if (connection is null)
            return null;

        using var doc = await Get(connection, "/lol-champions/v1/owned-champions-minimal");
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        var names = await ChampionCatalog(connection);
        var list = new List<ChampOption>();
        foreach (var champ in doc.RootElement.EnumerateArray())
        {
            if (!champ.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id) || id <= 0)
                continue;
            var alias = Text(champ, "alias");
            if (alias is not null && alias.Contains('_'))
                continue;
            if (champ.TryGetProperty("disabledQueues", out var disabled) && disabled.ValueKind == JsonValueKind.Array)
            {
                var blocked = false;
                foreach (var queue in disabled.EnumerateArray())
                {
                    if (queue.TryGetInt32(out var queueId) && queueId == 480)
                        blocked = true;
                }
                if (blocked)
                    continue;
            }

            var ownership = champ.TryGetProperty("ownership", out var own) ? own : default;
            var playable = Flag(champ, "freeToPlay")
                || (ownership.ValueKind == JsonValueKind.Object && (Flag(ownership, "owned") || Flag(ownership, "rental")));
            if (!playable)
                continue;

            var name = names.TryGetValue(id, out var info) ? info.Name : Text(champ, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;
            list.Add(new ChampOption(id, name));
        }

        return list
            .GroupBy(champ => champ.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(champ => champ.Id).First())
            .OrderBy(champ => champ.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<bool> SetSwiftSlot(int index, int? championId, string? position, int? spellSlot, int? spellId, int? primaryStyleId, int? subStyleId, int[]? perks)
    {
        var connection = LeagueLockfile.Connect();
        if (connection is null || index < 0)
            return false;

        using var doc = await Get(connection, "/lol-lobby/v1/lobby/members/localMember/player-slots");
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array)
            return false;

        var slots = JsonNode.Parse(doc.RootElement.GetRawText()) as JsonArray;
        if (slots is null || index >= slots.Count || slots[index] is not JsonObject slot)
            return false;

        if (championId is > 0)
        {
            slot["championId"] = championId;
            slot["skinId"] = championId * 1000;
        }

        var role = RoleName(position);
        if (role is not null && role is not ("FILL" or "UNSELECTED"))
        {
            for (var otherIndex = 0; otherIndex < slots.Count; otherIndex++)
            {
                if (otherIndex == index || slots[otherIndex] is not JsonObject other)
                    continue;
                if (NodeText(other["positionPreference"]) != role)
                    continue;
                other["positionPreference"] = NodeText(slot["positionPreference"]) ?? "MIDDLE";
            }
            slot["positionPreference"] = role;
        }

        if (spellSlot is 1 or 2 && spellId is > 0)
        {
            var key = spellSlot == 1 ? "spell1" : "spell2";
            var otherKey = spellSlot == 1 ? "spell2" : "spell1";
            if (NodeInt(slot[otherKey]) == spellId)
                slot[otherKey] = NodeInt(slot[key]);
            slot[key] = spellId;
        }

        if (perks is { Length: 9 } && primaryStyleId is > 0 && subStyleId is > 0)
        {
            slot["perks"] = JsonSerializer.Serialize(new
            {
                perkIds = perks,
                perkStyle = primaryStyleId,
                perkSubStyle = subStyleId
            });
        }

        return await Send(HttpMethod.Put, "/lol-lobby/v1/lobby/members/localMember/player-slots", slots.ToJsonString());
    }

    private static async Task<List<SwiftSlotView>> ReadSwiftSlots(LeagueConnection connection, JsonElement? embedded)
    {
        JsonElement source;
        JsonDocument? fetched = null;
        if (embedded is { ValueKind: JsonValueKind.Array } ready && ready.GetArrayLength() > 0)
            source = ready;
        else
        {
            fetched = await Get(connection, "/lol-lobby/v1/lobby/members/localMember/player-slots");
            if (fetched is null || fetched.RootElement.ValueKind != JsonValueKind.Array)
                return [];
            source = fetched.RootElement;
        }

        try
        {
            var names = await ChampionCatalog(connection);
            var icons = await SpellIcons(connection);
            var slots = new List<SwiftSlotView>();
            foreach (var slot in source.EnumerateArray())
            {
                var championId = slot.TryGetProperty("championId", out var champEl) && champEl.TryGetInt32(out var parsed) ? parsed : 0;
                var spell1 = slot.TryGetProperty("spell1", out var firstEl) && firstEl.TryGetInt32(out var first) ? first : 0;
                var spell2 = slot.TryGetProperty("spell2", out var secondEl) && secondEl.TryGetInt32(out var second) ? second : 0;
                var position = RoleName(Text(slot, "positionPreference")) ?? "UNSELECTED";
                var primary = 0;
                var sub = 0;
                var perkIds = new int[9];
                if (Text(slot, "perks") is { } perkText)
                {
                    try
                    {
                        using var perkDoc = JsonDocument.Parse(perkText);
                        var root = perkDoc.RootElement;
                        if (root.TryGetProperty("perkStyle", out var styleEl) && styleEl.TryGetInt32(out var styleId))
                            primary = styleId;
                        if (root.TryGetProperty("perkSubStyle", out var subEl) && subEl.TryGetInt32(out var subId))
                            sub = subId;
                        if (root.TryGetProperty("perkIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
                        {
                            var perkIndex = 0;
                            foreach (var perk in ids.EnumerateArray())
                            {
                                if (perkIndex >= perkIds.Length)
                                    break;
                                if (perk.TryGetInt32(out var perkId))
                                    perkIds[perkIndex] = perkId;
                                perkIndex++;
                            }
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }

                var champion = championId > 0 && names.TryGetValue(championId, out var info) ? info.Name : "";
                slots.Add(new SwiftSlotView(
                    championId,
                    champion,
                    position,
                    spell1,
                    spell2,
                    icons.TryGetValue(spell1, out var firstIcon) ? firstIcon : null,
                    icons.TryGetValue(spell2, out var secondIcon) ? secondIcon : null,
                    primary,
                    sub,
                    perkIds));
            }

            return slots;
        }
        finally
        {
            fetched?.Dispose();
        }
    }

    private static int NodeInt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

    private static string? NodeText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static Dictionary<string, string>? partyCopy;

    private static readonly HttpClient CopyClient = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static async Task<IReadOnlyDictionary<string, string>> PartyCopy()
    {
        if (partyCopy is { Count: > 0 })
            return partyCopy;

        try
        {
            var json = await CopyClient.GetStringAsync(
                "https://raw.communitydragon.org/latest/plugins/rcp-fe-lol-parties/global/default/trans.json");
            using var doc = JsonDocument.Parse(json);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { Length: > 0 } text)
                    map[prop.Name] = text;
            }

            if (map.Count > 0)
                partyCopy = map;
            return map;
        }
        catch
        {
            return partyCopy ?? new Dictionary<string, string>();
        }
    }

    private static void CollectNotices(
        JsonElement array,
        List<string> lines,
        IReadOnlyDictionary<string, string> copy,
        IReadOnlyDictionary<long, string> names)
    {
        if (array.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var raw = item.GetString() ?? "";
                var line = ClientNotice(copy, raw, new Dictionary<string, string>());
                if (line.Length > 0 && !lines.Contains(line))
                    lines.Add(line);
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var code = Text(item, "restrictionCode") ?? Text(item, "errorCode") ?? Text(item, "warningCode") ?? "";
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (item.TryGetProperty("restrictionArgs", out var argEl) && argEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in argEl.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { Length: > 0 } text)
                        args[prop.Name] = text;
                    else if (prop.Value.ValueKind == JsonValueKind.Number)
                        args[prop.Name] = prop.Value.ToString();
                }
            }

            if (item.TryGetProperty("summonerIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
            {
                var players = new List<string>();
                foreach (var idEl in ids.EnumerateArray())
                {
                    if (idEl.TryGetInt64(out var id) && names.TryGetValue(id, out var name))
                        players.Add(name);
                }

                if (players.Count > 0)
                {
                    args["players"] = string.Join(", ", players);
                    args["player"] = players[0];
                    args["summonerName"] = players[0];
                    args["penalizedSummonerName"] = players[0];
                }
            }

            var notice = ClientNotice(copy, code, args);
            if (notice.Length > 0 && !lines.Contains(notice))
                lines.Add(notice);
        }
    }

    private static string ClientNotice(IReadOnlyDictionary<string, string> copy, string code, Dictionary<string, string> args)
    {
        if (code.Length == 0 || copy.Count == 0)
            return "";

        string? template = null;
        foreach (var key in new[]
        {
            "game_select_queue_restriction_party_" + code,
            "parties_queue_restriction_player_" + code,
            "game_select_queue_restriction_player_" + code,
            "player_solo_queue_restriction_" + code,
            "parties_" + code
        })
        {
            if (copy.TryGetValue(key, out var found))
            {
                template = found;
                break;
            }
        }

        if (template is null)
            return "";

        foreach (var pair in args)
            template = template.Replace("{{" + pair.Key + "}}", pair.Value, StringComparison.OrdinalIgnoreCase);

        template = template.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase);
        return System.Text.RegularExpressions.Regex.Replace(template, "<[^>]+>", "").Trim();
    }

    private static async Task<long> LookupSummoner(LeagueConnection connection, string name)
    {
        var hash = name.IndexOf('#');
        if (hash > 0 && hash < name.Length - 1)
        {
            var game = Uri.EscapeDataString(name[..hash].Trim());
            var tag = Uri.EscapeDataString(name[(hash + 1)..].Trim());
            using var alias = await Get(connection, $"/lol-summoner/v1/alias/lookup?gameName={game}&tagLine={tag}");
            if (alias is not null && TryLong(alias.RootElement, "summonerId", out var id))
                return id;
        }

        using var byName = await Get(connection, "/lol-summoner/v1/summoners?name=" + Uri.EscapeDataString(name));
        if (byName is not null && TryLong(byName.RootElement, "summonerId", out var legacy))
            return legacy;
        return 0;
    }

    private static int IconOf(JsonElement element)
    {
        foreach (var name in new[] { "summonerIconId", "profileIconId", "icon" })
        {
            if (element.TryGetProperty(name, out var property) && property.TryGetInt32(out var id) && id > 0)
                return id;
        }

        return 29;
    }

    private static string? RoleName(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return null;
        var name = role.Trim().ToUpperInvariant();
        return name is "TOP" or "JUNGLE" or "MIDDLE" or "BOTTOM" or "UTILITY" or "FILL" or "UNSELECTED"
            ? name
            : null;
    }

    private static string FriendStatus(string? availability) => availability switch
    {
        "chat" => "Online",
        "mobile" => "Mobile",
        "away" => "Away",
        "dnd" => "Busy",
        _ => "Offline"
    };

    private static string PlayerName(JsonElement element, string gameKey, string tagKey, string fallbackKey)
    {
        var game = Text(element, gameKey);
        var tag = Text(element, tagKey);
        if (game is not null)
            return tag is null ? game : $"{game}#{tag}";
        return Text(element, fallbackKey) ?? "Player";
    }

    private static string? Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return null;
        var text = property.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;

    private static bool TryLong(JsonElement element, string name, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property))
            return false;
        if (property.TryGetInt64(out value))
            return true;
        return property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out value);
    }

    private static string? AssetUrl(string? path)
    {
        const string prefix = "/lol-game-data/assets/";
        if (string.IsNullOrEmpty(path) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        return "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/"
            + path[prefix.Length..].ToLowerInvariant();
    }
}

public record ChampionInfo(int Id, string Name, bool Classic);

public record Screen(string Phase, string Title, string? Detail, string? Action, ChampSelectView? ChampSelect = null, int? ClockMs = null, bool ClockDown = true, string? ClockPhase = null, LobbyView? Lobby = null, int? ClockTotalMs = null, string? ReadyResponse = null);
public record GameModeView(int Id, string Name, bool Roles);
public record LobbyView(int QueueId, string QueueName, bool CanInvite, bool IsLeader, bool ShowRoles, string FirstRole, string SecondRole, int MaxSize, IReadOnlyList<LobbyMemberView> Members, IReadOnlyList<LobbyInviteView> Invites, bool Swiftplay = false, IReadOnlyList<SwiftSlotView>? Slots = null, bool CanQueue = true, IReadOnlyList<string>? Errors = null, IReadOnlyList<string>? Warnings = null);
public record SwiftSlotView(int ChampionId, string Champion, string Position, int Spell1, int Spell2, string? Spell1Icon, string? Spell2Icon, int PrimaryStyleId, int SubStyleId, int[] Perks);
public record LobbyMemberView(long SummonerId, string Name, bool Leader, bool You, string FirstRole, string SecondRole, int Icon);
public record LobbyInviteView(long SummonerId, string Name, string State);
public record FriendView(long SummonerId, string Name, string Status, int Icon);
public record ChampSelectView(string? Turn, int? ActionId, int? HoveredChampionId, IReadOnlyList<ChampSlot> MyTeam, IReadOnlyList<ChampSlot> TheirTeam, IReadOnlyList<ChampOption> Champions, IReadOnlyList<int?> MyBans, IReadOnlyList<int?> TheirBans);
public record ChampSlot(string Name, string? Position, string? Champion, int? ChampionId, string? Spell1 = null, string? Spell2 = null, bool Hovering = false, string? Acting = null, int? BanHoverId = null, bool You = false);
public record ChampOption(int Id, string Name);
public record Loadout(int Spell1, int Spell2, IReadOnlyList<SpellChoice> Spells, IReadOnlyList<RunePageView> Pages, int? ActivePageId, IReadOnlyList<RuneStyleView> Styles);
public record SpellChoice(int Id, string Name, string Icon);
public record SpellInfo(int Id, string Name, string Icon, IReadOnlyList<string> Modes);
public record RunePageView(int Id, string Name, bool Editable, bool Deletable, bool Active, int PrimaryStyleId, int SubStyleId, int[] Perks);
public record RuneStyleView(int Id, string Name, string Icon, IReadOnlyList<int> Subs, IReadOnlyList<RuneRow> Rows, IReadOnlyList<RuneRow> Stats);
public record RuneRow(IReadOnlyList<RuneChoice> Runes);
public record RuneChoice(int Id, string Name, string Icon);
