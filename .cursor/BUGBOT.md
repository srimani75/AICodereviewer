# Bugbot rules — AICodereviewer

## Scope

This repo is a small .NET console app (`CoveReciewDotnet/`) that calls the Gemini API to produce markdown code-review reports.

## Security

- Flag any hardcoded API keys, tokens, or secrets (including `GEMINI_API_KEY`, `cursor_`, `ghp_`, connection strings).
- Ensure secrets are read only from environment variables or secure configuration, never committed.
- Call out logging that might print prompts, responses, or file contents in production builds.

## C# / .NET

- Prefer `async`/`await` consistently; flag sync-over-async or fire-and-forget without error handling on network calls.
- Flag missing cancellation/timeouts on `HttpClient` usage where appropriate.
- Prefer `System.Text.Json` patterns that avoid leaking raw exception bodies to stdout in sensitive environments.

## Reliability

- Flag unbounded file reads or missing handling for large repos (memory / request size).
- Suggest handling for empty file lists, Gemini API errors, and malformed JSON from the model.

## What not to nitpick

- Typos in folder name `CoveReciewDotnet` unless the PR explicitly renames it.
