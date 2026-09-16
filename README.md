# Shoko AnimeSchedule.net Plugin

A [Shoko](https://shokoanime.com/) airing schedule provider backed by [AnimeSchedule.net](https://animeschedule.net/). Pulls raw (original-language), subtitled and dubbed airing times, delays and streaming links for the anime already in your library.

Data is provided by [AnimeSchedule.net](https://animeschedule.net/), as required by their [API terms of use](https://animeschedule.net/api-terms-of-use).

## Features

- **Raw, sub and dub schedules** — tracks all three release kinds per anime, each as its own schedule per streaming platform.
- **Delay-aware** — reports `delayed-air` episodes as delayed, keeping the original slot alongside the new one (or leaving the airing slotless when no new date has been announced yet).
- **Multi-episode releases** — a timetable entry covering several episodes at once (e.g. a two-episode premiere) is submitted as one airing per episode, linked together.
- **Streaming channels** — registers a channel for every platform AnimeSchedule.net lists a stream on (Crunchyroll, Netflix, Amazon, HIDIVE, Hulu, Funimation, Wakanim, YouTube), carrying the episode's page URL.
- **Rate-limit aware** — reads AnimeSchedule.net's `X-RateLimit-*` response headers and backs off accordingly, rather than guessing a fixed request rate.
- **Efficient sweeps** — the recurring sweep fetches each of the six timetable pages (raw/sub/dub × this week/next week) once per run and matches them against every series, instead of one round-trip per anime.

## Requirements

AnimeSchedule.net rate-limits requests to 120/minute, counted **per application and per IP address independently**. A token bundled with the plugin would therefore be shared — and throttled — by every install using it. Each user needs their own AnimeSchedule.net application token:

1. Register an application at [animeschedule.net/api/v3/documentation/apps](https://animeschedule.net/api/v3/documentation/apps).
2. Copy its token into the plugin's **App Token** setting in the Shoko Web UI.

The provider does nothing (and `RefreshAsync` returns `false`) until a token is configured.

## Installation

### GUI (Recommended)

1. Open the Shoko Web UI and navigate to **Settings → Plugins → Repositories**.
2. Add the manifest URL:
   ```
   https://raw.githubusercontent.com/revam/dotnet-shoko-plugin-animeschedule/stable/manifest.json
   ```
3. Go to **Settings → Plugins → Browse** and find **AnimeSchedule.net**.
4. Click **Install** on the desired version.
5. Restart Shoko.

### Manual

1. Download the latest `Shoko.Plugin.AnimeSchedule-<version>-any.zip` from the [Releases](../../releases) page.
2. Extract the ZIP and place `Shoko.Plugin.AnimeSchedule.dll` into your Shoko **Plugins** folder.
3. Restart Shoko.

## Configuration

| Setting | Default | Description |
|---|---|---|
| **App Token** | *(none)* | Your personal AnimeSchedule.net application token. Required. |

## How it works

- A series is keyed to AnimeSchedule.net by resolving its AniDB anime ID through `GET /anime?anidb-ids=`, which returns the anime's unique `route` slug, total episode count and airing status.
- `GET /timetables/{raw|sub|dub}?year=&week=&tz=UTC` is fetched for the current and next ISO week, and matched back to the anime by `route`.
- `raw` maps to an `Original` track (`zh` for a donghua, `ja` otherwise), `sub` to `Subtitled` `en`, and `dub` to `Dubbed` `en`.
- One schedule is created per AniDB anime, air type and streaming platform (keyed `{airType}:{platform}`), covering episodes 1 through the anime's total episode count, marked finished once AnimeSchedule.net reports the anime as `Finished`.
- Delays are reported as AnimeSchedule.net gives them (`delayed-air` plus `delayedFrom`/`delayedUntil`) rather than inferred, and a `subtractedEpisodeNumber` range is submitted as one airing per episode and linked together.

## Building from Source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

This plugin is developed alongside the in-progress `IAiringScheduleService` abstractions, so `source/Shoko.Plugin.AnimeSchedule.csproj` and `tests/Shoko.Plugin.AnimeSchedule.Tests.csproj` reference `Shoko.Abstractions`/`Shoko.QueueProcessor` by project path (`../../Shoko/...`) rather than by NuGet package. Check out [ShokoServer](https://github.com/ShokoAnime/ShokoServer) as a sibling directory named `Shoko` before building, or repoint the `ProjectReference` entries at a published `Shoko.Abstractions`/`Shoko.QueueProcessor` package once one ships with the airing schedule contract.

```bash
dotnet restore
dotnet build --configuration Release
dotnet test tests/Shoko.Plugin.AnimeSchedule.Tests.csproj
```

The compiled assembly will be located at `source/bin/Release/net10.0/Shoko.Plugin.AnimeSchedule.dll`.

## License

This project is licensed under the MIT License. Airing schedule and streaming link data is provided by [AnimeSchedule.net](https://animeschedule.net/) and is not covered by this license.
