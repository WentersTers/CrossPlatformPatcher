#!/bin/bash
# Test if Wine can execute macOS commands
echo "Testing macOS command execution from Wine..."
echo "Current directory: $(pwd)"
echo "Shell: $SHELL"

# Test if osascript is accessible
if command -v osascript &>/dev/null; then
    echo "✓ osascript is available"
    osascript -e 'display dialog "Wine can execute macOS commands!"' 2>&1 &
    echo "  (Test dialog launched in background)"
else
    echo "✗ osascript is NOT available"
fi

# Test if open command works
if command -v open &>/dev/null; then
    echo "✓ open command is available"
else
    echo "✗ open command is NOT available"
fi
