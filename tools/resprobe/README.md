# resprobe — dual-mount resource-resolution probe (acceptance check (f))

Verifies that every expected form `.resources` name resolves through a mounted
directory: each file must open as a valid resource set with at least one
entry. No product identifiers live in this tool; the expected name list is
supplied per run through the restricted channel.

```
dotnet run --project tools/resprobe -- <mount-dir> <names.txt>   # exit 0 iff ALL resolve
```

Gate semantics: all listed resources resolve ⇒ stage-3 mount accepted;
anything less ⇒ `REVIEW` stop before publish. The smoke test (#3) remains the
sole fix arbiter; this probe proves the mount, not the product.
