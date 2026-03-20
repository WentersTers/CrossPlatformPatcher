---
name: doc-updater
description: Update project documentation, READMEs, and .gitignore files to reflect changes, build steps, test procedures, and ensure source code files are properly excluded from version control.
---

# Doc Updater Skill

## Purpose

This skill guides agents on how to maintain accurate project documentation and version control configuration after making changes to the project. It ensures that:

1. **Documentation stays current** with build, test, setup, and deployment procedures
2. **.gitignore properly excludes** any files containing source code extracted from `PAIcom.exe`
3. **Setup guides and READMEs** reflect any new dependencies, build commands, or platform-specific instructions
4. **Wiki and inline documentation** matches actual implementation

## When to Use This Skill

- After modifying build scripts, dependencies, or build procedures
- After updating test suite or adding new tests
- After changing setup, installation, or deployment steps
- When adding new files that might violate the source-code-free scope
- After changes to launcher generation, runtime patching, or platform-specific code
- When new reverse-engineering artifacts, compiled outputs, or temporary files are created

## Key Principles

The CrossPlatformPatcher repository must remain **free of source code extracted or decompiled from `PAIcom.exe`**. This is a critical constraint that affects documentation and .gitignore management:

- ✅ Track: patcher code, tests, docs, build scripts, support files
- ❌ Track: `PAIcom.exe`, patched outputs, decompiled game source
- ❌ Track: any files containing licensed source code from the original game

## Workflow

### 1. Identify Documentation to Update

After any project change, check if the following files need updates:

- **README.md** — High-level project description, setup, and build commands
- **SETUP_MAC.md** / **SETUP_LINUX.md** — Platform-specific setup instructions
- **build-and-test.sh** comments and behavior
- **PAIcom_Player_Folder/SETUP_MAC.md** and **SETUP_LINUX.md** — End-user setup guides
- Inline code documentation (comments in `.cs` files)
- Any architecture, algorithm, or integration notes

### 2. Update .gitignore for New Source Code Files

**This is critical.** If the change introduces or discovers any files that contain:
- Source code from `PAIcom.exe`
- Decompiled or reverse-engineered game internals
- Licensed or proprietary game assets

Then **immediately add them to .gitignore** before they can be committed:

```bash
# In .gitignore, add patterns like:
# Source code extracted from PAIcom
PAIcom_source/
/decompiled-binaries/
*.decompiled.cs

# Reverse-engineering temporary artifacts
reverse-engineering-temp/
disassembly-output/
```

**Important:** Use this whenever:
- New folders are created for local analysis (e.g., `reverse-engineering-artifacts/`)
- Generated decompiled code appears in the working directory (e.g., `ilspy-output/`)
- Patches are tested against the original `PAIcom.exe` and create intermediate outputs
- Analysis scripts generate dumps or intermediate files containing game code

### 3. Update Build and Test Documentation

When build commands, test procedures, or tool upgrades occur:

1. **Update README.md** with the current commands:
   ```bash
   dotnet build CrossPlatformPatcher.csproj -c Release
   dotnet test CrossPlatformPatcher.Tests/CrossPlatformPatcher.Tests.csproj -c Release
   ./build-and-test.sh
   ```

2. **Document new dependencies** in README.md and platform-specific setup guides (version, required tools, platform availability)

3. **Update comments** in build scripts if flags, output paths, or validation steps change

4. **Update launch and publish instructions** if binaries are generated to new locations or with different naming

### 4. Update Platform-Specific Guides

For each supported platform (macOS, Linux, Windows), ensure setup guides include:

- Required .NET SDK version
- Platform-specific dependencies (Vosk models, ONNX runtime, WineBottler on macOS, etc.)
- Step-by-step build/launch commands
- Troubleshooting for common issues
- Any manual post-build steps

### 5. Validate Changes

Before finalizing documentation updates:

1. **Run the documented commands** to verify they still work:
   ```bash
   dotnet build CrossPlatformPatcher.csproj -c Release
   dotnet test CrossPlatformPatcher.Tests/ -c Release
   ./build-and-test.sh
   ```

2. **Check .gitignore patterns** are correct and don't exclude files that should be tracked:
   ```bash
   git check-ignore -v <file_pattern>
   ```

3. **Review generated artifacts** and ensure they are not mistakenly tracked:
   ```bash
   git status
   ```

4. **Verify no source code files** appear in staged or untracked files that should be in .gitignore

## Examples

### Example 1: Adding a New Build Step

**Change:** You add a new pre-build step to download Vosk models.

**Updates needed:**
1. Update `README.md` → Add new command to "Building" section
2. Update `prepare-vosk-models.sh` comments → Reflect the new step
3. Update `build-and-test.sh` → Ensure the new step runs automatically
4. Update platform-specific guides → Document any platform-specific Vosk setup quirks
5. Check .gitignore → Ensure `VoskModels/zips/` is excluded (it already is)

### Example 2: Adding Reverse-Engineering Analysis

**Change:** You create a `reverse-engineering/` folder with disassembly analysis scripts and IL output.

**Updates needed:**
1. **Add to .gitignore immediately:**
   ```
   /reverse-engineering/
   ```
2. Update README.md → Note that source code from PAIcom is never tracked
3. Add comments to your analysis scripts explaining they're for _local_ use only
4. Document your findings in separate `.md` files (analysis notes) that don't contain actual code

### Example 3: Updating Launcher Generation

**Change:** The launcher generation logic now outputs binaries with a new naming convention or to a different directory.

**Updates needed:**
1. Update `README.md` → Note the new output location
2. Update `publish-all.sh` and `publish-all.bat` → Reflect new paths
3. Update `PAIcom_Player_Folder/launch.command` and `run.sh/run.bat` → Point to new binary locations
4. If any temp files are created and should be excluded, add to .gitignore

## Files to Check After Any Change

```
ROOT:
  - README.md
  - .gitignore
  - publish-all.sh / publish-all.bat
  - build-and-test.sh

PAIcom_Player_Folder/:
  - SETUP_MAC.md
  - SETUP_LINUX.md
  - launch.command
  - run.sh / run.bat
  - setup-wizard.sh

Core/ and CrossPlatformPatcher.Tests/:
  - Inline code comments and class-level documentation
```

## Summary Checklist

Use this checklist when updating documentation:

- [ ] All build commands documented are tested and working
- [ ] Platform-specific guides (if changed) are accurate and complete
- [ ] .gitignore contains entries for any new source code or reverse-engineering artifacts
- [ ] No source code from `PAIcom.exe` appears in tracked files
- [ ] Version numbers, dependencies, and tool requirements are up-to-date
- [ ] Launch/publish instructions match actual output paths and naming
- [ ] Inline code documentation reflects current implementation
- [ ] git status shows only expected changes