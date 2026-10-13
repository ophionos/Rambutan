#!/bin/bash
# SessionStart hook for Claude Code on the web: makes `dotnet build/test` work
# in a fresh cloud container. Local sessions are left alone.
#
# The SDK comes from Ubuntu's archive (dotnet-sdk-10.0 in noble-updates), not
# dotnet-install.sh: the cloud network policy blocks Microsoft's download hosts
# (builds.dotnet.microsoft.com, dotnetcli.azureedge.net), while the Ubuntu
# archive and api.nuget.org are reachable.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

cd "$CLAUDE_PROJECT_DIR"

if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  export DEBIAN_FRONTEND=noninteractive
  # Unrelated third-party PPAs in the base image may be unreachable; the Ubuntu
  # indexes still refresh, so don't let their failure abort the hook.
  apt-get update -qq || true
  apt-get install -y -qq dotnet-sdk-10.0
fi

if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  {
    echo "export DOTNET_CLI_TELEMETRY_OPTOUT=1"
    echo "export DOTNET_NOLOGO=1"
  } >> "$CLAUDE_ENV_FILE"
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet restore Rambutan.slnx

# UI driving (the `demo` skill, tools/ui-driver/ui.py): a virtual display and the
# two Python packages the driver uses. Best effort: builds and tests don't need it.
if ! command -v Xvfb >/dev/null 2>&1; then
  apt-get install -y -qq xvfb || true
fi
if ! python3 -c "import PIL, Xlib" 2>/dev/null; then
  python3 -m pip install -q --root-user-action=ignore pillow python-xlib || true
fi
