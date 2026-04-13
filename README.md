# Gemini Agentic Code Review

**Repository:** [github.com/srimani75/AICodereviewer](https://github.com/srimani75/AICodereviewer)

Clone:

```powershell
git clone https://github.com/srimani75/AICodereviewer.git
cd AICodereviewer
```

If the remote was empty when you first pushed, use `git push -u origin main` (or `master`, depending on your default branch).

---

C#/.NET console app that runs an agentic review pipeline with Gemini:

1. **Planner agent** selects risky files.
2. **Reviewer agent** inspects each selected file for defects.
3. **Judge agent** deduplicates and calibrates severity.
4. Generates a markdown report.

## Flow Diagram

```mermaid
flowchart TD
    A[Repo Path] --> B[File Scanner]
    B --> C[Planner Agent]
    C --> D[Selected Files]
    D --> E[Reviewer Agent]
    E --> F[Per-File Findings JSON]
    F --> G[Judge Agent]
    G --> H[Final Findings + Summary]
    H --> I[review-report.md]
```

## Setup

- Install .NET 10 SDK

Set your Gemini API key:

```powershell
$env:GEMINI_API_KEY="your_api_key_here"
```

## Run

```powershell
dotnet run --project .\CoveReciewDotnet\GeminiAgenticCodeReview.csproj -- --repo . --output review-report.md
```

Optional flags:

- `--model gemini-1.5-pro`
- `--max-files 40`
- `--top-files 12`
- `--max-chars-per-file 16000`

## GitHub Actions

| Workflow | Purpose |
|----------|---------|
| [`.github/workflows/ci.yml`](.github/workflows/ci.yml) | **PRs:** `request-bugbot` posts `bugbot run` immediately (in parallel with CI; it does not wait on restore/lint/build/test). **All runs:** sequential `restore` → `lint` (`dotnet format --verify-no-changes`) → `build` (Release) → `unit-test` (`dotnet test --no-build`, using build artifacts). [Cursor Bugbot](https://cursor.com/docs/bugbot) needs the app + repo enabled in the [Bugbot dashboard](https://cursor.com/dashboard/bugbot). Bugbot uses `continue-on-error` and is not a required check—add required checks only for `lint`, `build`, and `unit-test`. |

If Bugbot is already set to review every PR automatically in the dashboard, you may get **duplicate** reviews—turn off automatic Bugbot for that repo or remove the `request-bugbot` job from the workflow.

## Output

- Writes `review-report.md` with prioritized findings (critical/high/medium/low).
