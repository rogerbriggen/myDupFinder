# CLAUDE.md

This file provides instructions for Claude Code (and other AI assistants) working with this repository.

For detailed project instructions, conventions, and architecture details, see:
[.github/copilot-instructions.md](.github/copilot-instructions.md)

## Quick Reference

### Build and Test

```bash
dotnet restore && dotnet tool restore
dotnet build --configuration Release
dotnet test --configuration Release --verbosity normal
```

### Project

- .NET 10 C# solution for finding duplicate files
- Uses file-scoped namespaces, nullable reference types enabled
- Follows `.editorconfig` conventions (Allman braces, 4-space indent for C#)
- Every C# file requires MIT license header: `// Roger Briggen license this file to you under the MIT license.`
- Tests use xUnit in `tests/rogerbriggen.myDupFinderLibUnitTest/`
