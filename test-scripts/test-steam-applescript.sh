#!/bin/bash
# Test script to verify Steam AppleScript automation works on your system
# Run this from macOS Terminal (not through Wine)

echo "========================================="
echo "Steam AppleScript Automation Test"
echo "========================================="
echo ""

# Check if Steam is running
STEAM_RUNNING=$(osascript -e 'tell application "System Events" to count (processes where name is "Steam")' 2>/dev/null)
if [ "$STEAM_RUNNING" != "1" ]; then
    echo "❌ ERROR: Steam is not running!"
    echo "   Please start Steam and try again."
    exit 1
fi
echo "✅ Steam is running"

# Check Accessibility permissions
echo ""
echo "Testing Accessibility permissions..."
osascript -e 'tell application "System Events" to tell process "Steam" to get name' &>/dev/null
if [ $? -ne 0 ]; then
    echo "⚠️  WARNING: Cannot access Steam via System Events"
    echo "   You may need to grant Accessibility permissions:"
    echo "   1. System Preferences → Security & Privacy → Privacy → Accessibility"
    echo "   2. Add Terminal.app (or the app running this script)"
    echo ""
else
    echo "✅ Accessibility permissions OK"
fi

echo ""
echo "Attempting to set Steam status to Invisible..."
echo "(Steam will be brought to front)"
echo ""

# Method 1: Try menu click
echo "Method 1: Menu bar automation..."
osascript <<'EOF'
tell application "Steam" to activate
delay 1
tell application "System Events"
    tell process "Steam"
        set frontmost to true
        delay 0.5
        
        -- Try to find and click the Friends menu
        try
            -- Get all menu bar items to find Friends
            set menuBarItems to name of menu bar items of menu bar 1
            log menuBarItems
            
            -- Try clicking Friends menu (try different approaches)
            tell menu bar 1
                tell menu bar item "Friends"
                    click
                end tell
            end tell
            
            delay 0.5
            
            -- Try to click Invisible in the dropdown
            try
                click menu item "Invisible" of menu 1 of menu bar item "Friends" of menu bar 1
                log "SUCCESS: Clicked Invisible via menu"
            on error errMsg
                log "Menu item click failed: " & errMsg
            end try
        on error errMsg
            log "Menu bar access failed: " & errMsg
        end try
    end tell
end tell
EOF

if [ $? -eq 0 ]; then
    echo "✅ Method 1 completed (check if status changed)"
else
    echo "❌ Method 1 failed"
fi

echo ""
echo "========================================="
echo "If the status didn't change, the issue is:"
echo "1. Accessibility permissions not granted"
echo "2. Steam menu structure is different"
echo "3. Wine cannot execute these commands"
echo "========================================="
echo ""
echo "Fallback: Opening Steam friends window..."
open 'steam://friends' 2>/dev/null

echo ""
echo "Please manually set your status to Invisible via the Friends menu."
