# Dev container image for myDupFinder. The .NET SDK, Node.js, GitHub CLI and
# Claude Code come from features in devcontainer.json; post-create.sh installs
# Codex CLI and its Claude Code plugin.
FROM mcr.microsoft.com/devcontainers/base:ubuntu-24.04

RUN apt-get update \
    && export DEBIAN_FRONTEND=noninteractive \
    && apt-get install -y --no-install-recommends \
        python3 python3-pip python3-venv python-is-python3 \
        ripgrep jq sqlite3 \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# Pre-create named volume mount points with the dev user's ownership.
RUN mkdir -p \
        /home/vscode/.claude \
        /home/vscode/.codex \
        /home/vscode/.config/gh \
        /home/vscode/.nuget/packages \
        /home/vscode/.cache/mydupfinder/artifacts \
    && chown -R vscode:vscode \
        /home/vscode/.claude \
        /home/vscode/.codex \
        /home/vscode/.config \
        /home/vscode/.nuget \
        /home/vscode/.cache/mydupfinder
