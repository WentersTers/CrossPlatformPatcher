---
name: test-runner
description: Execute test suite, parse results, capture failures, and provide comprehensive test report with metrics and diagnostics.
---

# Test Runner Skill

## Purpose
Systematically run the project's test suite, parse all output, capture pass/fail/skip counts, identify failing tests with diagnostics, and provide a comprehensive report with actionable next steps.

## Output
A structured test report containing:
1. **Test Execution Summary** — Command used, test framework, configuration
2. **Test Results Overview** — Total tests, passed count, failed count, skipped count, duration
3. **Failures** — All failed tests with:
   - Test name and namespace
   - Failure reason (assertion, exception, timeout)
   - Stack trace (top 5 frames)
   - Expected vs. actual (if applicable)
4. **Slow Tests** — Tests taking >500ms (if run with timing enabled)
5. **Test Coverage** (if available) — Code coverage percentage
6. **Actionable Recommendations** — Next steps for fixing failures

---

## Skill Steps

### 1. Identify Test Framework & Configuration
Examine project structure for test setup:
- **C# / .NET**: Look for `.csproj` files in test folders, `[Test]` or `[Fact]` attributes
  - Detect test framework: xUnit, NUnit, MSTest
  - Find test project name (e.g., `CrossPlatformPatcher.Tests`)
  - Check for `appsettings.test.json`, test Data folders
  - Note any test categories or traits

### 2. Determine Test Execution Command
Based on detected test framework:

**C# / dotnet (xUnit/NUnit/MSTest):**
```bash
dotnet test <TestProject>.csproj -c Release --verbosity normal
# With detailed output:
dotnet test <TestProject>.csproj -c Release --logger "console;verbosity=detailed"
# With test filtering:
dotnet test <TestProject>.csproj -c Release --filter "FullyQualifiedName~TestClass"
```

**Node.js / Jest:**
```bash
npm test
# OR with coverage:
npm test -- --coverage
```

**Python / pytest:**
```bash
python -m pytest -v
# OR:
pytest tests/ -v --tb=short
```

**Java / Maven:**
```bash
mvn test
```

### 3. Execute Tests & Capture Output
Run the test command and capture:
- **Standard output** (stdout) — Test results, summary, assertion messages
- **Standard error** (stderr) — Errors, warnings, diagnostic logs
- **Exit code** — 0 = all passed, non-zero = failures
- **Timing** — Start time, end time, total duration

Parse output line-by-line to extract:
- Test count (total, passed, failed, skipped)
- Individual test names and results
- Failure reasons and stack traces
- Test file/line numbers (if available)

### 4. Identify & Categorize Failures
For each failed test, extract:
- **Full test name** (namespace + class + method)
- **Failure type**: AssertionFailedException, TimeoutException, NullReferenceException, etc.
- **Failure message** (first line typically has reason)
- **Stack trace frames** (focus on project code, not framework internals)
- **Expected vs. actual** (if assertion error shows comparison)

Group failures by:
- Test class
- Failure type (assertion vs. exception vs. timeout)
- Test area (unit vs. integration)

### 5. Parse Test Output (C# / dotnet example)

Sample test output:
```
Starting test execution, please wait...
A total of 1 test files matched the specified pattern.
  Passed AssemblyPatcherIntegrationTests.TestOpenWakeWordInjection [145 ms]
  Failed AssemblyPatcherIntegrationTests.TestProcessStartRewriting [234 ms]
    System.InvalidOperationException: Process.Start not found in patched IL

Test Run Summary:
  Total tests: 2
  Passed: 1
  Failed: 1
  Skipped: 0
  Duration: 1.234s
```

Extract:
- Line with "Passed" or "Failed" → test result + duration
- Exception/error lines → failure reason
- "Test Run Summary" section → counts and duration

### 6. Generate Recommendations
Based on test results:

**If all tests pass:**
- ✅ `Tests are healthy. Safe to commit.`
- Note any slow tests (>500ms) as potential optimization targets

**If tests fail:**
- 🔴 `Identify root causes:` (list failure types)
- Suggest running individual failures with verbose logging:
  ```bash
  dotnet test <TestProject> --filter "FullyQualifiedName~FailingTestName" --logger "console;verbosity=detailed"
  ```
- If exception-based failures: check logs for initialization issues or missing resources
- If assertion-based failures: review test expectations vs. current implementation
- If timeout-based failures: check for deadlocks or missing mocks

**If integration tests timeout:**
- Check if test resources (files, databases, network) are available
- Review test setup/teardown for incomplete cleanup
- Consider running unit tests separately

### 7. Provide Summary & Next Steps
Report in this order:
1. **Pass/Fail Status** (one line)
2. **Metrics** (counts, duration)
3. **Failed Test Details** (if any)
4. **Recommendations** (specific actions)
5. **Command to Investigate Further** (if needed)

---

## For CrossPlatformPatcher Project

**Primary test command:**
```bash
dotnet test CrossPlatformPatcher.Tests/CrossPlatformPatcher.Tests.csproj -c Release --verbosity normal
```

**Key test classes to monitor:**
- `AssemblyPatcherIntegrationTests` — IL rewriting tests (heavy, ~5+ seconds)
- `OpenWakeWordTests` — Wake-word detection and Vosk integration
- `ProgramCliTests` — CLI argument parsing and launcher generation

**Known considerations:**
- Integration tests may require build artifacts (PAIcom.exe reference)
- OpenWakeWord tests may skip on non-Wine platforms
- Vosk model tests may timeout if models aren't cached locally

---

## Command Reference

| Scenario | Command |
|----------|---------|
| Run all tests | `dotnet test CrossPlatformPatcher.Tests/CrossPlatformPatcher.Tests.csproj -c Release` |
| Run specific test | `dotnet test CrossPlatformPatcher.Tests -c Release --filter "FullyQualifiedName~AssemblyPatcherIntegrationTests.TestOpenWakeWordInjection"` |
| Run with verbose output | `dotnet test CrossPlatformPatcher.Tests -c Release --logger "console;verbosity=detailed"` |
| Run with XML report | `dotnet test CrossPlatformPatcher.Tests -c Release --logger "trx;LogFileName=results.trx"` |
| Run without capture (see Console.WriteLine) | `dotnet test CrossPlatformPatcher.Tests -c Release --logger "console" --no-capture` |
