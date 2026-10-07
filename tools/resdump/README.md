# resdump — manifest-resource table dump + regression check

Generic .NET metadata tool (no product identifiers). It exists because a
patch-pipeline defect class rewrites or drops manifest resources / linked
resource-assembly references, which surfaces at runtime as
`MissingManifestResourceException` in form constructors.

```
dotnet run --project tools/resdump -- <fileA> <fileB>          # dump + diff
dotnet run --project tools/resdump -- --check <fileA> <fileB>  # exit 0/1
```

Per entry: name, name codepoints (catches non-ASCII mangling), size, sha256,
and the Implementation target (`embedded`, `file:…`, `asmref:…`).

## Regression tests this tool covers

| # | Test | How |
|---|---|---|
| 1 | **Manifest resource name-set equality** pre/post | `--check`: every input name present in output with byte-identical codepoints; additions allowed, removals/renames/impl-changes fail |
| 2 | **Linked-resource target preservation** (companion materializable) | `--check`: `impl` of every preserved name is unchanged (`asmref:`/`file:` targets survive) |
| 3 | **End-to-end smoke** (subject constructs + renders) | not this tool — run the matrix leg; the visual gate requires the rendered subject with a nonzero frame diff |

Invocation recipes for specific products (which binaries to compare, where the
golden inputs live) travel through the restricted channel only —
`docs/REPO-PUBLISHING.md` rule 8.
