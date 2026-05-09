# Steam Status Control - Implementation Notes

## Problem Analysis (From Logs)

Based on the runtime logs, here's what happens when you say "hey paicom hide my online status on steam":

1. ✅ Voice recognition works: "set my online status on steam"
2. ✅ Fuzzy matching works: matched to "hide my online status on steam" (86.7% confidence)
3. ✅ Command routing works: token resolved to `invisible`
4. ✅ Steam detection works: "Matched steam command"
5. ✅ Command execution works: "Executed native command: /bin/sh -c ..."
6. ❌ **But Steam status doesn't change**

### Root Cause

The issue is that **AppleScript GUI automation of Steam's status menu is unreliable**. The logs show the command IS executing, but the AppleScript approach has these problems:

1. **Wine Environment**: Running under Wine (`isWine=True`), which may have limited access to macOS accessibility features
2. **Steam Menu Structure**: Steam's Friends menu status options may not be accessible via standard AppleScript menu item clicks
3. **Accessibility Permissions**: System Events needs Accessibility permissions to control applications, which may not be granted to Wine's shell

## Current Implementation

### Approach: AppleScript Menu Click + Fallback

The code now tries to:
1. Write an AppleScript to `/tmp/steam-status.scpt`
2. Activate Steam
3. Use GUI automation to click: Friends → Invisible/Online
4. If AppleScript fails, fallback to opening `steam://friends`

### Code Location
- File: `/PAIcom.OWW/OpenWakeWordHelper.cs`
- Method: `TryDispatchNativeCommand` → `ExecuteNativeCommand`
- Command Types: `steam-set-invisible`, `steam-set-online`

### Platform Support

| Platform | Method | Reliability |
|----------|--------|-------------|
| macOS (Native) | AppleScript menu click | ⚠️ Medium (needs Accessibility permissions) |
| macOS (Wine) | AppleScript via /bin/sh | ⚠️ Medium (Wine limitations) |
| Linux | Steam CLI args (`+friends_status_invisible`) | ⚠️ Low (may not work in all Steam versions) |

## Testing & Debugging

### Test AppleScript Manually

Run this in Terminal to test if AppleScript can control Steam:

```bash
osascript -e 'tell application "Steam" to activate' \
          -e 'delay 0.5' \
          -e 'tell application "System Events"' \
          -e 'tell process "Steam"' \
          -e 'set frontmost to true' \
          -e 'end tell' \
          -e 'try' \
          -e 'click menu item "Invisible" of menu "Friends" of menu bar item "Friends" of menu bar 1' \
          -e 'end try' \
          -e 'end tell'
```

### Check Accessibility Permissions

1. Open **System Preferences** → **Security & Privacy** → **Privacy** → **Accessibility**
2. Ensure the terminal app running Wine has Accessibility permissions
3. You may need to add: Terminal, Wine, or the PAIcom launcher

### View Runtime Logs

Check `/PAIcom_Player_Folder/launcher-runtime.log` for execution details:
- Look for: `[oww-native-exec]` - shows which command is being executed
- Look for: `[oww-native-command]` - shows the actual command
- Look for: `[oww-native-cmd-success]` - confirms execution

## Alternative Solutions

### Option 1: Use Steam Console (Manual)
If automated approach doesn't work:
1. Steam must be running
2. Press `Ctrl+Shift+\`` (backtick) in Steam to open console
3. Type: `friends status invisible`
4. Press Enter

### Option 2: Third-Party Tools
Tools like [SteamCleaner](https://github.com/mackron/SteamCleaner) or SteamCMD might offer programmatic control, but they're complex to integrate.

### Option 3: Better AppleScript
The AppleScript might need to navigate the menu differently:

```applescript
tell application "Steam" to activate
delay 1
tell application "System Events"
    tell process "Steam"
        -- Get Friends menu (index might vary)
        click menu bar item 4 of menu bar 1  -- Try different indices
        delay 0.3
        -- Navigate to status submenu
        keystroke arrow down
        keystroke arrow down
        key code 36  -- Enter
    end tell
end tell
```

### Option 4: User Notification + Manual Action
If automation consistently fails:
- Open Steam friends window
- Show notification: "Please manually set your status to Invisible"
- User completes the action

## Files Modified

1. `/PAIcom.OWW/OpenWakeWordHelper.cs`
   - Added Steam status command detection
   - Implemented `steam-set-invisible` and `steam-set-online` commands
   
2. `/Core/OpenWakeWordHelper.cs`
   - Same changes for core version

## Next Steps

1. **Test the AppleScript** manually to see if it works outside of Wine
2. **Check Accessibility permissions** for Wine/Terminal
3. **Review logs** after next voice command to see if the new AppleScript executes
4. If still failing, consider implementing **Option 4** (user notification fallback)

## Known Limitations

- Steam's AppleScript support is limited
- Wine cannot directly execute macOS-native commands reliably
- GUI automation depends on exact menu structure which may change with Steam updates
- No official Steam API for status control from external applications
