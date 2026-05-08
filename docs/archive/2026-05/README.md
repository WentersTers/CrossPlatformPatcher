# Archive 2026-05

This folder contains documentation that has been superseded by newer, more comprehensive guides.

## Archived Documents

### Pre-May 2026 - Build and Installer Docs

The following documents have been consolidated into more comprehensive guides:

- **QUICK_REFERENCE.md** → Partially replaced by [BUILD_SYSTEM.md](../BUILD_SYSTEM.md) and [QUICK_START.md](../QUICK_START.md)
  - Command cheat sheets remain useful; refer to main docs for full details

### Historical Testing and Optimization (Previously archived in 2026-04/)

See [archive/2026-04/](2026-04/) for earlier documentation on:
- RTDIAGS analysis and method discovery
- Keyword matching demonstrations
- Steam status fix implementations
- Test and optimization snapshots

---

## Current Documentation Structure

**Active documentation** is in [docs/](../) and organized as follows:

### Build & Deployment
- [Build System](../BUILD_SYSTEM.md) — Complete build pipeline for all platforms
- [Build Outputs](../BUILD_OUTPUTS.md) — Directory structure of build artifacts
- [Installer Guide](../INSTALLER_GUIDE.md) — macOS .pkg and .app details
- [macOS Setup](../SETUP_MAC.md) — User-facing macOS setup documentation

### Development & Testing
- [Live Testing](../LIVE-TESTING.md) — Runtime testing during game execution
- [Command Injection Guide](../COMMAND_INJECTION_GUIDE.md) — Automated test injection
- [Test Commands](../TEST_COMMANDS.md) — Command testing reference
- [Test Runner Guide](../TEST_RUNNER_GUIDE.md) — Test suite deep-dive

### Features & References
- [Keyword Matching](../KEYWORD_MATCHING.md) — Matching strategy documentation
- [Command Categories](../COMMAND_CATEGORIES.md) — Command taxonomy
- [Animation Documentation](../ANIMATION_SCRIPT_DOCUMENTATION.md) — Animation scripting
- [Animation Methods Reference](../ANIMATION_METHODS_REFERENCE.md) — Animation methods
- [Optimization Guide](../OPTIMIZATION_GUIDE.md) — Performance tuning
- [Cross-Platform Integration](../CROSS-PLATFORM-INTEGRATION.md) — OS-specific behavior

### Project Management
- [Project Structure](../PROJECT_STRUCTURE.md) — Code organization and entry points
- [README](../README.md) — Documentation index and workflow overview

---

## Why Documents Are Archived

Documents are moved to the archive when:

1. **Superseded by comprehensive guides** — A new doc provides more complete, current information
2. **Historical reference only** — The information is valuable for understanding past decisions but shouldn't be used as implementation source-of-truth
3. **Consolidated** — Multiple small docs merged into one authoritative guide

**Archival is not deletion:** These documents remain available for historical context and research.

---

## Using This Archive

1. **Don't use archived docs as primary references** — Use active docs instead
2. **Do use archived docs for context** — Understand how decisions evolved
3. **When to research**: Debug sessions, understanding feature history, edge case analysis

---

## Moving Forward

When creating new documentation:

1. **Update active docs first** — Reflect changes in the primary docs/
2. **Archive when superseded** — Move outdated docs here with rationale
3. **Update this index** — Track what was archived and why
4. **Maintain links** — Reference the active replacement docs

---

**Last Updated:** May 2026

**Next Archive:** 2026-06 (when new superseding documentation is created)
