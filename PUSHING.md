# Pushing — this repository is PUBLIC

This repository carries the cross-platform PAIcom patcher as **generic tooling only**.
Read `docs/REPO-PUBLISHING.md` for the full policy; this file is the push-time checklist.

## What belongs here

- Patcher source: `Core/`, `inputs/`, the `PAIcom.OWW/` injected twin, `models/`, `Properties/`
- Tests: `CrossPlatformPatcher.Tests/`
- Setup wizards: `SetupWizardCore/`, `SetupWizardWindows/`, `SetupWizardMacApp/`
- Generic docs: `docs/`
- Harness that describes the harness: `harness/`, `scripts/`, `tools/`, `.github/`

The `PAIcom.OWW/` and `models/` names look product-specific but are **patcher build
inputs**: `CrossPlatformPatcher.csproj` compiles `PAIcom.OWW/` and embeds the resulting
`PAIcom.OWW.dll` plus `models/hey_pie_com.quant.onnx`. Do not remove them.

## What must never be committed here

- Product installs and bundles
- Decompiled or re-derived product source
- Findings, issues, revision notes, verdicts
- Test evidence bundles (screenshots, logs, status files)
- Product file inventories and product-describing harness inputs
- The tracker workbook

That material lives in restricted stores reached through the established restricted
channel. It is never named or described in this repository.

## Pre-push checklist

1. Never run `git add -A`. Stage explicit paths only.
2. Confirm no staged path falls in the "never commit" list above.
3. Confirm the tracker and the coder-side store are not staged (they are `.gitignore`d).
4. Run `git diff --cached --stat` and read it before every push.
