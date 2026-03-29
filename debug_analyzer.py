#!/usr/bin/env python3
import re
from datetime import datetime

log_file = "PAIcom_Player_Folder/diagnostics/runtime-diagnostics-raw-20260326-233751.log"

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

# Find start time
start_time = None
for line in lines:
    if line.startswith('# start='):
        start_str = line.split('=')[1].strip()
        base, tz = start_str.rsplit('.', 1)
        frac_part = tz[:-1] if tz.endswith('Z') else tz
        if len(frac_part) > 6:
            frac_part = frac_part[:6]
        start_str_fixed = f"{base}.{frac_part}+00:00"
        start_time = datetime.fromisoformat(start_str_fixed)
        print(f"✓ Parsed start time: {start_time}")
        break

if not start_time:
    print("✗ Start time not found")
    exit(1)

# Count 2026-prefixed lines
data_lines = [l for l in lines if l.startswith('2026-')]
print(f"✓ Found {len(data_lines)} timestamped lines")

# Count method lines
method_lines = [l for l in data_lines if 'method|' in l]
print(f"✓ Found {len(method_lines)} method lines")

# Test first method line
if method_lines:
    test_line = method_lines[0]
    parts = test_line.split(' ', 1)
    print(f"\n✓ Sample line splits into {len(parts)} parts")
    
    if len(parts) == 2 and parts[1].startswith('method|'):
        content = parts[1]
        print(f"✓ Content starts with 'method|'")
        match = re.match(r'method\|([^|]+)\|\.*([^|]+)\|', content)
        if match:
            print(f"✓ Regex matches!")
            print(f"  Type: {match.group(1)[:50]}")
            print(f"  Method: {match.group(2)[:50]}")
        else:
            print(f"✗ Regex does NOT match")
            print(f"  Trying to match: {content[:80]}")
            
            # Try to figure out why
            import sys
            print(f"\nDEBUG: First 3 pipes in content:")
            pipes = [i for i, c in enumerate(content) if c == '|']
            for i, p in enumerate(pipes[:3]):
                segment = content[p:p+20]
                print(f"  Pipe {i} at pos {p}: {repr(segment)}")
