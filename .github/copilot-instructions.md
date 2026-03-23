# Project Guidelines

## Scope
- Keep the repository free of source code extracted or decompiled from `PAIcom.exe`.
- Only add patcher code, tests, docs, build scripts, and support files that are necessary to build or verify the patcher.
- Treat `PAIcom.exe`, patched game outputs, and any local reverse-engineering artifacts as inputs or temporary outputs, not tracked project sources.

## Code Style
- Preserve the existing C# style and file organization.
- Prefer small, targeted edits over broad refactors.
- Avoid copying implementation details from the original game binary; write compatibility code only where the patcher needs it.

## Build And Test
- Build the patcher with `dotnet build CrossPlatformPatcher.csproj -c Release`.
- Test the patcher with `dotnet test CrossPlatformPatcher.Tests/CrossPlatformPatcher.Tests.csproj -c Release`.
- run the ./build-and-test.sh script to validate the full build and test workflow.
- If launcher generation or runtime patching changes, validate the generated output on the relevant operating systems when possible.

## Documentation
- Update `README.md` and the setup docs when build, test, or rebuild steps change.
- Keep any instructions for rebuilding or republishing the project current and easy to follow.

## Version Control
- Update `.gitignore` for new generated artifacts or local reverse-engineering folders before they can be committed.
- Do not introduce files that contain PAIcom source material into version control.