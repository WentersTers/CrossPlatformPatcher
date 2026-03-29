# Sequential Method Testing - Current Implementation Analysis

## Problem Summary

The `./run-sequential-method-tests.sh` script sets a `PAICOM_TEST_CATEGORY` filter for each test run, but **the category filter is NOT actually preventing methods from other categories from being invoked**. 

### What Currently Happens:

1. Script sets `PAICOM_TEST_CATEGORY=animation-methods` (for example)
2. Code searches for and finds the **best handler from ALL assemblies** (no filtering)
3. That best handler is invoked **regardless of its category**
4. The invocation is **only logged as a test result if it matches the category filter**

### Result:

- Methods from multiple categories get invoked in a single test run
- The test logs appear to only show methods from the target category (because non-matching invocations aren't logged)
- But the actual method invocations are across all categories
- This defeats the purpose of sequential, category-based testing

## Code Flow Analysis

### Current Implementation (Broken)

**File:** `Core/OpenWakeWordHelper.cs`

1. **Handler Discovery** (e.g., `TryGetRenderHandler`, `TryGetAnimationHandler`)
   - Lines 1670-1700: Enumerates ALL methods from all assemblies
   - Picks the "best" method based on scoring (no category filter applied)
   - Returns the highest-scoring method regardless of category

2. **Method Invocation** (in `TryInvokeStringHandler`)
   - Lines 631, 645, 661: Invokes the method **unconditionally**
   - **Only logs the result** if `ShouldTestMethod(method)` returns true (line 631)
   - `ShouldTestMethod` checks if method's category matches `_testCategoryFilter` (lines 905-909)

3. **Filtering is Broken**
   - `ShouldTestMethod` only prevents **logging**, not execution
   - Lines 631, 645, 661:
     ```csharp
     // Method is ALWAYS invoked:
     method.Invoke(null, new object[] { argument });
     
     // But ONLY logged if category matches:
     if (_sequentialMethodTestMode && ShouldTestMethod(method))
     {
         RecordMethodTestAttempt(method, true, detail);
     }
     ```

## The Fix Required

The category filter needs to prevent method selection/invocation, not just logging. Options:

### Option A: Filter During Handler Discovery ✓ RECOMMENDED
Modify handler discovery methods (`TryGetRenderHandler`, `TryGetAnimationHandler`, etc.) to:
1. Check if category filter is active
2. Only consider methods that match the target category
3. Return early if no matching methods found in that category

**Advantages:**
- Clean, focused testing per category
- Only one method per test run
- Accurate test results

**Changes needed:**
- Modify `GetMethodCategory()` logic to be applied during method enumeration
- Add early return if filtered candidates are empty
- Log when category mismatch causes fallback or failure

### Option B: Add Category-Filtered Candidate Discovery Path
Create a separate `TryDispatchAcrossFilteredCandidates()` that:
1. Gets ALL candidate methods
2. Filters by category before trying any
3. Uses the filtered list for sequential testing

**Advantages:**
- Reuses existing candidate discovery code
- Clear separation of test vs. normal dispatch

**Disadvantages:**
- More code duplication

### Option C: Skip Non-Matching Methods During Invocation
Make the invocation itself respect the filter:
```csharp
if (_sequentialMethodTestMode && !ShouldTestMethod(method))
{
    detail = $"Skipping {method} - category {GetMethodCategory(method)} does not match filter {_testCategoryFilter}";
    return false;  // Skip this method entirely
}
```

**Advantages:**
- Minimal code changes

**Disadvantages:**
- Still scans all methods, just doesn't invoke matching ones
- Less efficient

## Fix Implemented ✓

**Option A has been implemented in OpenWakeWordHelper.cs**

### Changes Made:

1. **Game Command Handler Discovery (Windows Forms)**
   - Line 1564: Added category filter check in LINQ Where clause
   - Methods not matching the target category are now skipped, not selected

2. **Game Command Handler Discovery (Assemblies)**  
   - Line 1684: Added category filter check in method enumeration loop
   - Methods not matching the target category are now skipped before scoring

3. **Animation Handler Discovery (Windows Forms)**
   - Line 1799: Added category filter check in LINQ Where clause
   - Methods not matching the target category are now skipped

4. **Animation Handler Discovery (Assemblies)**
   - Line 1838: Added category filter check in method enumeration loop
   - Methods not matching the target category are now skipped before adding to candidates

5. **Improved Error Messages**
   - When category filtering is active and no handlers are found, the logs now explicitly show:
     - `[category filter active: <category>]`
   - Applied to all 4 handler discovery paths

### Result:

- ✓ Methods are now filtered BEFORE selection, not after invocation
- ✓ Only methods matching the target category are considered for invocation
- ✓ Test runs now truly test one category at a time
- ✓ Clear logging when category filtering prevents handler discovery
- ✓ Build compiles successfully with all changes

## Test Script Issues

**File:** `sequential-method-test.sh` (Line 95-108)

The test script hardcodes test categories that don't match the actual method categorization:
```bash
declare -a TEST_CATEGORIES=(
    "animation-methods:Animation handlers"      # ← Category name
    "audio-methods:Audio callbacks"
    "button-methods:Button click handlers"
    ...
)
```

But the actual method categorization in `GetMethodCategory()` (line 876) uses different logic:
- Checks for keywords: "paint", "render", "draw" → "render-methods"
- Checks for keywords: "audio", "sound" → "audio-methods"
- etc.

**Issues:**
- The script uses generic category names like "button-methods" 
- But actual methods are categorized by method name keywords
- There may not be any methods matching "button-methods" if no methods have "button" or "click" in their names
- This causes tests to run with a filter that matches zero methods

## Verification Steps

1. Run a diagnostic to see what methods are actually categorized:
   ```bash
   grep "\[methodtest\]" PAIcom_Player_Folder/diagnostics/paicom-*.log | grep -E "animation|audio|button" | sort | uniq
   ```

2. Check what categories categories actually exist:
   ```bash
   grep "\[methodtest\]" PAIcom_Player_Folder/diagnostics/paicom-*.log | grep -oE "\[.*?\]" | sort | uniq
   ```

3. Run a single sequential test and examine which methods get invoked vs. logged:
   ```bash
   PAICOM_SEQUENTIAL_METHOD_TEST=1 PAICOM_TEST_CATEGORY=animation-methods ./build-patch-and-launch.sh --test-commands --test-duration 5
   ```

## Recommendation

The `./run-sequential-method-tests.sh` script is the better testing approach, but it needs:

1. **Fix in OpenWakeWordHelper.cs:** Apply category filtering BEFORE method invocation, not after
2. **Documentation:** Clarify which categories actually exist based on method name keywords
3. **Auto-discovery:** Use the Python analysis script to dynamically generate the test category list from actual methods found during a diagnostic run

This will ensure that each test run truly exercises only one category of methods.
