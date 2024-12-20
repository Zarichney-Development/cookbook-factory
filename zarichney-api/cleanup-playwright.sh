#!/bin/bash

# Only proceed if running as root
if [ "$EUID" -ne 0 ]; then
  echo "Cleanup script must be run as root. Skipping cleanup."
  exit 0
fi

# Kill any Playwright-related processes started by this service
pkill -u ec2-user -f "chrome.*--headless"
pkill -u ec2-user -f "node.*run-driver"

# Clean up temporary files created by ec2-user
find /tmp -user ec2-user -name '.org.chromium.*' -type d -exec rm -rf {} +

# Clean up shared memory used by ec2-user
rm -rf /dev/shm/.com.google.Chrome.* 2>/dev/null

# Reset any stuck system resources
echo 3 > /proc/sys/vm/drop_caches 2>/dev/null || true
