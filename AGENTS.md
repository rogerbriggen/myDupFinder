# AGENTS.md

This file provides instructions for Claude Code, Github Copilot, Codex (and other AI assistants) working with this repository.

For detailed project instructions, conventions, and architecture details, see:
[.github/copilot-instructions.md](.github/copilot-instructions.md)

## Quick Reference

### Build and Test

```bash
dotnet restore && dotnet tool restore
dotnet build --configuration Release
dotnet test --configuration Release --verbosity normal --blame-hang-timeout 5m
```

Test runs are limited: `tests/rogerbriggen.myDupFinderLibUnitTest/.runsettings` sets a session timeout of 10 minutes (a plain `dotnet test` picks it up), and `--blame-hang-timeout 5m` names a test that hangs. The suite takes well under a minute. Run tests in the foreground only, never in the background or detached.

### Project

- .NET 11 C# solution for finding duplicate files
- Uses file-scoped namespaces, nullable reference types enabled
- Follows `.editorconfig` conventions (Allman braces, 4-space indent for C#)
- Every C# file requires MIT license header: `// Roger Briggen license this file to you under the MIT license.`
- Tests use xUnit in `tests/rogerbriggen.myDupFinderLibUnitTest/`
