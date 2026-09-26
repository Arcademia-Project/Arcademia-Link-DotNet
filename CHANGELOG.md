# Changelog

## 0.1.0

- Sign in with Arcademia using the authorization code flow with PKCE and a loopback redirect on 127.0.0.1.
- Sessions saved per client ID, encrypted with DPAPI on Windows, refreshed automatically.
- `GetUserAsync`, `GetAchievementsAsync`, `GetGameAchievementsAsync`.
- `AchievementSnapshot.NewlyUnlockedSince` for spotting new unlocks between syncs.
- `SignOutAsync` revokes the session on the server.
