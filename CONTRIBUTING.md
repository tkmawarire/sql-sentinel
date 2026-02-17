# Contributing to SQL Sentinel MCP Server

Thank you for your interest in contributing! This guide covers the process for submitting issues and pull requests.

## Code of Conduct

Be respectful and constructive. We expect all contributors to maintain a professional and welcoming environment. Harassment, discrimination, and unconstructive criticism will not be tolerated.

## How to Contribute

### Reporting Bugs

Open an issue with:

- Steps to reproduce the problem
- Expected vs actual behavior
- SQL Server version and edition (e.g., SQL Server 2019 Developer)
- Connection type (SQL Auth, Windows Auth, Azure SQL)
- .NET SDK version (`dotnet --version`)
- Error output or stack trace (redact connection strings and credentials)

### Suggesting Features

Open an issue describing:

- The use case and problem you're trying to solve
- Your proposed solution or approach
- Any alternatives you've considered

### Submitting Pull Requests

1. Fork the repository and create a branch from `main`
2. Make your changes (see guidelines below)
3. Ensure `dotnet build` passes with no warnings
4. Describe what and why in the PR description
5. Link related issues (e.g., "Closes #42")
6. Keep PRs focused — one logical change per PR

## Development Setup

See the [Development section](README.md#development) in the README for prerequisites, build instructions, and how to use the debug API and CLI.

## Branch Naming

Use a descriptive prefix:

| Prefix | Purpose |
|--------|---------|
| `feature/` | New functionality |
| `fix/` | Bug fixes |
| `docs/` | Documentation changes |
| `refactor/` | Code restructuring without behavior changes |

Examples: `feature/add-query-store-tool`, `fix/deadlock-xml-parsing`, `docs/update-tool-reference`

## Commit Messages

Follow [Conventional Commits](https://www.conventionalcommits.org/) format:

```
<type>: <short description>

[optional body]
```

| Type | Purpose |
|------|---------|
| `feat` | New feature |
| `fix` | Bug fix |
| `docs` | Documentation |
| `refactor` | Restructuring without behavior change |
| `chore` | Build, CI, dependencies |
| `test` | Adding or updating tests |

Examples:
```
feat: add query store integration tool
fix: handle empty XML in deadlock events
docs: add blocking analysis usage example
refactor: extract wait stat categorization into shared method
chore: update ModelContextProtocol to v0.2.0
```

## Code Style

- Follow existing patterns in the codebase
- Nullable reference types are enabled — handle nullability explicitly
- Use dependency injection for service access in tools
- Tools return JSON strings (use `System.Text.Json`), with optional Markdown via `responseFormat`
- Keep logging on stderr (stdout is reserved for MCP protocol)
- Use `CancellationToken` where appropriate for async operations

## Adding MCP Tools

1. Add a `public static` method in the appropriate file under `SqlServer.Profiler.Mcp/Tools/`
   - Session lifecycle → `SessionManagementTools.cs`
   - Event retrieval → `EventRetrievalTools.cs`
   - Diagnostics → `DiagnosticTools.cs`
   - Permissions → `PermissionTools.cs`
   - Or create a new file for a distinct category
2. Decorate with `[McpServerTool(Name = "sqlsentinel_your_tool")]` and `[Description("...")]`
3. Add `[Description("...")]` to all parameters — these become the tool's input schema for AI agents
4. Inject services via method parameters (DI resolves them automatically)
5. Follow the `sqlsentinel_` naming prefix convention
6. Return structured JSON; support `responseFormat` parameter for Markdown output where appropriate

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
