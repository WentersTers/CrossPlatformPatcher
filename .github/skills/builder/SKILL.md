---
name: builder
description: Analyze project structure, identify build system, execute build, capture errors/warnings, and report results with logs and metadata.
---

# Build Analysis & Execution Skill

## Purpose
Systematically inspect a project's build configuration, execute the build, capture all output, identify errors/warnings/important details, and provide a comprehensive report with logs and metadata.

## Output
A structured report containing:
1. **Build Configuration** — Project type, build system, framework/runtime, configuration mode
2. **Build Command Used** — Exact command executed (with flags)
3. **Build Output Summary** — Pass/fail status, duration, artifact locations
4. **Errors** — All compilation errors, if any (with line numbers and context)
5. **Warnings** — Significant warnings grouped by category
6. **Important Details** — Build-time notes, deprecations, version conflicts, embedding details
7. **Full Build Log** — Complete stdout/stderr for reference
8. **Recommendations** — Next steps if failures occurred or warnings are critical

---

## Skill Steps

### 1. Detect Project Type & Build System
Examine root directory for build configuration files:
- **C# / .NET**: Look for `.csproj`, `.sln`, `project.json`, MSBuild files
  - Extract `TargetFramework`, `OutputType`, `RuntimeIdentifiers`
  - Note any multi-project solutions (`.sln` with multiple `.csproj` files)
- **Node.js/npm**: Check `package.json`, `webpack.config.js`, `tsconfig.json`
- **Python**: Look for `setup.py`, `pyproject.toml`, `requirements.txt`
- **Java**: Check `pom.xml`, `build.gradle`, `build.gradle.kts`
- **Go**: Look for `go.mod`, `Makefile`
- **Rust**: Check `Cargo.toml`
- **Other**: Check for Makefile, build scripts, or CI configuration

### 2. Identify Primary Build Target
For the detected language/system:
- **C#/dotnet**: Find main `.csproj` in root or identify `.sln` entry point
- **npm/yarn**: Note `build` script in `package.json`
- **Python**: Identify setup.py or build tool (setuptools, poetry, etc.)
- **Java**: Identify Maven or Gradle as primary build tool
- Note any build scripts (shell scripts, batch files, Makefile)

### 3. Determine Build Configuration
Detect available build modes:
- **C#/dotnet**: Usually `Debug` or `Release` (check `.csproj` for `<Configuration>`)
- **Node.js**: Often `development`, `production` or no explicit mode
- **Python**: Usually no explicit mode, but check for tox configs
- Note if building for specific platform/architecture (e.g., `--self-contained`, `-r osx-arm64`)

### 4. Construct & Execute Build Command
Based on detected system:

**C# / .NET (most common):**
```bash
cd <project-root>
dotnet build <ProjectFile>.csproj -c Release
# OR if multi-project solution:
dotnet build <Solution>.sln -c Release
```

**Node.js/npm:**
```bash
npm install
npm run build
# OR for yarn:
yarn install
yarn build
```

**Python:**
```bash
python -m pip install -e .
# OR
python setup.py build
```

**Java/Maven:**
```bash
mvn clean install
```

**Java/Gradle:**
```bash
./gradlew build
```

**Make:**
```bash
make build
```

### 5. Capture All Output
Execute the build command and capture:
- **Standard output** (stdout) — Build progress, artifact paths, version info
- **Standard error** (stderr) — Warnings, errors, diagnostic messages
- **Exit code** — 0 = success, non-zero = failure
- **Duration** — Start and end timestamps
- **Environment** — OS, runtime versions, paths

### 6. Parse Errors & Warnings
Extract and organize:
- **Compilation Errors**: Group by file, function, or error category
  - Include line numbers, error codes (e.g., CS0103, NU1605)
  - Note blocking vs. non-blocking
- **Warnings**: List by category (deprecation, style, performance, compatibility)
  - Highlight any suppressed warnings
  - Note if warnings-as-errors flag is set
- **Build-Specific Details**: 
  - Embedded resources and their sources
  - Downloaded packages or dependencies resolved
  - Code generation steps
  - Runtime identifier selection
  - Custom MSBuild targets running

### 7. Assess Build Status
Determine outcome:
- ✅ **Success**: Build completed without errors
- ⚠️ **Success with Warnings**: Build passed but warnings exist (may indicate future issues)
- ❌ **Failed**: Build did not complete, has errors preventing artifact generation
- 🔍 **Partial Success**: Some projects built, others failed (in multi-project scenarios)

### 8. Generate Report

Format findings as:

```
## Build Analysis Report

**Project**: [Name from .csproj / package.json]
**Type**: C# / Node.js / Python / etc.
**Framework**: .NET 8.0 / Node 18.x / etc.
**Build Mode**: Release / Debug / Production
**Date**: [ISO timestamp]

### Build Command
\`\`\`bash
[exact command executed]
\`\`\`

### Result
- **Status**: ✅ Success / ⚠️ Success with Warnings / ❌ Failed
- **Duration**: X.XXs
- **Exit Code**: 0 / [code]
- **Artifacts**: [list output paths]

### Errors
[If any - list each error with context]

### Warnings
[If any - group by category and list]

### Important Details
- Embedded resources: [list if any]
- Dependency resolution: [notes on package versions, conflicts]
- Platform-specific: [runtime identifiers, OS-specific paths]
- Custom targets: [any special MSBuild/build hooks]

### Full Build Log
\`\`\`
[Complete stdout + stderr]
\`\`\`

### Recommendations
[Next steps: fix errors, address critical warnings, test artifacts, etc.]
```

---

## Example Invocation

**User:** "Run the build and show me what happened — any errors, warnings, important details"

**Assistant:** 
1. Detects project type by examining root directory (e.g., .csproj, package.json, Cargo.toml, pom.xml, etc.)
2. Identifies the primary build command for that system
3. Executes the build with appropriate flags for Release/optimized mode
4. Captures all output (stdout, stderr, exit code, duration)
5. Parses for errors and warnings, extracts build-specific details
6. Reports comprehensive status: pass/fail, artifacts generated, any warnings or errors
7. Provides full log for reference and next-step recommendations

---

## Tips

- **For multi-project solutions**: Build main `.sln` first, then specific projects if needed
- **Capture full output**: Don't suppress logs with `2>&1 | tail -n`; keep complete output for analysis
- **Check exit code**: Even "successful" runs may have non-zero exit codes in some build systems
- **Note environment**: Runtime versions (dotnet --version, node --version) help diagnose compatibility issues
- **Dependencies matter**: If first build fails, run cleanup step (`dotnet clean`, `npm ci`, `mvn clean`) and retry
- **Warnings-as-errors**: Check if any flags like `/WarnAsError` or `-Werror` are set in project files