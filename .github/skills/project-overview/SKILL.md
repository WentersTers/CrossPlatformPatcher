---
name: project-overview
description: Create a comprehensive project structure overview: repo tree, folder usage, core components, and build/test commands.
---

<!-- Tip: Use /create-skill in chat to generate content with agent assistance -->

# Project Overview Skill

## Purpose
Generate a quick, comprehensive overview of a codebase suitable for onboarding, documentation, or understanding repo structure at a glance.

## Output
A markdown document with:
1. **Project Purpose** — Brief description of what the project does, key features
2. **Repository Tree** — Visual folder structure (3–4 levels deep)
3. **Directory Breakdown** — Each major folder's responsibility & core files
4. **Core Components** — Key classes/modules & their roles in the workflow
5. **Build & Test Commands** — Essential dev commands (build, test, publish, run)
6. **Dependencies** — NuGet/package manager references
7. **Common Workflows** — Debugging, extending, troubleshooting examples
8. **Version & Metadata** — Framework, runtimes, version number
## Skill Steps

### 1. Gather Context
**Use `Explore` subagent** (quick thoroughness):
- High-level project description: What does it do? Key tech stack?
- Top-level folder names and purposes
- Project type: library, app, patcher, framework, etc.
- Main entry point file (Program.cs, main.go, src/main.ts, etc.)

### 2. Map Repository Structure
- List root-level files and folders
- For each major folder (src, lib, core, tests, etc.):
  - List direct children (2–3 levels)
  - Note key files and their purpose
  - Identify build/output folders to exclude
- Generate a clean tree view (collapse obj/, bin/, node_modules/)

### 3. Identify Core Components
- Find main classes/modules in the primary language
- Understand their roles: entry point, core logic, utilities, tests
- Note dependencies between them
- If multiple projects in one repo: list each project file and its role

### 4. Document Build & Test
Search for and document:
- **Build commands**: dotnet build, npm run build, make, gradle, etc.
- **Test commands**: dotnet test, pytest, jest, etc.
- **Publish/Release commands**: dotnet publish, cargo build --release, etc.
- **Dev scripts**: setup.sh, install.sh, dev-server scripts
- Check for **Makefile**, **package.json**, **.github/workflows/**, **build.gradle**, **.csproj files**

### 5. Extract Dependencies
- List main NuGet/npm/pip/Maven packages from manifest files
- Note each dependency's main purpose (testing, serialization, networking, etc.)
- Highlight critical/unique dependencies

### 6. Discover Common Workflows
- Check README.md for quickstart & troubleshooting
- Search for test/debug examples in code
- Identify extension/customization patterns
- Note any special flags or options

### 7. Compile Into Markdown
Organize findings into the **Output** structure above. Use:
- Code blocks for commands
- Tree structure for folders
- Bullet lists for responsibilities
- Subsections for clarity

---

## Example Invocation

**User:** "Give me a quick overview of this project"

**Assistant:** Runs this skill via `Explore` subagent to gather and summarize repo structure, then presents a formatted overview markdown.

---

## Tips

- **For large repos**: Focus on top-level structure first; expand detail only for core directories
- **For multi-language projects**: Group by language or functionality, not just folder hierarchy
- **For monorepos**: Treat each project folder as a separate "core component"
- **Exclude noise**: Skip `obj/`, `bin/`, `node_modules/`, `.git/`, `dist/`, `build/` unless discussing cleanup
- **Prioritize build commands**: These are often the most helpful for developers

