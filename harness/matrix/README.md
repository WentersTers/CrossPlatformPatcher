# harness/matrix — REV-013 / ISS-017 multi-OS test matrix rig

> ## ⚠ OPERATING RULES ⚠
> **Generic harness rules**: `CHECKLIST.md` (this repo).
> **Product-specific operating rules** live in the restricted store (internal —
> see `docs/REPO-PUBLISHING.md` rule 8; read before every run).
> Both rule sets are **enforced**: `required-tree.example.conf` (schema here;
> real inventory + patch target loaded via `REQUIRED_TREE_CONF` or local
> `required-tree.conf`) + `verify_app_tree`/`verify_guest_tree` — a violating
> leg is `VOID-STAGING`, not a verdict.

Coder harness lane (REV-013). **Patch-core files are off-limits here**: every
change in this directory is a harness/laptop change and must never ride along
in patch-core diffs. Patch-core defects discovered by a run are filed as ISS
drafts (`ISS-DRAFT-01`, `ISS-DRAFT-02`, …) and routed to the patch lane — never
hot-fixed from here.

## Deliverables (REV-013 §4)

| ID | File | Role |
|---|---|---|
| D1 | `rig_probe.sh` | Host resource inventory + concurrency decision (JSON header) |
| D2 | `vm_provision.sh` | Base images per distro, guest tools, runtime layer, snapshot `golden-<leg>` |
| D3 | `run_matrix.sh` | Leg orchestrator → `evidence/ISS-017/leg-XX/status.json` |
| D4 | `capture_evidence.sh` | Dual-tier capture (visual + data), retries logged, sha256 per artifact |
| D5 | `report_gen.py` | §7 matrix table + per-leg verdict lines + bundle index |

Registry: `legs.conf` — 12 legs with **pre-registered** expectations
(`PASS` / `NO-OP` / `EXPECTED-FAIL`). A deviation is evidence, not a surprise.

## Rig topology

Console (Windows) → `ssh sage@100.78.133.38` (laptop = rig host) → libvirt/qemu
guests. Everything runs under `~/rev013/` on the laptop:

```
~/rev013/
  harness/matrix/      this directory (deployed copy)
  src/                 repo sources synced from the console (git HEAD)
  app-input/           ORIGINAL product inputs only (stale outputs excluded)
  app-bundle/          regenerated once per base: patched real-program + run.sh
  provenance.json      git_sha / build_ts / git_dirty captured on the console
  work/                logs
  venv/                python + Pillow (frame-diff)
  evidence/ISS-017/leg-XX/   the evidence bundles (rsync back for adjudication)
```

## Phase 0 smoke (L01) — exact sequence

```sh
# on the laptop
bash ~/rev013/harness/matrix/rig_probe.sh
bash ~/rev013/harness/matrix/vm_provision.sh --leg L01
bash ~/rev013/harness/matrix/run_matrix.sh --legs L01
python3 ~/rev013/harness/matrix/report_gen.py
```

## Evidence contract (dual tier, REV-013 §4/§5)

- **Visual**: `menu.png` (menu text/UI readable — Operator OCR-checks),
  `pet_t0.png` + `pet_t1.png` 2 s apart. `frame_diff == 0` means a frozen
  frame ⇒ visual tier fails. QGA guest-exec capture is primary,
  `virsh screendump` is the logged fallback — never a silent retry.
- **Data**: `tls-diag.sh` (Perry's tool, **consume-only** — copied into the
  guest untouched) Section 7 line `BCL version : <asm> file: <file>`; the
  `file:` field is compared to baseline `4.6.57.0` (wine-mono mscorlib
  FileVersion). A mismatch is `REVIEW`, never an auto-fail. Plus
  `find-pai-runtime.sh` dump, runtime identity, `PROTON_LOG` excerpt on
  Proton/GE legs, `c0000135` scan.

## Do-not-regress checklist (enforced in `run_matrix.sh` verdict)

1. `mscoree=b` effective at launch (harness wrapper sets it; `runsh_winell_overrides`
   records the shipped blank value as the defect exhibit — ISS-DRAFT-01).
2. wine-mono MSI filename is exactly `wine-mono-<ver>-x86.msi`; any `_64`
   variant is a hard FAIL.
3. GE internal Mono recorded, never installed over (Phase 1 legs).
4. `winetricks dotnet48` no-op behavior logged (L02).
5. No verdict is ever derived from terminal output alone — only from written
   artifacts in the leg bundle.

## Resource policy (REV-013 §3)

`max_concurrent = clamp(floor((free_RAM_GB − 4) / 4), 1, 3)`; sequential legs
`L03, L04, L08, L12` never run concurrently; thermal throttle ⇒ 1 concurrent
with the reason logged; stop condition (free RAM < 4 GB or disk < 20 GB) ⇒
`DEFERRED — resources`, never a half-run.
