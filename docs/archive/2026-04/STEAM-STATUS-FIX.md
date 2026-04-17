# Steam Status Control Fix

## Problem
When saying "hey paicom hide my online status on steam", the command would only open Steam but not set the status to invisible.

## Root Cause
The `TryDispatchNativeCommand` method in `OpenWakeWordHelper.cs` detected the word "steam" but didn't have specific handling for status-related keywords like "invisible", "offline", "hide status", or "online status". It would fall through to the generic `steam-launch` command which only opens the Steam application.

## Solution
Added explicit detection and handling for Steam status commands:

### New Command Detection
The code now checks for these keywords in the match phrase:

**For Invisible/Offline:**
- "invisible"
- "offline"
- "hide" + "status"
- "appear" + "offline"

**For Online/Active:**
- "online"
- "active"
- "show" + "status"

### Implementation by Platform

#### macOS (Native & Wine)
Uses AppleScript to:
1. Activate Steam and bring it to front
2. Open Steam console with `Cmd+Shift+`` (backtick)
3. Type the console command `friends status invisible` or `friends status online`
4. Press Enter to execute

Fallback: Opens Steam friends window if AppleScript fails

#### Linux
Uses Steam command-line arguments:
- `steam +opensteamweb +friends_status_invisible`
- `steam +opensteamweb +friends_status_online`

Fallback: Opens Steam friends window with a message to manually set status

## Files Modified
- `/PAIcom.OWW/OpenWakeWordHelper.cs`
- `/Core/OpenWakeWordHelper.cs`

## Testing
After rebuilding the patcher with these changes:
1. Apply the patch to your PAIcom.exe
2. Say: "hey paicom hide my online status on steam"
3. Should set Steam to invisible status
4. Say: "hey paicom show my online status on steam"
5. Should set Steam back to online status

## Notes
- AppleScript requires Accessibility permissions for System Events
- Steam must be running for the status change to work
- The AppleScript approach is more reliable than console commands on macOS
- Linux support depends on Steam's command-line argument support
