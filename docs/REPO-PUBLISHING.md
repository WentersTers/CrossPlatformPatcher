# Repository publishing policy — what goes where

**This doc consolidates the public/private split.** If you are about to commit,
read this first.

## The two homes

| Repo | Visibility | Carries |
|---|---|---|
| This repo (`CrossPlatformPatcher`) | **PUBLIC** | **Generic tooling only**: patcher source + tests + docs, the test harness (`harness/`), build/CI scripts, and harness operating config that describes the harness (schemas, examples, loaders). |
| The restricted research store | **PRIVATE** | **Everything product-adjacent**: decompiled/deobfuscated material, program info in words, research + findings, test evidence bundles, ISS/REV artifacts, and product-describing harness inputs. |

Original program binaries and reverse-engineered source **never** enter the
public repo (see its README). Nothing product-adjacent flows from the private
store to the public one; the public side carries only generic tooling.

## Decision table (quick)

| Content | Destination |
|---|---|
| Patcher/harness **source code**, tests, build scripts | PUBLIC |
| Deobfuscated / decompiled source, RE'd material | PRIVATE |
| **Program info in words** (how the program behaves, file inventory, run flows) | PRIVATE |
| **Findings**: ISS drafts, REV notes, verdicts, deviations | PRIVATE |
| Test **evidence bundles** (screenshots, logs, status.json) | PRIVATE |
| Generic harness docs (rig usage, deliverables, checklists) | PUBLIC |
| Harness inputs describing the **product** (real target names, file inventories) | PRIVATE (schema/example public) |
| Original binaries, user installs, backups | Neither — stay outside version control |

## Rules

1. **When in doubt, private.** A leak of product internals to the public repo
   is not reversible in practice (forks/caches).
2. **Harness commits never ride along in patch-core diffs** and vice versa;
   keep paths and commits separate.
3. **Evidence is never committed to the public repo.** `evidence/` is
   gitignored there as a guard; publishable summaries are re-derived in words
   and land in the private store instead.
4. Finding claims cite evidence paths in the **private** store.
5. Tracker (CHANGELOG/REV/ISS workbook) stays **Operator-side** (out of both
   repos).
6. **Harness inputs describing the product are private; harness inputs
   describing the harness are public.** Prose about product behavior, file
   inventories, run flows, and identifiers = product = private. Generic rig
   operation = harness = public (`harness/matrix/CHECKLIST.md`).
7. **When a tool needs a private input, ship the schema/example publicly and
   load the real data from a local override.** The loader stays public; the
   data file is gitignored on the public side and lives in the private store
   (`REQUIRED_TREE_CONF` env or local `required-tree.conf`).
8. **Oblique-reference discipline (disclosure policy).** Shared outputs
   (tracker rows, handoff docs, report text, commit messages, summaries) speak
   about the **public surface only**. Internal research locations are
   referenced at most obliquely — no names, no subjects, no contents, and no
   description of what they handle or are responsible for. Evidence provenance
   is recorded by public commit identifiers and neutral artifact labels only;
   restricted material travels through the established restricted channel and
   is never described by name or location in shared text.

## Content audits (recorded)

- Product file-inventory input — **audit result: carries product identifiers**.
  Moved to the private store; public keeps a schema example + loader. Guard:
  `.gitignore`. Patch-target selection likewise reads from the private input
  (`target|…` line), not from public code.
- Product operating rules in words — **product/patch knowledge**. Moved to the
  private store. Generic harness lines extracted to public
  `harness/matrix/CHECKLIST.md`.
- **History rewrite (authorized)**: superseded public artifacts purged from
  retrievable branch history; pre-rewrite commit identifiers are superseded
  and retained only in historical audit rows.
