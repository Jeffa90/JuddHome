# JuddHome

A Jellyfin server plugin that **replaces the default home screen** with a
Netflix/Disney+ style modular home screen with intelligent recommendations.

- Target: **Jellyfin 10.11.11** (`targetAbi 10.11.11.0`), **.NET 9.0**
- Fully self-contained: no third-party plugin libraries, no external CDN
  dependencies (all web assets are embedded in the DLL)
- All data access uses Jellyfin's injected interfaces (`ILibraryManager`,
  `IUserDataManager`, `IUserManager`, `ITVSeriesManager`) — no HTTP loopback

## Sections

Default order (each section can be hidden/reordered per user):

1. **Hero Banner** — auto-rotating featured banner (Continue Watching + highly
   rated unwatched), Play / More Info buttons, crossfade, arrows and dots
2. **Continue Watching** — items 5–90% complete, with progress bars
3. **Next Up** — next unwatched episode of in-progress shows
4. **Because You Watched** — up to 5 rows seeded by your last completed items,
   matched by genre + studio + people, deduplicated across rows
5. **Recommended For You** — weighted scoring: genre match 40%, community
   rating >7.5 25%, added <90 days 20%, actors from your history 15%
6. **Latest Movies**
7. **Latest TV Shows**
8. **Watch Again** — fully watched movies and shows
9. **Popular In Your Library** — top unwatched by community rating
10. **My List** — a Jellyfin playlist named exactly `My List` (the row offers
    to create it if missing)

Empty sections are silently skipped. Users with fewer than 3 watched items get
fallbacks: no Because You Watched, an expanded Popular row instead of
Recommended For You, and a top-rated hero banner.

## Build

Requires the .NET 9 SDK (or newer with the net9.0 targeting pack).

```bash
dotnet build -c Release
```

The output you deploy is `bin/Release/net9.0/` — it contains the plugin DLL
and `meta.json` (copied automatically by the build).

## Deploy on Windows

Copy the contents of `bin/Release/net9.0/` to:

```
C:\ProgramData\Jellyfin\Server\plugins\JuddHome_1.0.0\
```

Restart the Jellyfin service:

```powershell
Restart-Service JellyfinServer
```

## Deploy on Linux (native)

Copy the contents of `bin/Release/net9.0/` to:

```
/var/lib/jellyfin/plugins/JuddHome_1.0.0/
```

```bash
sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/JuddHome_1.0.0/
sudo systemctl restart jellyfin
```

## Deploy on Linux (Docker / Dockge)

Copy the contents of `bin/Release/net9.0/` to the plugins folder inside the
container's config volume:

```
/path/to/jellyfin/config/plugins/JuddHome_1.0.0/
```

Restart the Jellyfin container via Dockge, or:

```bash
docker restart jellyfin
```

## How the home screen takeover works

On startup the plugin injects
`<script src="../JuddHome/Web/home.js" defer></script>` into the web client's
`index.html`. The script activates only on the home route, hides the vanilla
home sections and mounts the JuddHome UI in their place. It authenticates with
the existing `window.ApiClient` token — there is no separate login.

**If the web directory is read-only** (some Docker images), the injection is
skipped with a warning in the Jellyfin log and
`GET /JuddHome/Health` reports `"ScriptInjected": false`. In that case add the
script tag above manually before `</body>` in `jellyfin-web/index.html`
(inside the container: `/usr/share/jellyfin/web/index.html` — you may need to
re-add it after image updates).

## Configuration

- **Admin:** Dashboard → Plugins → JuddHome — server-wide section defaults
  (toggles + drag to reorder), items per row (max 40), hero banner on/off and
  speed (5/8/12 s), recommendation refresh interval (1/3/6/12 h), list of
  per-user overrides with a reset button.
- **Users:** on the home screen, the "⚙ Customise home" button — or
  **hamburger menu → JuddHome Settings** — to hide/show and reorder their own
  sections. New users inherit the server-wide defaults.

## REST API

All endpoints require Jellyfin authentication (the standard
`Authorization: MediaBrowser Token=...` header); admin endpoints require an
admin user.

| Endpoint | Description |
|---|---|
| `GET /JuddHome/Sections` | Ordered, enabled sections for the current user |
| `GET /JuddHome/Section/{sectionType}` | Items for one section |
| `GET /JuddHome/Hero` | 5 hero banner items |
| `GET /JuddHome/Recommendations` | Scored recommendation list |
| `GET/POST /JuddHome/Config/User` | Read / save own section preferences |
| `POST /JuddHome/Config/Admin` | Save server-wide defaults (admin) |
| `GET /JuddHome/Config/Users` | List per-user overrides (admin) |
| `POST /JuddHome/Config/ResetUser/{userId}` | Reset a user to defaults (admin) |
| `GET /JuddHome/Health` | Version, status, per-section refresh timestamps |
| `POST /JuddHome/MyList/Create` | Create the "My List" playlist |
| `GET /JuddHome/PlayTarget/{itemId}` | Resolve what actually plays (series → next episode) |

## Troubleshooting

- **Home screen unchanged after install** — check `GET /JuddHome/Health`. If
  `ScriptInjected` is false, see the read-only web directory note above. Also
  hard-refresh the browser (Ctrl+F5) so the modified `index.html` is fetched.
- **A section is missing** — empty sections are hidden by design; also check
  the user hasn't disabled it in JuddHome Settings.
- **Plugin marked Malfunctioned** — confirm the folder name is
  `JuddHome_1.0.0`, `meta.json` is present next to the DLL, and the server is
  Jellyfin 10.11.x.
