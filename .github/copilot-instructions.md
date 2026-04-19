# GitHub Copilot Instructions for myDupFinder

## Project Overview

myDupFinder is a .NET 10 C# application for finding duplicate files by scanning directories and computing file hashes. It stores file metadata and SHA-512 hashes in SQLite databases and can generate CSV reports of duplicates. Because it is usually a lot of files, the code needs to be fast but even more important correct. Also, pause and resume is important since scanning may take multiple days.

## Solution Structure

- **`src/rogerbriggen.myDupFinder/`** - Console application entry point (namespace: `RogerBriggen.MyDupFinder`)
- **`src/rogerbriggen.myDupFinderLib/`** - Core library with scanning, duplicate finding logic, and project configuration (namespace: `RogerBriggen.MyDupFinderLib`)
- **`src/rogerbriggen.myDupFinderDB/`** - Database layer using Entity Framework Core with SQLite (namespace: `RogerBriggen.myDupFinderDB`)
- **`src/rogerbriggen.myDupFinderData/`** - Data transfer objects and shared models (namespace: `RogerBriggen.myDupFinderData`)
- **`src/rogerbriggen.myDupFinderWin/`** - Windows desktop application
- **`tests/rogerbriggen.myDupFinderLibUnitTest/`** - Unit tests using xUnit (namespace: `RogerBriggen.MyDupFinderLibUnitTest`)

## Build and Test Commands

```bash
# Restore dependencies
dotnet restore && dotnet tool restore

# Build
dotnet build --configuration Release

# Run tests
dotnet test --configuration Release --verbosity normal

# Run tests with code coverage
dotnet test --configuration Release /p:CollectCoverage=true /p:CoverletOutputFormat="cobertura" /p:CoverletOutput="TestResults/"

# Generate coverage report
dotnet tool run reportgenerator -- "-reports:tests/rogerbriggen.myDupFinderLibUnitTest/TestResults/coverage.cobertura.xml" "-targetdir:tests/rogerbriggen.myDupFinderLibUnitTest/TestResults/CoverageReport" "-reporttypes:Html;MarkdownSummaryGithub"
```

## Coding Conventions

### General Style

- Use C# latest language features (LangVersion: latest)
- Nullable reference types are enabled (`<Nullable>enable</Nullable>`)
- Nullable warnings are treated as errors
- Use file-scoped namespaces (`namespace X;` instead of `namespace X { }`)
- Follow the `.editorconfig` rules strictly

### Naming Conventions

- **Types** (classes, structs, interfaces, enums): PascalCase
- **Interfaces**: Prefix with `I` (e.g., `IScanService`, `IRunner`)
- **Public/protected members**: PascalCase
- **Private/internal instance fields**: `_camelCase` (prefix with underscore)
- **Private/internal static fields**: `s_camelCase` (prefix with `s_`)
- **Constants**: PascalCase
- **Local variables and parameters**: camelCase
- **Methods**: PascalCase

### File Header (Required)

Every C# file must include the following license header:

```csharp
// Roger Briggen license this file to you under the MIT license.
```

This is enforced by IDE0073 as a warning.

### Code Style Preferences

- Use `var` for built-in types and when type is apparent
- Use pattern matching over `is`/`as` with casts
- Place `using` directives outside namespace
- Sort `System` directives first
- Use braces for control flow blocks
- New line before open braces (Allman style)
- Indent with 4 spaces for C# files
- Use 2 spaces for XML/YAML/project files

## Architecture Patterns

- **Dependency Injection**: Uses `Microsoft.Extensions.DependencyInjection`
- **Logging**: Uses `Microsoft.Extensions.Logging` with Serilog as the provider
- **Service Pattern**: Services implement `IService` interface (Common folder)
- **Runner Pattern**: Background work uses `IRunner`/`BasicRunner` pattern
- **Database**: Entity Framework Core with SQLite (`Microsoft.EntityFrameworkCore.Sqlite`)
- **Configuration**: XML-based project files (DTOs serialized/deserialized)
- **Versioning**: Nerdbank.GitVersioning (version defined in `version.json`)

## Testing

- Framework: xUnit
- Code coverage: Coverlet
- Test project references the library directly
- Tests follow the pattern: `MethodName_Scenario_ExpectedBehavior` or descriptive names
- Keep tests in the `tests/rogerbriggen.myDupFinderLibUnitTest/` project

## CLI Usage

The console app accepts commands:
- `exampleproject [projectfile.xml]` - Generate an example project file
- `run [projectfile.xml]` - Run the duplicate finder with a project file
- `dryrun [projectfile.xml]` - Validate a project file without running

## Key Dependencies

- .NET 10
- Entity Framework Core (SQLite)
- Serilog (logging)
- Nerdbank.GitVersioning (versioning)
- xUnit (testing)
- Coverlet (code coverage)
- ReportGenerator (coverage reports)
