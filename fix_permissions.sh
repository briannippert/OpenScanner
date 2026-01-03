#!/bin/bash

# OpenScanner Permission Fixer

if [ "$EUID" -ne 0 ]; then
  echo "[ERROR] Please run as root (e.g. sudo ./fix_permissions.sh)"
  exit 1
fi

# Detect the real user
REAL_USER=${SUDO_USER:-$USER}
PROJECT_ROOT=$(pwd)

echo "[INFO] Fixing permissions in $PROJECT_ROOT for user: $REAL_USER"

# 1. Change ownership of everything to the real user
chown -R "$REAL_USER":"$REAL_USER" "$PROJECT_ROOT"

# 2. Set directory permissions to 755 (rwxr-xr-x)
find "$PROJECT_ROOT" -type d -exec chmod 755 {} +

# 3. Set file permissions to 644 (rw-r--r--)
find "$PROJECT_ROOT" -type f -exec chmod 644 {} +

# 4. Restore execution bits for scripts
find "$PROJECT_ROOT" -name "*.sh" -exec chmod +x {} +

echo "[OK] Permissions restored. You can now build and run as $REAL_USER."