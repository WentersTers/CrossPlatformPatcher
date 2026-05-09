#!/bin/bash
# Test script to set Steam to invisible status
# This should be run from macOS directly, not through Wine

echo "Testing Steam status change via AppleScript..."

# Test 1: Check if osascript is available
if ! command -v osascript &> /dev/null; then
    echo "ERROR: osascript not found"
    exit 1
fi

echo "osascript found, proceeding..."

# Test 2: Check if Steam is running
STEAM_RUNNING=$(osascript -e 'tell application "System Events" to count (processes where name is "Steam")')
if [ "$STEAM_RUNNING" = "0" ]; then
    echo "WARNING: Steam is not running"
    exit 1
fi

echo "Steam is running, attempting to set status to invisible..."

# Method 1: Try Steam's console command (most reliable)
osascript <<'EOF'
tell application "Steam" to activate
delay 0.5
tell application "System Events"
    tell process "Steam"
        set frontmost to true
    end tell
end tell
delay 0.3
tell application "System Events"
    -- Open Steam console (Shift+Ctrl+`)
    keystroke "`" using {control down, shift down}
    delay 0.3
    -- Type the command
    keystroke "friends status invisible"
    delay 0.2
    -- Press Enter
    key code 36
    delay 0.2
    -- Close console
    keystroke "`" using {control down, shift down}
end tell
EOF

if [ $? -eq 0 ]; then
    echo "SUCCESS: AppleScript executed successfully"
else
    echo "FAILED: AppleScript returned an error"
fi
