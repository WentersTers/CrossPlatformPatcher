#!/usr/bin/env python3
from datetime import datetime

log_file = "PAIcom_Player_Folder/diagnostics/runtime-diagnostics-raw-20260326-233751.log"

with open(log_file) as f:
    lines = f.readlines()

# Get start time
start_time_str = None
for line in lines:
    if line.startswith('# start='):
        start_time_str = line.split('=')[1].strip()
        break

# Get first and last method line timestamps
method_lines = [l for l in lines if l.startswith('2026-') and 'method|' in l]

if method_lines:
    first_ts = method_lines[0].split(' ')[0]
    last_ts = method_lines[-1].split(' ')[0]
    
    print(f"Start time (from header): {start_time_str}")
    print(f"First method timestamp:   {first_ts}")
    print(f"Last method timestamp:    {last_ts}")
    
    # Calculate seconds
    base, tz = start_time_str.rsplit('.', 1)
    frac = tz[:-1][:6]
    start_ts = f"{base}.{frac}+00:00"
    start = datetime.fromisoformat(start_ts)
    
    first_base, first_tz = first_ts.rsplit('.', 1)
    first_frac = first_tz[:-1][:6]
    first = datetime.fromisoformat(f"{first_base}.{first_frac}+00:00")
    
    print(f"\nTime difference for first method: {(first - start).total_seconds():.2f} seconds")
    
    # Sample some method line timestamps
    mid_point = len(method_lines) // 2
    sample_ts = method_lines[mid_point].split(' ')[0]
    sample_base, sample_tz = sample_ts.rsplit('.', 1)
    sample_frac = sample_tz[:-1][:6]
    sample = datetime.fromisoformat(f"{sample_base}.{sample_frac}+00:00")
    print(f"Time difference for middle method: {(sample - start).total_seconds():.2f} seconds")
    
    last_base, last_tz = last_ts.rsplit('.', 1)
    last_frac = last_tz[:-1][:6]
    last = datetime.fromisoformat(f"{last_base}.{last_frac}+00:00")
    print(f"Time difference for last method: {(last - start).total_seconds():.2f} seconds")
