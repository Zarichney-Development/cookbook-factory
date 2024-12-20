#!/bin/bash
export PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1
export PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH=/usr/bin/google-chrome

# Validate that Chrome executable exists
if [ ! -x "$PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH" ]; then
  echo "Chrome executable not found at $PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH"
  exit 1
fi

dotnet /opt/cookbook-api/zarichney-api.dll
