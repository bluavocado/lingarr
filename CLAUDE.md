# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Lingarr automatically translates subtitle files for a Sonarr/Radarr media library. It ships as a single
Docker image: an ASP.NET Core (.NET 10) backend that also serves the compiled Vue 3 SPA from `wwwroot`.

## Commands

```bash
# Backend build/test (solution file is .slnx, not .sln)
dotnet build Lingarr.slnx
dotnet test Lingarr.slnx
dotnet test Lingarr.slnx --filter "Category!=Integration"          # what CI runs; skips Testcontainers
dotnet test Lingarr.Server.Tests --filter "FullyQualifiedName~SsaParserTests"
dotnet test Lingarr.Server.Tests --filter "FullyQualifiedName~SsaParserTests.ParsesDialogue"

# Frontend (from Lingarr.Client/)
npm run lint          # oxlint
npm run format        # oxfmt
npm run build         # vue-tsc --noEmit && vite build
npx vue-tsc --noEmit  # type-check only; CI runs this separately

# Docs (from Lingarr.Docs/) — VitePress
npm run dev
```

Dev environment (from repo root):

```bash
docker network create lingarr      # once; the compose file expects an external network
docker compose -f docker-compose.dev.yml up -d
```

| Service | URL |
|---|---|
| Client (Vite, hot reload) | http://localhost:9876 |
| Server | http://localhost:9877 |
| Swagger / Hangfire dashboard | http://localhost:9877/swagger, /hangfire (Development only) |
| Docs | http://localhost:9879 |
| phpMyAdmin | http://localhost:9878 |

The **frontend hot-reloads; the backend container must be rebuilt** after C# changes. The dev server runs
behind Traefik with `BASE_PATH=/lingarr` so sub-path hosting stays exercised.

Enable the pre-commit hook with `git config core.hooksPath .githooks` — it runs `docker exec Lingarr.Client
npm run lint` (so the dev compose stack must be up) followed by `dotnet test Lingarr.slnx`.

## Project layout and dependency direction

```
Lingarr.Contracts  ← plugin-facing API only; keep it stable, external plugin DLLs compile against it
Lingarr.Core       ← entities, LingarrDbContext, SettingKeys, DatabaseConfiguration (no server deps)
Lingarr.Migrations ← FluentMigrator migrations shared by all three database providers
Lingarr.Server     ← controllers, services, Hangfire jobs, SignalR hubs; references the three above
Lingarr.Client     ← Vue 3 SPA, built into Lingarr.Server/wwwroot by the Dockerfile
samples/CloudflarePlugin ← reference implementation of an external translation plugin
```

`Lingarr.Core/Program.cs` is a stub; `Lingarr.Server` is the only host. NuGet versions are centrally
pinned in `Directory.Packages.props` — `PackageReference` entries carry no `Version` attribute.

## Startup pipeline

`Program.cs` is three lines; everything lives in two extension classes:

- `Extensions/ServiceCollectionExtensions.Configure` — DI registration, Swagger, cookie auth, EF Core,
  Hangfire storage (per-provider), SignalR, and **external plugin loading before the container is built**.
- `Extensions/ApplicationBuilderExtensions.Configure` — `BASE_PATH` path base, SignalR hub mapping,
  FluentMigrator run, one-off migration of plaintext API keys to encrypted storage, then SPA fallback.

The SPA fallback (`ConfigureSpa`) catches everything not under `/api` or `/signalr`, injects a
`<base href>` and a `lingarr-timezone` meta tag into `index.html`. Any new server-side route prefix must
be excluded there or the SPA will swallow it.

Two `IHostedService`s complete startup: `StartupService` (seeds settings from env vars, syncs plugin
settings rows, validates Sonarr/Radarr config) and `ScheduleInitializationService` (registers recurring
Hangfire jobs from the schedule settings).

## Settings system

Settings are rows in the `settings` table keyed by snake_case strings; **all keys are constants in
`Lingarr.Core/Configuration/SettingKeys.cs`** — never hardcode a key string. Env vars of the same name
uppercased seed the DB on startup (`StartupService.ApplySettingsFromEnvironment`), and `<VAR>_FILE`
Docker-secret variants take precedence. Documented in `Settings.MD`.

`SettingService` caches reads in `IMemoryCache` and raises `SettingChanged`. `SettingChangedListener`
(singleton) reacts by invalidating cache, pushing updates over the `SettingUpdatesHub`, and
re-registering the affected recurring Hangfire jobs. **Adding a setting that drives a schedule or a job
means adding it to the relevant group in `SettingChangedListener`**, otherwise changes only take effect
after a restart. Secret values go through `IEncryptionService` (ASP.NET Data Protection, keys persisted
to `/app/config/keys`).

## Background jobs

All work runs through Hangfire on named queues: `movies`, `shows`, `system`, `translation`, `webhook`,
`default`. Worker count is `MAX_CONCURRENT_JOBS` (default 1). Jobs live in `Lingarr.Server/Jobs` and are
annotated with `[Queue(...)]` and usually `[AutomaticRetry(Attempts = 0)]`; `JobContextFilter` exposes
the current job name to the job body.

Flow: `SyncMovieJob`/`SyncShowJob` pull media from Radarr/Sonarr → `MediaSubtitleProcessor` hashes the
subtitle state of each item and creates `TranslationRequest` rows when it changes →
`AutomatedTranslationJob` (or the API, or a Radarr/Sonarr webhook) enqueues `TranslationJob` →
`SubtitleTranslationService` drives the actual per-line translation and reports progress over
`JobProgressHub`. `ProofreadJob` runs the optional second pass. `TranslationRequestService` owns request
lifecycle (create/cancel/retry/resume) and de-duplicates against already-enqueued Hangfire jobs.

## Translation providers and plugins

Providers implement `ITranslationService` (optionally `IBatchTranslationService`, `IProofreadService`)
from `Lingarr.Contracts`. Built-ins are constructed by name in
`Services/Translation/TranslationFactory`; its default switch arm falls through to
`GetKeyedService<ITranslationService>(name)`, which is how external plugins resolve.

Each provider also has an `IPluginManifest` in `Services/Plugins/Manifests` describing its settings
fields — **the settings UI renders from the manifest, so a new provider needs no frontend code**.
`PluginLoader` scans `PLUGINS_PATH` for DLLs carrying `[assembly: LingarrPluginApiVersion]` and
`[PluginProvider("id")]`. See `Lingarr.Docs/developers/plugins.md` and `samples/CloudflarePlugin`.

Adding a built-in provider touches: the service class, a manifest, factory registration, manifest
registration in `ServiceCollectionExtensions`, `SettingKeys`, a seed migration, and — if it supports
batching — the `BatchServiceTypes` set in `SettingChangedListener`.

## Database

Three providers (`DB_CONNECTION` = `mysql` | `postgresql` | `sqlite`) share one schema.
`DatabaseConfiguration` builds connection strings from `DB_*` env vars for both EF Core and Hangfire.

- **Schema changes are FluentMigrator migrations, not EF migrations.** Add
  `Lingarr.Migrations/Migrations/M{NNNN}_{Name}.cs` with a unique sequential `[Migration(N)]` and a
  working `Down()`. They run automatically at startup. `Lingarr.Migrations.Tests` runs every migration
  up and down against real MySQL/Postgres containers (`Category=Integration`) plus SQLite.
- EF Core is used for reads/writes only; table/column names come from `EFCore.NamingConventions`
  (snake_case), so migrations must use snake_case identifiers.
- Timestamps are `DateTimeOffset` in entities and stored as UTC — `LingarrDbContext` installs value
  converters, and Postgres needs `timestamp without time zone`. Parse external timestamps through
  `Lingarr.Core.Helpers.UtcDateTime`, and use `TimeZoneConfiguration.Current` for cron schedules.

## Auth

`[LingarrAuthorize]` (a custom `IAsyncAuthorizationFilter`, not the framework attribute) gates
controllers. It short-circuits with a 403 `onboardingRequired` payload until onboarding is complete,
is a no-op when `auth_enabled` is `false`, and otherwise accepts either the `Lingarr.Auth` cookie or an
`X-Api-Key` header.

## Frontend

Vue 3 `<script setup>` + TypeScript + Pinia + Tailwind 4, `@/` aliases `src/`.

- `src/services/*` are thin axios wrappers, aggregated in `services/index.ts` and injected into stores;
  components should call stores, not axios.
- `src/store/*` are Pinia option stores with `acceptHMRUpdate`.
- `src/ts/*` holds all shared types and constants, re-exported from `src/ts/index.ts` — import from
  `@/ts`, not from the individual files.
- Live updates use `composables/useSignalR.ts` against the three hubs (`/signalr/TranslationRequests`,
  `/signalr/SettingUpdates`, `/signalr/JobProgress`); URLs must go through `utils/baseUrl.ts` so
  `BASE_PATH` deployments keep working.
- `tsconfig` is strict with `noUnusedLocals`/`noUnusedParameters`; oxlint runs `correctness` as error.

## Conventions

- Conventional Commits for branch names and commit messages (`feat/…`, `fix/…`).
- XML doc comments on public server APIs — `GenerateDocumentationFile` is on and Swagger reads them.
- **Do not add AI co-author trailers to commits** — `CONTRIBUTING.md` states PRs containing them are
  rejected.
