# Harness operating checklist (generic)

These rules describe the **harness**, not the product, and are public per
`docs/REPO-PUBLISHING.md`. Product-specific operating rules live in the
restricted store (internal — rule 8).

## Rig discipline (every run)

1. **Self-measure, never guess**: host resources come from `rig_probe.sh`
   (`max_concurrent`, stop-check) — never hardcoded.
2. **Snapshot, don't reinstall**: legs revert to a golden snapshot; provisioning
   runs once per base image.
3. **Stop condition**: refuse to start a leg on projected free RAM < 4 GB or
   free disk < 20 GB — report `DEFERRED — resources`, never a half-run.
4. **Thermal backoff** to 1 concurrent VM with the reason logged in the leg
   report (so a slow leg traces to throttling, not to the subject under test).
5. **Sequential legs** for GPU/compositor-asserted runs — compositor contention
   manufactures false failures.

## Evidence discipline (every leg)

6. **No terminal-log-only verdicts.** A verdict is derived from written
   artifacts in the evidence bundle (screenshots, logs, `status.json`).
7. **Dual tier**: visual (menu + animated subject, frames N seconds apart with
   the frame-diff recorded) **and** data (diagnostic logs + runtime identity).
8. **Never a silent retry or fallback**: every retry, every QGA → screendump
   fallback, every workaround is a logged line in `status.json`.
9. **DEFERRED is a first-class verdict** with a reason string, not a gap.
10. **FAIL verdicts carry**: error signature, failing gate number, and the
    captured artifacts. Expected-fail legs record the exact predicted evidence.
11. **Chain of custody**: sha256 per artifact, capture timestamps, and the
    build provenance (`git_sha`, `build_ts`, `git_dirty`) in every `status.json`.

## Lane discipline

12. Harness commits never ride along in patch-core diffs (and vice versa).
13. Defects in the subject under test are filed as ISS items for the owning
    lane — never hot-fixed from the harness lane.
14. Field tools (external diagnostic scripts) are **consume-only**: copied and
    run unmodified; changes route to their owner.
15. When a tool needs product-specific input data, ship schema/example
    publicly and load the real data from a local override (see
    `docs/REPO-PUBLISHING.md`).
16. **Environment parity before any comparison claim**: a differential must
    run both sides under identical display/runtime preconditions (ISS-028
    lesson) — record the preconditions in the evidence.
