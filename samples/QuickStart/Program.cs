using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Arcademia.Link;

var settings = ParseArgs(args);
if (!settings.TryGetValue("client", out var clientId))
{
    Console.WriteLine("Usage: QuickStart --client <client id> [--base <url>] [--store <path>] [--auto <username> <password>] [--signout]");
    return 1;
}

var baseUrl = new Uri(settings.GetValueOrDefault("base", "https://manager.arcademia.ac"));
var options = new ArcademiaLinkOptions
{
    ClientId = clientId,
    BaseUrl = baseUrl,
    AppName = "Arcademia QuickStart",
    TokenStore = settings.TryGetValue("store", out var storePath)
        ? new FileTokenStore(storePath)
        : FileTokenStore.ForClient(clientId),
};

if (settings.TryGetValue("auto", out var auto))
{
    var parts = auto.Split('\n');
    options.OpenBrowser = (uri, ct) => ApproveHeadlessAsync(baseUrl, uri, parts[0], parts[1], ct);
}

using var client = new ArcademiaLinkClient(options);
client.SessionExpired += (_, _) => Console.WriteLine("Session expired, sign in again.");

if (settings.ContainsKey("signout"))
{
    await client.SignOutAsync();
    Console.WriteLine("Signed out.");
    return 0;
}

if (await client.TryRestoreSessionAsync())
{
    Console.WriteLine("Restored a saved session.");
}
else
{
    Console.WriteLine("Opening the browser to sign in...");
    var user = await client.SignInAsync();
    Console.WriteLine($"Signed in as {user?.Username ?? "(no profile scope)"}.");
}

var cachePath = Path.Combine(Path.GetTempPath(), $"arcademia-link-quickstart-{clientId}.json");
AchievementSnapshot previous = null;
if (File.Exists(cachePath))
    previous = JsonSerializer.Deserialize<AchievementSnapshot>(File.ReadAllText(cachePath));

var snapshot = await client.GetAchievementsAsync();
File.WriteAllText(cachePath, JsonSerializer.Serialize(snapshot));

Console.WriteLine($"{snapshot.User?.Username}: {snapshot.UnlockedCount}/{snapshot.AchievementCount} achievements across {snapshot.GameCount} games");
foreach (var game in snapshot.Games)
{
    Console.WriteLine($"  {game.Name} ({game.UnlockedCount}/{game.AchievementCount}, {game.CompletionPercent}%)");
    foreach (var a in game.Achievements)
        Console.WriteLine($"    [{(a.Unlocked ? "x" : " ")}] {a.Name} ({a.GlobalPercent}% of players){(a.Unlocked ? $" unlocked {a.UnlockedAt:u}" : "")}");
}

if (previous != null)
{
    var fresh = snapshot.NewlyUnlockedSince(previous);
    Console.WriteLine(fresh.Count == 0 ? "No new unlocks since last run." : $"{fresh.Count} new unlock(s) since last run:");
    foreach (var (game, achievement) in fresh)
        Console.WriteLine($"  {game.Name}: {achievement.Name}");
}

return 0;

static Dictionary<string, string> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string>();
    for (var i = 0; i < args.Length; i++)
    {
        var key = args[i].TrimStart('-');
        if (key == "auto" && i + 2 < args.Length)
        {
            result[key] = args[i + 1] + "\n" + args[i + 2];
            i += 2;
        }
        else if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            result[key] = args[++i];
        else
            result[key] = "";
    }
    return result;
}

static async Task ApproveHeadlessAsync(Uri baseUrl, Uri authorize, string username, string password, CancellationToken ct)
{
    var cookies = new CookieContainer();
    using var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { BaseAddress = baseUrl };
    var login = await http.PostAsJsonAsync("/api/Auth/Login", new { username, password, rememberMe = false }, ct);
    login.EnsureSuccessStatusCode();

    var query = authorize.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
    var preview = await http.GetFromJsonAsync<JsonElement>("/api/Link/Authorize" + authorize.Query, ct);
    Console.WriteLine($"Consent screen: valid={preview.GetProperty("valid")} app={preview.GetProperty("app").GetProperty("name")}");

    var decision = await http.PostAsJsonAsync(
        "/api/Link/Authorize",
        new
        {
            clientId = query["client_id"],
            redirectUri = query["redirect_uri"],
            responseType = query["response_type"],
            scope = query["scope"],
            state = query["state"],
            codeChallenge = query["code_challenge"],
            codeChallengeMethod = query["code_challenge_method"],
            approve = true,
        },
        ct
    );
    decision.EnsureSuccessStatusCode();
    var redirect = (await decision.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct)).GetProperty("redirectUrl").GetString();
    _ = Task.Run(async () =>
    {
        using var browser = new HttpClient();
        await browser.GetAsync(redirect);
    });
}
