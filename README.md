# Jellyfin Box Sets

Automatically creates and maintains Jellyfin box sets from the TMDB collections
your movies belong to. A from-scratch replacement for the unmaintained official
`jellyfin-plugin-tmdbboxsets`.

Targets **Jellyfin 10.11.x** (`targetAbi` 10.11.0.0, `net9.0`).

## How it works

The plugin needs **no TMDB API key and makes no HTTP calls of its own**. It
relies on two things the Jellyfin server already does:

1. During a normal metadata refresh, the bundled TheMovieDb provider stores each
   movie's TMDB collection ID as `MetadataProvider.TmdbCollection`.
2. Jellyfin ships a TMDB metadata provider for `BoxSet` items that fills in the
   name, overview and artwork for any box set tagged with a TMDB collection ID.

So the plugin only has to:

1. Group owned movies by their TMDB collection ID.
2. Create a box set tagged with `ProviderIds["Tmdb"] = <collectionId>`.
3. Add the movies to it.
4. Queue one metadata refresh so core fills in the real name and artwork.

Box sets **without** a TMDB provider ID are never touched, so hand-made
collections are safe.

## Configuration

Dashboard → Plugins → Box Sets.

| Setting | Default | Effect |
| --- | --- | --- |
| Enable automatic sync | on | Sync shortly after a movie is added or changes, in addition to the scheduled task. |
| Automatic sync delay (seconds) | 15 | Debounce window after the last movie change. |
| Minimum movies per collection | 2 | How many owned movies a collection needs before a box set is created. |
| Strip "Collection" suffix | off | Trims a trailing "Collection" from the placeholder name. |
| Remove orphaned box sets | off | Deletes managed box sets that fall below the minimum. |
| Excluded TMDB collection IDs | empty | Comma-separated collection IDs to ignore. |

### TMDB metadata (optional)

Only needed when your network blocks `api.themoviedb.org`. Jellyfin's built-in
TMDB provider cannot be pointed at a proxy (it constructs `TMDbClient` with no
base-URL override), so on a blocked network box sets keep the placeholder name
`TMDB Collection <id>` and get no artwork. Filling these in lets the plugin
fetch collection details itself.

| Setting | Default | Effect |
| --- | --- | --- |
| TMDB API key or v4 token | empty | Empty disables direct lookups entirely. |
| Value is a v4 token | off | Sends the key as a bearer token instead of `api_key`. |
| TMDB proxy base URL | empty | Include the trailing `/3`. Empty calls TMDB directly. |
| Proxy shared secret | empty | Optional, sent as a header. |
| Proxy secret header name | `X-Proxy-Secret` | Must match what your proxy checks. |
| Metadata language | `en-US` | Language requested from TMDB. |
| TMDB image base URL | `https://image.tmdb.org/t/p/original` | Artwork host. |

Lookups run **only when something is missing** — a placeholder name, an empty
overview, or absent artwork — so repeated syncs cost no API calls once a box set
is complete.

Note the image CDN is a *different host* from the API and is frequently still
reachable when the API is blocked, so artwork usually works as soon as the
plugin can read the image paths.


A **Sync Box Sets** scheduled task (Library category) runs daily at 03:00
and can be triggered manually from Dashboard → Scheduled Tasks.

## Building

Requires the .NET 9 SDK.

```bash
dotnet build -c Release
```

The build produces a single `Jellyfin.Plugin.TmdbBoxSets.dll`; the Jellyfin
assemblies are reference-only and are supplied by the server at runtime.

## Installing from the plugin repository (recommended)

In Jellyfin: **Dashboard -> Plugins -> Repositories -> +**, then add:

| Field | Value |
| --- | --- |
| Repository Name | `Jaqobs Plugins` |
| Repository URL | `https://raw.githubusercontent.com/Jaqobs/jellyfin_boxset_plugin/main/manifest.json` |

Then go to **Catalogue**, find *Box Sets* under the **Movies and Shows** section, and
install it. Restart Jellyfin when prompted. Updates appear in the dashboard
automatically once a new version is released.

## Installing manually

```bash
dotnet build -c Release
mkdir -p "<jellyfin-data-dir>/plugins/Box Sets_1.0.1.0"
cp Jellyfin.Plugin.TmdbBoxSets/bin/Release/net9.0/Jellyfin.Plugin.TmdbBoxSets.dll \
   "<jellyfin-data-dir>/plugins/Box Sets_1.0.1.0/"
```

Restart Jellyfin, then check Dashboard → Plugins.

## Notes

- The "strip Collection suffix" option only affects the placeholder name the
  plugin sets at creation time. Core's TMDB box set provider may restore the
  full official name on refresh.
- The suffix regex matches the English word "Collection" only. Non-English
  metadata may need additional patterns.

## Releasing

Releases are cut by pushing a tag; everything else is automated by
`.github/workflows/release.yml`.

```bash
git tag v1.0.1.0
git push origin v1.0.1.0
```

The workflow builds with the assembly version taken from the tag, zips the DLL
at the archive root, creates a GitHub release with the zip attached, then
regenerates `manifest.json` (including its MD5 checksum) and commits it back to
`main`. Jellyfin clients pick the new version up from the repository URL above.

Plugin identity lives in `build.yaml` and is the single source of truth for the
manifest; `scripts/update_manifest.py` reads it rather than duplicating values.

## License

GPL-3.0-only. See [LICENSE](LICENSE).
