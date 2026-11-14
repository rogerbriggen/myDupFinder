#!/usr/bin/env bash
# Runs after creation; also safe to rerun manually after a setup failure.
set -euo pipefail

export CODEX_HOME="${CODEX_HOME:-$HOME/.codex}"

# Fix only the mount roots; shared credentials and caches may already contain
# files from other containers using the same vscode user.
sudo chown "$(id -u):$(id -g)" \
  "$HOME/.claude" \
  "$CODEX_HOME" \
  "$HOME/.config/gh" \
  "$NUGET_PACKAGES" \
  "$ArtifactsPath"

# Keep credentials in the shared volume instead of a container-local keyring.
# Seed fresh configurations only; existing shared settings belong to the user.
if [[ ! -f "$CODEX_HOME/config.toml" ]]; then
  (umask 077; printf '%s\n' 'cli_auth_credentials_store = "file"' > "$CODEX_HOME/config.toml")
fi

# The Node feature provides a user-writable global npm prefix.
npm install --global @openai/codex

# User scope puts the plugin in the same shared Claude volume as the reference
# Rust devcontainer. Avoid adding the marketplace again on subsequent runs.
codex_marketplaces="$(claude plugin marketplace list --json)"
if ! jq -e 'any(.[]; .name == "openai-codex")' <<< "$codex_marketplaces" > /dev/null; then
  claude plugin marketplace add openai/codex-plugin-cc
fi
claude plugin install codex@openai-codex --scope user

# Warm the shared NuGet cache and restore the repository's coverage report tool.
# EnableWindowsTargeting in devcontainer.json also permits restoring/building
# the Windows Forms project on Linux (running that UI still requires Windows).
dotnet restore
dotnet tool restore

echo
echo "Dev container ready:"
dotnet --version
gh --version | head -n1
claude --version
codex --version
python3 --version
echo
echo "Build:        dotnet build --configuration Release"
echo "Test:         dotnet test --configuration Release --verbosity normal"
echo "Run CLI:      dotnet run --project src/rogerbriggen.myDupFinder -- exampleproject projectfile.xml"
echo "Codex login:  codex login --device-auth (shared via the codex-config volume)"
echo "Claude login: claude"
echo "GitHub login: gh auth login"
echo "The Windows Forms UI can be built here but must run on Windows."
