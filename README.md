# Chronicle.Plugin.TheTVDB

[![Latest Release](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.TheTVDB?style=flat-square&label=release&color=3C8DBE)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.TheTVDB/releases/latest)

TV metadata plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle) powered by [TheTVDB](https://thetvdb.com/).

Fetches series, season, and episode metadata — titles, overviews, air dates, cast, directors, ratings, artwork, and network information — from the community standard database used by Sonarr, Plex, Kodi, Trakt, and SIMKL.

---

## How It Works

TVDB IDs are already stored in Chronicle by Trakt and SIMKL sync. This plugin reads those cross-reference IDs to fetch metadata without needing a title search, and falls back to a text search for items that haven't been synced from Trakt or SIMKL.

| Level | Strategy |
|-------|----------|
| Show | Check existing TVDB ID (from Trakt/SIMKL sync), else text search |
| Season | Derived from parent show's TVDB ID + season number |
| Episode | Derived from parent show's TVDB ID + season/episode number; title-match fallback if numbering differs |

**TVDB and TMDB episode numbering can differ** — particularly for anime and shows where TVDB and TMDB split seasons differently. When the season/episode numbers don't match, this plugin falls back to title matching and logs the discrepancy in the enrichment drill-down so you can investigate.

---

## Supported Media Types

| Media Type | Level | Fields |
|-----------|-------|--------|
| `tv` (Show) | 0 | title, overview, year, poster, backdrop, banner, genres, cast, directors, rating |
| `tv` (Season) | 1 | title, overview, year, poster, banner |
| `tv` (Episode) | 2 | title, overview, year, runtime, rating, directors, cast |
| `anime` (Show) | 0 | title, overview, year, poster, backdrop, banner, genres, cast, directors, rating |
| `anime` (Season) | 1 | title, overview, year, poster, banner |
| `anime` (Episode) | 2 | title, overview, year, runtime, rating, directors, cast |

Additional data stored in `metadata_json` (under `chronicle.plugin.thetvdb`): network, status, original country, IMDb ID, Zap2It ID.

---

## External ID Format

This plugin stores IDs in the following formats:

| Format | Example | Notes |
|--------|---------|-------|
| `series:{tvdbId}` | `series:76290` | TV show |
| `series:{tvdbId}/season:{n}` | `series:76290/season:2` | Season |
| `episode:{tvdbEpisodeId}` | `episode:308662` | Individual episode |

**Fix Match:** enter any of the above formats, a raw TVDB series ID (`76290`), or a TheTVDB URL:
- `https://thetvdb.com/series/breaking-bad` — slug resolved automatically
- `https://thetvdb.com/series/76290`
- `episode:308662` — for a specific episode

---

## Installation

1. Build the plugin:
   ```powershell
   dotnet build -c Release
   ```

2. Copy the output files into your Chronicle plugins directory:
   ```powershell
   $pluginDir = "..\Chronicle\src\Chronicle.API\plugins\chronicle.plugin.thetvdb"
   New-Item -ItemType Directory -Force $pluginDir
   Copy-Item "bin\Release\net9.0\*.dll" $pluginDir
   Copy-Item "manifest.json"            $pluginDir
   ```

3. Go to Chronicle → Plugins → TheTVDB → Settings and enter your API key.

4. Run **Fetch Missing TV Metadata** from Settings → Background Tasks to enrich any items that already have a TVDB ID stored from Trakt or SIMKL sync.

---

## Configuration

| Setting | Required | Default | Description |
|---------|----------|---------|-------------|
| `api_key` | ✓ | — | TheTVDB API key. Free at https://thetvdb.com/dashboard |
| `language` | | `eng` | ISO 639-2 three-letter language code (e.g. `eng`, `fra`, `deu`, `jpn`). Translations in this language are preferred. |
| `fallback_language` | | `eng` | Language used when no translation exists in the preferred language. |

> **Note:** TVDB uses three-letter ISO 639-2 codes, unlike TMDB and Fanart.tv which use two-letter ISO 639-1 codes. Use `eng` not `en`.

---

## Dependencies

TheTVDB provides complete standalone metadata — it does not require other plugins to run first. However, it works best alongside:

- **Trakt or SIMKL** — sync stores TVDB series IDs so enrichment can skip the text search and go straight to the correct series
- **TMDB** — covers movies (TVDB is TV-only) and provides an alternative source for fields where you want TMDB to take priority
- **Fanart.tv** — uses the TVDB series ID stored by this plugin (or by Trakt/SIMKL) to fetch high-quality artwork

Recommended enrichment order:
1. Trakt/SIMKL — Import / Delta Sync (stores TVDB IDs)
2. **TheTVDB — Fetch Missing TV Metadata**
3. TMDB — Fetch Missing Metadata (cross-checks, fills gaps)
4. Fanart.tv — Fetch Missing Artwork (now has TVDB IDs to look up)

---

## Development

Both repositories must be cloned as siblings:

```
<base>\
  Chronicle\
  Chronicle.Plugin.TheTVDB\
```

The plugin references `Chronicle.Plugins` via a local project reference marked `Private="false"` so the host's copy is used at runtime rather than a copy in the plugin output directory.

```powershell
$pluginDir = "..\Chronicle\src\Chronicle.API\plugins\chronicle.plugin.thetvdb"
New-Item -ItemType Directory -Force $pluginDir
dotnet build -c Release
Copy-Item "bin\Release\net9.0\*.dll" $pluginDir
Copy-Item "manifest.json"            $pluginDir
```
