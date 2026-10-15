# Shoko AnimeSchedule.net Plugin

A [Shoko](https://shokoanime.com/) airing schedule provider backed by [AnimeSchedule.net](https://animeschedule.net/). Pulls raw (original-language), subtitled and dubbed airing times, delays and streaming links for the anime already in your library.

Data is provided by [AnimeSchedule.net](https://animeschedule.net/), as required by their [API terms of use](https://animeschedule.net/api-terms-of-use).

## Features

- **Raw, sub and dub schedules** — tracks all three release kinds per anime, each as its own schedule per streaming platform.
- **Delay-aware** — reports `delayed-air` episodes as delayed, keeping the original slot alongside the new one (or leaving the airing slotless when no new date has been announced yet).
- **Multi-episode releases** — a timetable entry covering several episodes at once (e.g. a two-episode premiere) is submitted as one airing per episode, linked together.
- **Streaming channels** — registers a channel for every platform AnimeSchedule.net lists a stream on (Crunchyroll, Netflix, Amazon, Apple TV, Bilibili TV, Disney+, HIDIVE, Hulu, OceanVeil, YouTube and any platform added since), carrying the episode's page URL. Global services are registered without a country; a regional one carries its own (AnimeSchedule.net's Hulu is Hulu in `US`).
- **Gentle on the API** — sends at most one request a second (about half of AnimeSchedule.net's limit), reads its `X-RateLimit-*` response headers and waits when the window is spent. A rate limit is retried once after its wait; a second one, or three server errors in a row, suspends the plugin's requests (reported to Shoko as a suspension) and ends the sweep.
- **Core-driven sweeps** — the provider implements `ISweepingAiringScheduleProvider`, so the server decides when a sweep is due and how long one chunk may run. A chunk fetches each of the six timetable pages (raw/sub/dub × this week/next week) once and matches them against every series, stops as soon as its deadline fires, and hands back the shoko series ID it got to as the cursor the next chunk resumes after.
- **A catalog instead of one lookup per series** — the plugin keeps the anime AnimeSchedule.net lists as ongoing and upcoming in a database of its own, and only looks up, in batches, the few anime the catalog does not cover and that could be airing.
- **Delta writes** — a timetable covers a fortnight rather than a whole run, so airings are written with `MergeAirings`: the entries the timetables still list are submitted, an episode dropped from a week they do list is named as a removal, and the weeks either side are left alone.

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
   https://raw.githubusercontent.com/revam/dotnet-shoko-plugin-animeschedule/metadata/manifest.json
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

- **Catalog.** A sweep starts by reading `GET /anime?airing-statuses=ongoing` (every pass) and `airing-statuses=upcoming` (once a day), page by page, into the plugin's database (`data/<plugin-id>/animeschedule.db3`). Each anime's links to AniDB, AniList and MyAnimeList give its IDs on those sites. Anime not seen in a list or a lookup for 60 days are dropped.
- **Matching.** A series matches the catalog by its AniDB anime ID, then by the AniList entry it is linked to, then by the MyAnimeList ID that AniList entry gives itself. AniDB's own MyAnimeList links are never used: AniDB folds many MyAnimeList entries into one anime without saying which is the main one.
- **Lookups.** A series the catalog does not cover is looked up through `GET /anime?anidb-ids=…&mt=any`, 18 anime per request, but only while it could be airing by its AniDB dates: no end date, ended within the last 4 weeks, or starting within the next 8. Finished anime are never looked up.
- **Backoff.** A miss waits 1, 2, 4, 8, 16, then 30 days before the next lookup, and 8 misses in a row give the anime up, as does an unknown anime that has ended. A known anime is looked up again weekly. When the anime's AniDB start date, end date or episode count changes, the backoff is cleared, and a given-up anime gets one fresh check. Refreshing a single series looks it up whatever its next check says, and records the answer.
- **Timetables.** `GET /timetables/{raw|sub|dub}?year=&week=&tz=UTC` is fetched for the current and next ISO week, cached for five minutes, and matched to the series by `route`.
- `raw` maps to an `Original` track (`zh` for a donghua, `ja` otherwise), `sub` to `Subtitled` `en`, and `dub` to `Dubbed` `en`.
- One schedule is created per AniDB anime, air type and streaming platform (keyed `{airType}:{platform}`), covering episodes 1 through the anime's total episode count, marked finished once AnimeSchedule.net reports the anime as `Finished`.
- A schedule whose anime the timetables no longer list for its air type and platform has its airings inside the fortnight named as removals, the same as an episode dropped from a listed week. Nothing is withdrawn for an air type whose timetables could not be fetched.
- Delays are reported as AnimeSchedule.net gives them rather than inferred, and a `subtractedEpisodeNumber` range is submitted as one airing per episode and linked together.
- `airingStatus` is a series-level state: once a series is delayed or on break, every entry it returns reads `delayed-air` and carries the same `delayedFrom`/`delayedUntil` window, historical weeks included. Only an entry slotted at or after the window's start is treated as postponed; one with no `delayedUntil` yet becomes a slotless airing.

## Building from Source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

`Shoko.Abstractions` is a package reference, and the airing schedule contract is only in its 6.0 prereleases, so the feed that carries the version in `source/Shoko.Plugin.AnimeSchedule.csproj` has to be one of your NuGet sources.

```bash
dotnet restore
dotnet build --configuration Release
dotnet test tests/Shoko.Plugin.AnimeSchedule.Tests.csproj
```

The `design` project exists only for `dotnet ef`, which needs the Entity Framework Core assemblies the plugin leaves out of its output. Add a migration from the repository root with:

```bash
dotnet ef migrations add <Name> -p source -s design -o Storage/Migrations
```

The compiled assembly will be located at `source/bin/Release/net10.0/Shoko.Plugin.AnimeSchedule.dll`.

## License

This project is licensed under the MIT License. Airing schedule and streaming link data is provided by [AnimeSchedule.net](https://animeschedule.net/) and is not covered by this license.
