# Pull request draft

> Head: `bluavocado/lingarr` branch `feat/translated-context` (two commits, cherry-picked from `feature/personalized` without `.doc/` and `CLAUDE.md`) → base: `lingarr-translate/lingarr` `main`
> Open it at: https://github.com/lingarr-translate/lingarr/compare/main...bluavocado:lingarr:feat/translated-context
> Copy the title and body below into the GitHub form. This file lives only on `feature/personalized`.

Commits on the PR branch (maintainers can squash on merge):

| commit | message |
| --- | --- |
| `f06ce4e` | feat: pair translated lines into contextBefore and give /content context |
| `376a81a` | feat: structure translated context as JSON lines and clean model output |

---

## Title

```
feat: translated context for AI prompts and context-aware /content translation
```

## Body

### Summary

Single-line AI translation can now show the model how the *previous* lines were translated, and the `/api/translate/content` endpoint (used by Bazarr) gains the same context support as file translation. A new opt-in setting, **Use translated lines as context before**, switches `{contextBefore}` / `{contextAfter}` from plain source lines to one JSON object per line carrying `position`, `source` and, once a line is translated, `translation`. Model output is cleaned of prompt labels before it is stored, and new installations get a sectioned default user prompt. Everything is off by default and existing settings are left untouched.

### Motivation

- With per-line translation the model never sees its own earlier output, so names, honorifics and recurring terms drift from line to line. Sending the previous lines *with their translations* is the most direct way to keep them consistent.
- `/api/translate/content` had its own hand-written translation loop that never built context, never reported progress, and did not share the de-duplication, logging or cancellation handling of `TranslateSubtitles`.
- In testing with a local Qwen model the model sometimes copied the `[TRANSLATION]` label into its answer; the labelled text was stored as the translation and then fed back into the context of every following line. The same risk exists for proofreading, whose default prompt uses `[SOURCE]` / `[TRANSLATION]` labels.

### What changed

**Backend**

- `SubtitleTranslationService`
  - New constructor option `useTranslatedContext` (kept off the `TranslateSubtitles` signature, so all call sites are unchanged).
  - `BuildContext` renders each context line as a `ContextLine` JSON object when the option is on (`{"position":12,"source":"Hold it!","translation":"Wacht even!"}`; "after" lines and untranslated lines carry no `translation`). Non-ASCII text is not `\u`-escaped, embedded line breaks are escaped, so one entry is always one physical line. With the option off, context is plain source text exactly as before.
  - New `CleanTranslationOutput`: strips leading `[TRANSLATION]` / `[SOURCE]` / `[Target-to-Translate]` labels (with optional `#12` numbering, repeated labels, trailing colon) and unwraps a JSON object with a `translation` or `line` field. Applied in `TranslateSubtitleLine`, so `/file`, `/content`, `/line` and every provider, including plugins, are covered. SDH markers such as `[MUSIC]` are untouched. When nothing usable is left, the source line is kept and a warning is logged, mirroring the batch path.
- `TranslationRequestService.TranslateContentAsync`: the single-line branch now calls `TranslateSubtitles` instead of its own loop. Context settings are read, progress is throttled to percentage changes (per-line `EmitLine` is unchanged), `StartTime`/`EndTime` are derived from `Position` so the stacked-`.ass` de-duplication cache cannot merge repeated lines across a request, and `ThrowIfCancellationRequested()` after the loop preserves the previous cancellation semantics (no partial results reach statistics).
- `TranslationJob` reads the new setting and passes it through.
- Proofreading: `ProofreadJob` and the single-line proofread endpoint run the proofread answer through the same cleanup before comparing and storing it.
- New model `Lingarr.Server/Models/ContextLine.cs`.

**Settings and migrations**

- `M0021_SeedAiContextUseTranslated`: seeds `ai_context_use_translated = false`. Inserting a row only, no schema change.
- `M0014` / `M0002` seed values updated for **new installations only**: the default `ai_user_prompt` is now a sectioned layout (context before, context after, target line, then the instructions), and `ai_context_before` / `ai_context_after` default to `0`. Existing rows are never modified, upgraded installations keep their prompt and counts.

**Frontend**

- `TranslationPrompt.vue`: toggle **Use translated lines as context before** with help text describing the JSON format; shown only in single-line mode, like the other context settings. `setting.ts` constants/types.

**Docs**

- `Lingarr.Docs/translation-services/ai-services.md`: new default user prompt, placeholder table, cleanup behaviour.

### Default user prompt for new installations

```
[Context-Before]
{contextBefore}

[Context-After]
{contextAfter}

[Target-to-Translate]
{lineToTranslate}

Translate only the text under [Target-to-Translate]. The lines under [Context-Before] and [Context-After] are neighbouring subtitle lines for context only: use them to keep names, terms and tone consistent, and never translate or repeat them. When a [Context-Before] entry includes a "translation" field, follow that earlier translation. Reply with the translated target text alone, as plain text, without labels, JSON or position numbers.
```

The instructions live in the user prompt rather than the system prompt on purpose: the system prompt is also sent in batch mode, where these sections do not exist.

### Compatibility

- Default off. With the option off, `{contextBefore}` / `{contextAfter}` render exactly as before.
- No change to `ITranslationService` in `Lingarr.Contracts`; the plugin API version is unchanged. When the option is on, plugins receive the JSON lines through the existing `contextLinesBefore` / `contextLinesAfter` parameters.
- Batch translation is unaffected (it bypasses the user prompt and carries no context).
- `/api/translate/content` behaviour changes only in single-line mode: context is now sent when configured, `Progress: N%` is logged, the percentage broadcast is throttled (≤ 101 messages per request instead of one per line), and a whitespace-only line is returned as-is instead of as an empty string.
- `/api/translate/line` and per-line translation now return the source text (with a warning) when the model returns nothing usable, instead of an empty line.
- All model outputs are trimmed; previously only some providers did this.

### Testing

- `dotnet test --filter "Category!=Integration"`: 247 tests pass, including new coverage for the JSON context format (before/after sides, resumed lines, multi-line entries, embedded line breaks, non-ASCII), the `/content` de-duplication regression, label-echo cleanup end to end, and the empty-output fallback.
- `Lingarr.Migrations.Tests`: migrations up, down to 7 and re-applied on SQLite; up on MySQL and PostgreSQL via Testcontainers.
- `oxlint` and `vue-tsc --noEmit` clean.
- Docker image built from the branch; a fresh SQLite install gets the new seed values, an existing SQLite database keeps its values.
- Manual: LocalAI with Qwen3.6-27B against real subtitles through `/api/translate/content`. This is where the label echo and the multi-line `/content` entries were found; the JSON format and the cleanup were verified against it.

### Notes for reviewers

- Why a setting instead of a new placeholder: a translated variant of `{contextBefore}` is the same slot with a different rendering and shares the same line count, so a second placeholder would only invite prompts that contain both. `language_code_format` already switches the rendering of `{sourceLanguage}` the same way.
- Why JSON lines: they match the `{position, line}` shape batch mode already uses, escape line breaks, and give every entry a position without introducing labels the model might echo.
- `CleanTranslationOutput` is `public static` so it can be unit-tested and reused by the proofread paths.

### Checklist

- [x] Conventional Commits
- [x] `npm run lint` / `vue-tsc` clean
- [x] `dotnet test` green
- [x] Documentation updated
- [x] No AI co-author tags
