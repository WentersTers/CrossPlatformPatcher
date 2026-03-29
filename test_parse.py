#!/usr/bin/env python3
import re
from datetime import datetime

log_file = "/Users/sheryluglis/Downloads/CrossPlatformPatcher/PAIcom_Player_Folder/diagnostics/runtime-diagnostics-raw-20260326-233751.log"

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

# Find start time
start_time = None
for line in lines[:100]:
    if line.startswith('# start='):
        print(f"Found header: {line.strip()}")
        start_str = line.split('=')[1].strip()
        base, tz = start_str.rsplit('.', 1)
        frac_part = tz[:-1] if tz.endswith('Z') else tz
        if len(frac_part) > 6:
            frac_part = frac_part[:6]
        start_str_fixed = f"{base}.{frac_part}+00:00"
        print(f"After fix: {start_str_fixed}")
        start_time = datetime.fromisoformat(start_str_fixed)
        print(f"Parsed: {start_time}")
        break

# Look for method lines
print("\nScanning for method entries...")
method_count = 0
for i, line in enumerate(lines):
    if 'method|' in line[:50]:
        if method_count < 3:
            print(f"Line {i}: {line[:100]}")
            
            # Try to parse it
            parts = line.split(' ', 1)
            print(f"  Split result: {len(parts)} parts")
            if len(parts) == 2:
                print(f"  Timestamp: {parts[0]}")
                print(f"  Content starts with: {parts[1][:50]}")
                
                if parts[1].startswith('method|'):
                    print("  ✓ Matches method| pattern")
                    match = re.match(r'method\|([^|]+)\|\.([^|]+)\|', parts[1])
                    if match:
                        print(f"    Type: {match.group(1)}")
                        print(f"    Method: {match.group(2)}")
        method_count += 1

print(f"\nTotal method| lines found: {method_count}")
