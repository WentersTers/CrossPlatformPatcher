#!/bin/bash
# Set Steam to invisible status on macOS
# This script should work when called from Wine or native macOS

STATUS="${1:-invisible}"

echo "Setting Steam status to: $STATUS"

# Check if Steam is running
STEAM_RUNNING=$(osascript -e 'tell application "System Events" to count (processes where name is "Steam")' 2>/dev/null)
if [ "$STEAM_RUNNING" != "1" ]; then
    echo "Steam is not running"
    exit 1
fi

# Use AppleScript to control Steam via GUI
osascript <<EOF
tell application "Steam" to activate
delay 1

tell application "System Events"
    tell process "Steam"
        set frontmost to true
    end tell
    
    delay 0.5
    
    -- Try to click the Friends menu and set status
    try
        -- Get the menu bar
        tell menu bar 1
            -- Click on "Friends" menu (might be localized)
            tell menu bar item "Friends"
                click
            end tell
        end tell
        
        delay 0.3
        
        -- Navigate to status submenu
        tell window 1
            try
                -- Try to find and click the status option
                click menu item "$STATUS" of menu "Friends"
            on error
                -- Fallback: use keyboard navigation
                keystroke "$STATUS"
                delay 0.2
                key code 36 -- Enter
            end try
        end tell
        
    on error errMsg
        log "Menu approach failed: " & errMsg
        
        -- Fallback: Try console command approach
        tell process "Steam"
            -- Open console with Cmd+Shift+`
            keystroke "`" using {command down, shift down}
            delay 0.5
            keystroke "friends status $STATUS"
            delay 0.3
            key code 36 -- Enter
            delay 0.3
            keystroke "`" using {command down, shift down}
        end tell
    end try
end tell

delay 0.5
tell application "System Events"
    tell process "Steam"
        -- Click elsewhere to dismiss any dialogs
        key code 53 -- Escape
    end tell
end tell
EOF

if [ $? -eq 0 ]; then
    echo "SUCCESS: Steam status should be set to $STATUS"
else
    echo "FAILED: Could not set Steam status"
    # Fallback: open friends
    open 'steam://friends' 2>/dev/null
fi
