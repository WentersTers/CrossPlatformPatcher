#!/usr/bin/env python3
"""
Analyze runtime diagnostic logs to extract methods called in first 20 seconds.
Groups methods by category and generates a sequential test plan.
"""

import sys
import re
from datetime import datetime, timedelta
from pathlib import Path
from collections import defaultdict

def parse_diagnostic_log(log_path):
    """Parse raw diagnostic log and extract types/methods invoked in first 20 seconds."""
    methods = []
    
    try:
        with open(log_path, 'r', encoding='utf-8', errors='ignore') as f:
            lines = f.readlines()
    except Exception as e:
        print(f"Error reading log: {e}", file=sys.stderr)
        return methods
    
    # Find start timestamp
    start_time = None
    for line in lines:
        if line.startswith('# start='):
            try:
                start_str = line.split('=')[1].strip()
                # Handle nanosecond precision (7+ decimal places) by truncating to microseconds
                if 'Z' in start_str:
                    base, tz = start_str.rsplit('.', 1)
                    # Truncate to 6 decimal places for microseconds
                    frac_part = tz[:-1] if tz.endswith('Z') else tz  # Remove 'Z'
                    if len(frac_part) > 6:
                        frac_part = frac_part[:6]
                    start_str = f"{base}.{frac_part}+00:00"
                else:
                    start_str = start_str.replace('Z', '+00:00')
                
                start_time = datetime.fromisoformat(start_str)
                break
            except Exception as e:
                pass
    
    if not start_time:
        print("Could not find start time", file=sys.stderr)
        return methods
    
    # Extract type invocations with timestamps
    # Format: timestamp method|TYPE|returns=...;params=...;static=...
    for line in lines:
        if not line.startswith('2026-'):
            continue
        
        parts = line.split(' ', 1)
        if len(parts) != 2:
            continue
        
        try:
            ts_str = parts[0]
            # Handle nanosecond precision for timestamp too
            if ts_str.endswith('Z'):
                base, tz = ts_str.rsplit('.', 1)
                frac_part = tz[:-1]  # Remove 'Z'
                if len(frac_part) > 6:
                    frac_part = frac_part[:6]
                ts_str = f"{base}.{frac_part}+00:00"
            else:
                ts_str = ts_str.replace('Z', '+00:00')
            
            ts = datetime.fromisoformat(ts_str)
            content = parts[1]
            
            # Calculate seconds from start
            seconds_elapsed = (ts - start_time).total_seconds()
            
            # Extract method/type invocations
            # Format: method|Type.Or.Namespace|returns=...
            if content.startswith('method|'):
                match = re.match(r'method\|([^|]+)\|', content)
                if match:
                    type_name = match.group(1)
                    
                    methods.append({
                        'timestamp': ts,
                        'seconds': seconds_elapsed,
                        'type': type_name,
                        'method': type_name,  # Fall back to type name since method name isn't logged
                        'full': content.strip(),
                        'line': line.strip()
                    })
        except Exception as e:
            pass
    
    return methods

def categorize_methods(methods):
    """Categorize methods by type and function."""
    categories = defaultdict(list)
    
    for method in methods:
        method_name = method['method'].lower()
        type_name = method['type'].lower()
        
        # Categorize by function
        if 'event' in method_name or 'handler' in method_name:
            cat = 'event-handlers'
        elif 'audio' in method_name or 'sound' in method_name:
            cat = 'audio-methods'
        elif 'show' in method_name or 'hide' in method_name or 'visible' in method_name:
            cat = 'visibility-methods'
        elif 'paint' in method_name or 'draw' in method_name or 'render' in method_name:
            cat = 'render-methods'
        elif 'click' in method_name or 'button' in method_name:
            cat = 'button-methods'
        elif 'form' in method_name or 'window' in method_name:
            cat = 'form-methods'
        elif 'text' in method_name or 'input' in method_name:
            cat = 'input-methods'
        elif 'animation' in method_name:
            cat = 'animation-methods'
        else:
            cat = 'other-methods'
        
        categories[cat].append(method)
    
    return categories

def generate_test_plan(methods, cutoff_seconds=60):
    """Generate a sequential test plan focusing on early methods."""
    early_methods = [m for m in methods if m['seconds'] <= cutoff_seconds]
    
    categories = categorize_methods(early_methods)
    
    # Generate test plan
    test_plan = []
    test_num = 1
    
    # Sort categories by when they first appear
    sorted_cats = sorted(categories.items(), 
                        key=lambda x: min(m['seconds'] for m in x[1]))
    
    for cat_name, cat_methods in sorted_cats:
        # Get unique method names
        unique_methods = {}
        for m in cat_methods:
            key = f"{m['type']}.{m['method']}"
            if key not in unique_methods:
                unique_methods[key] = m
        
        # Create test entry for this category
        test_entry = {
            'num': test_num,
            'category': cat_name,
            'method_count': len(unique_methods),
            'first_seen': min(m['seconds'] for m in cat_methods),
            'methods': list(unique_methods.values()),
            'description': f"Test {cat_name} ({len(unique_methods)} unique methods)"
        }
        
        test_plan.append(test_entry)
        test_num += 1
    
    return test_plan, early_methods

def print_test_plan(test_plan, early_methods):
    """Print the test plan in a readable format."""
    print("\n" + "="*80)
    print("SEQUENTIAL METHOD TEST PLAN (First 60 seconds of startup)")
    print("="*80)
    print(f"\nTotal early methods (0-60s): {len(early_methods)}")
    print(f"Method categories to test: {len(test_plan)}\n")
    
    for test in test_plan:
        print(f"TEST #{test['num']}: {test['description']}")
        print(f"  First appeared: {test['first_seen']:.2f}s after startup")
        print(f"  Methods to test: {test['method_count']}")
        
        # Show first few methods as examples
        for i, method in enumerate(test['methods'][:3]):
            sig = method['method']
            # Truncate long signatures
            if len(sig) > 60:
                sig = sig[:57] + "..."
            print(f"    - {method['type']}.{sig}")
        
        if test['method_count'] > 3:
            print(f"    ... and {test['method_count'] - 3} more")
        print()
    
    print("="*80)
    print("\nTEST EXECUTION DETAILS:")
    print("  - Each test uses: hey paicom open the browser")
    print("  - Duration per test: ~5 seconds")
    print("  - Total estimated time: ~" + str(len(test_plan) * 5) + " seconds")
    print("\nENVIRONMENT VARIABLES:")
    print("  - PAICOM_SEQUENTIAL_METHOD_TEST=1")
    print("  - PAICOM_TEST_CATEGORY=<category_name>")
    print("  - PAICOM_RUNTIME_DIAGNOSTIC_RAW_PATH=<path_to_log>")
    print("\n")

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: analyze-early-methods.py <diagnostic_log_path>")
        print("\nExample:")
        log_path = Path.home() / "Downloads/CrossPlatformPatcher/PAIcom_Player_Folder/diagnostics"
        print(f"  python3 analyze-early-methods.py {log_path}/runtime-diagnostics-raw-*.log")
        sys.exit(1)
    
    log_file = Path(sys.argv[1])
    if not log_file.exists():
        print(f"Error: Log file not found: {log_file}", file=sys.stderr)
        sys.exit(1)
    
    print(f"Analyzing: {log_file}")
    methods = parse_diagnostic_log(str(log_file))
    
    if not methods:
        print("No methods found in log", file=sys.stderr)
        sys.exit(1)
    
    print(f"Total methods found: {len(methods)}")
    
    # Find methods in first 60 seconds
    early_methods = [m for m in methods if m['seconds'] <= 60]
    print(f"Methods in first 60 seconds: {len(early_methods)}")
    
    # Generate and print test plan
    test_plan, early_only = generate_test_plan(methods)
    print_test_plan(test_plan, early_only)
    
    # Save test plan as JSON for consumption by test runner
    import json
    output_path = log_file.parent / "test-plan.json"
    
    test_plan_json = []
    for test in test_plan:
        test_plan_json.append({
            'num': test['num'],
            'category': test['category'],
            'method_count': test['method_count'],
            'first_seen_seconds': test['first_seen'],
            'description': test['description']
        })
    
    try:
        with open(output_path, 'w') as f:
            json.dump(test_plan_json, f, indent=2)
        print(f"Test plan saved to: {output_path}")
    except Exception as e:
        print(f"Warning: Could not save test plan: {e}", file=sys.stderr)
