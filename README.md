# Arcademia.Link

Arcademia Link lets people sign in to your app with their Arcademia account and read the achievements they have earned, alongside every public achievement on Arcademia. The library handles the whole OAuth 2.0 sign-in (authorization code flow with PKCE), the browser round trip, storing the session, and refreshing it quietly in the background. You get plain C# objects back and build whatever UI you like.

The package is free to use, but the API isn't open: every app needs a client ID, which the Arcademia team issues on request (see below).

## What your app can see

With the user's permission, your app can read:

- their username (the `profile` scope)
- every public achievement on Arcademia, with the ones they have personally earned marked as unlocked, plus when they unlocked it and how rare it is (the `achievements` scope)

Your app never sees their email address, password, site, role, owned games, or team unlocks. Hidden achievements stay hidden (name, description, icon and API name are blanked out) until the user earns them.

The user ID you receive is stable for your app but different for every app, so it can't be used to match people across services.

## Getting a client ID

Ask the Arcademia team to register your app. They will need:

- the app's name and a one-line description (users see both on the consent screen)
- a homepage URL, if you have one
- your redirect URI. For a desktop app this is `http://127.0.0.1/callback`. Any port is accepted on 127.0.0.1, so the library picks a free one each time.

You'll get back a client ID that looks like `arcapp_1a2b3c...`. It is not a secret and can ship inside your app.

## Install

Install from NuGet:

```
dotnet add package Arcademia.Link
```

Or search for `Arcademia.Link` in the Visual Studio or Rider package manager.

The library targets .NET Standard 2.0, so it works in .NET Framework 4.6.2+, .NET 6/7/8/9/10, WPF, WinForms, Avalonia, MAUI (desktop) and console apps.

## Quick start

```csharp
using Arcademia.Link;

var arcademia = new ArcademiaLinkClient(new ArcademiaLinkOptions
{
    ClientId = "arcapp_your_client_id",
    AppName = "My Achievement Tracker",
});

if (!await arcademia.TryRestoreSessionAsync())
{
    var user = await arcademia.SignInAsync();
    Console.WriteLine($"Signed in as {user.Username}");
}

var snapshot = await arcademia.GetAchievementsAsync();
foreach (var game in snapshot.Games)
{
    Console.WriteLine($"{game.Name}: {game.UnlockedCount}/{game.AchievementCount}");
    foreach (var achievement in game.Achievements)
        Console.WriteLine($"  [{(achievement.Unlocked ? "x" : " ")}] {achievement.Name} ({achievement.GlobalPercent}% of players)");
}
```

`SignInAsync` opens the user's default browser on the Arcademia consent page. If they aren't logged in to Arcademia yet, they log in first. When they press Allow, the browser is sent back to a small local listener the library runs on 127.0.0.1, the tab shows a "you're signed in" page, and `SignInAsync` returns. If they press Cancel, it throws `ArcademiaSignInCancelledException` with `Error == "access_denied"`.

## Sessions

After sign-in the session is saved and reused, so users only sign in once. By default it is stored at `%LOCALAPPDATA%\Arcademia\Link\<client id>.tokens`, encrypted with Windows DPAPI for the current Windows user. On other platforms the file is plain JSON, so point it somewhere private or supply your own store.

- `TryRestoreSessionAsync()` loads a saved session. Call it on startup.
- `IsSignedIn` tells you whether a session is loaded.
- `SignOutAsync()` tells Arcademia to end the session and deletes the saved copy.
- The `SessionExpired` event fires if the session stops working, for example because the user unlinked your app in their Arcademia settings. Any call made at that point throws `ArcademiaSignInRequiredException`, and you should show your "Sign in with Arcademia" button again.

Access tokens last an hour and are refreshed automatically before they run out. Sessions don't expire on their own; they last until the user or Arcademia revokes them.

To store the session somewhere else (your own settings file, a database, the OS keychain), implement `ITokenStore`:

```csharp
public interface ITokenStore
{
    Task<StoredTokens> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(StoredTokens tokens, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}
```

`MemoryTokenStore` and `FileTokenStore` are included.

## Reading achievements

| Call | Returns |
| --- | --- |
| `GetUserAsync()` | `ArcademiaUser` with `Id` and `Username` (needs `profile`) |
| `GetAchievementsAsync()` | `AchievementSnapshot` with every game that has public achievements |
| `GetAchievementsAsync(unlockedOnly: true)` | Only games and achievements the user has unlocked |
| `GetGameAchievementsAsync(gameId)` | One `ArcademiaGame` |

`AchievementSnapshot` has the user (when the `profile` scope was granted), totals, and a list of `ArcademiaGame`. Each game has `Id`, `Name`, `ThumbnailUrl`, `AchievementCount`, `UnlockedCount`, `CompletionPercent` and a list of `ArcademiaAchievement`:

| Property | Meaning |
| --- | --- |
| `Id` | Stable numeric ID. Use this as your key. |
| `ApiName` | The developer's ID for it, such as `FIRST_BLOOD`. Null while hidden. |
| `Name`, `Description` | Display text. "Hidden achievement" and null while hidden. |
| `IconUrl` | Full URL of the icon (PNG or JPEG). Null while hidden. Arcademia shows locked achievements in greyscale; you can do the same. |
| `Hidden` | The developer marked it secret. |
| `Revealed` | False only for hidden achievements the user hasn't earned. |
| `Unlocked`, `UnlockedAt` | Whether the user has earned it personally, and when (UTC). |
| `GlobalPercent` | Share of the game's players who have it, 0 to 100. |

Icons are small, often pixel art at 30 to 128 pixels. Use nearest-neighbour scaling for sources of 128px or less so they stay crisp.

### Spotting new unlocks

Keep the last snapshot you fetched and compare:

```csharp
var latest = await arcademia.GetAchievementsAsync();
foreach (var (game, achievement) in latest.NewlyUnlockedSince(previous))
    ShowToast($"{game.Name}: {achievement.Name}");
previous = latest;
```

The snapshot classes are plain objects, so you can serialise them with `System.Text.Json` to keep them between runs.

Please don't poll more than about once a minute per user. The API allows 60 requests a minute for each signed-in user, and a refresh on app start plus a manual "sync" button is usually plenty.

## Options

| Option | Default | Notes |
| --- | --- | --- |
| `ClientId` | required | Issued by Arcademia. |
| `AppName` | "the app" | Used on the page shown in the browser after sign-in. |
| `BaseUrl` | `https://manager.arcademia.ac` | Change only for testing against staging. |
| `Scopes` | `profile`, `achievements` | Ask only for what you need. |
| `RedirectPath` | `/callback` | Must match the path registered with Arcademia. |
| `RedirectPort` | 0 (any free port) | Set a fixed port only if you have to. |
| `SignInTimeout` | 5 minutes | How long `SignInAsync` waits for the browser. |
| `TokenStore` | `FileTokenStore` per client ID | See Sessions. |
| `OpenBrowser` | system browser | `Func<Uri, CancellationToken, Task>`, for example to open an embedded WebView2 instead. |
| `CallbackPage` | built-in page | `Func<bool success, string message, string html>` to brand the "you're signed in" tab. |
| `HttpClient` | new client | Pass your own to share connection pooling or add a proxy. |

## Errors

Everything the library throws derives from `ArcademiaLinkException`, which carries an `Error` code and `StatusCode`.

| Exception / Error | When |
| --- | --- |
| `ArcademiaSignInCancelledException` `access_denied` | The user pressed Cancel. |
| `ArcademiaSignInCancelledException` `timeout` | The browser didn't come back within `SignInTimeout`. |
| `ArcademiaSignInRequiredException` | Not signed in, or the session was revoked. Sign in again. |
| `insufficient_scope` (403) | You called something your app or the user didn't allow. |
| `rate_limited` (429) | Slow down and try again in a minute. |
| `network_error` | Arcademia couldn't be reached. The session is kept. |

## Using the HTTP API directly

If you're not using .NET, this is the flow the library implements. It is standard OAuth 2.0 (RFC 6749) with PKCE (RFC 7636) and loopback redirects for native apps (RFC 8252).

1. Create a `code_verifier` (43 to 128 random URL-safe characters) and its `code_challenge` (base64url SHA-256 of the verifier, no padding). Create a random `state`.
2. Start listening on `http://127.0.0.1:<port>/callback`.
3. Open the browser at:

   ```
   https://manager.arcademia.ac/link/authorize
     ?response_type=code
     &client_id=arcapp_...
     &redirect_uri=http://127.0.0.1:<port>/callback
     &scope=profile achievements
     &state=<state>
     &code_challenge=<challenge>
     &code_challenge_method=S256
   ```

4. The browser comes back with `?code=...&state=...`, or `?error=access_denied&state=...` if the user said no. Check that `state` matches.
5. Exchange the code within 5 minutes. The body is `application/x-www-form-urlencoded`:

   ```
   POST /api/Link/Token
   grant_type=authorization_code&client_id=...&code=...&redirect_uri=<same as step 3>&code_verifier=...
   ```

   ```json
   {
     "access_token": "arcat_...",
     "token_type": "Bearer",
     "expires_in": 3600,
     "refresh_token": "arcrt_...",
     "scope": "profile achievements"
   }
   ```

6. Call the API with `Authorization: Bearer <access_token>`.
7. When the access token expires, refresh it. Every refresh returns a new refresh token and the old one stops working, so save the new one straight away.

   ```
   POST /api/Link/Token
   grant_type=refresh_token&client_id=...&refresh_token=...
   ```

8. To sign out, `POST /api/Link/Revoke` with `client_id` and `token` (either token). It always returns 200.

Errors from the token endpoint use the standard shape, `{"error": "invalid_grant", "error_description": "..."}`. `invalid_grant` on a refresh means the session is over and the user needs to sign in again.

### Endpoints

All under `https://manager.arcademia.ac/api/Link/v1`:

| Request | Scope | Response |
| --- | --- | --- |
| `GET /me` | `profile` | `{"id": "9f2c...", "username": "player1"}` |
| `GET /me/achievements` | `achievements` | Snapshot of every game (add `?unlockedOnly=true` to trim it) |
| `GET /me/achievements/{gameId}` | `achievements` | One game |

Example snapshot (trimmed):

```json
{
  "user": { "id": "9f2c41d0a8b37e65c1d2e3f4a5b6c7d8", "username": "player1" },
  "generatedAt": "2026-09-26T15:47:43Z",
  "gameCount": 1,
  "achievementCount": 3,
  "unlockedCount": 1,
  "games": [
    {
      "id": 4,
      "name": "Army of Myths",
      "thumbnailUrl": "https://manager.arcademia.ac/api/Games/1/Thumbnail",
      "achievementCount": 3,
      "unlockedCount": 1,
      "achievements": [
        {
          "id": 12,
          "apiName": "FIRST_LIGHT",
          "name": "Let there be light",
          "description": "Turn on the first lamp",
          "iconUrl": "https://manager.arcademia.ac/api/Achievements/Icons/abc.png",
          "hidden": false,
          "revealed": true,
          "unlocked": true,
          "unlockedAt": "2026-09-20T18:02:11Z",
          "globalPercent": 42.5
        },
        {
          "id": 13,
          "apiName": null,
          "name": "Hidden achievement",
          "description": null,
          "iconUrl": null,
          "hidden": true,
          "revealed": false,
          "unlocked": false,
          "unlockedAt": null,
          "globalPercent": 3.1
        }
      ]
    }
  ]
}
```

A `401` with `WWW-Authenticate: Bearer error="invalid_token"` means the access token is missing, expired or revoked; refresh and retry once. Rate limits are 30 token requests a minute per IP address and 60 API requests a minute per session.

## Sample

`samples/QuickStart` signs in, prints every achievement, and lists new unlocks since the last run:

```
dotnet run --project samples/QuickStart -- --client arcapp_your_client_id
```
