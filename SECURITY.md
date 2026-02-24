# Security Policy

## Supported Versions

| Version | Supported |
|---------|-----------|
| 2.x     | Yes       |
| 1.x     | No        |

## Reporting Vulnerabilities

If you discover a security vulnerability, please report it responsibly:

1. **Do NOT open a public GitHub issue.**
2. Email security concerns to the maintainer via the contact information in the repository.
3. Include a description of the vulnerability, steps to reproduce, and potential impact.
4. Allow reasonable time for a fix before public disclosure.

## Security Model

### Transport

SQL Sentinel uses **stdio transport** for MCP communication. The server reads from stdin and writes to stdout. There are no open network ports or HTTP endpoints in the MCP server itself.

The API project (`SqlServer.Profiler.Mcp.Api`) is a **localhost-only debug tool** intended for development use only. It should never be exposed to a network. Swagger UI is only enabled in the Development environment.

### SQL Server Authentication

SQL Sentinel delegates authentication entirely to SQL Server via connection strings. The server does not store, cache, or manage credentials.

**Best practices:**
- Use Windows/Integrated Authentication where possible.
- Use `TrustServerCertificate=false;Encrypt=true` in production.
- Only use `TrustServerCertificate=true` in development with self-signed certificates.
- Store connection strings in environment variables (`SQL_SENTINEL_CONNECTION_STRING`), not in configuration files.

### DatabaseTools Guardrails

The `DatabaseTools` module accepts user-provided SQL statements with the following safeguards:

- **Keyword prefix validation:** Each tool enforces that SQL starts with the expected keyword (SELECT, INSERT, CREATE, UPDATE, DROP).
- **Statement separation detection:** Semicolons outside string literals and `GO` batch separators are blocked to prevent multi-statement injection.
- **Context-specific deny lists:** Each operation context blocks dangerous keywords. For example, `SELECT` context blocks `DROP`, `DELETE`, `TRUNCATE`, `EXEC`, `xp_`, `OPENROWSET`, and other dangerous patterns.
- **Command timeout:** All database commands have a 30-second timeout to prevent long-running attacks.

**Important:** These guardrails are defense-in-depth measures. For production deployments, always use a **least-privilege SQL login** that only has permissions on the specific tables and operations needed. Never use `sa` or sysadmin accounts.

### Error Handling

All MCP tool errors are sanitized before being returned to clients. Raw SQL Server error messages (which may contain schema names, table structures, or version information) are mapped to generic messages. Full exception details are logged to stderr for server-side diagnostics.

### Memory Storage

The persistent memory system stores profiling data at `~/.sqlsentinel/memory/` (or the path specified by `SQLSENTINEL_MEMORY_PATH`).

- Memory files contain query text, performance metrics, and server metadata.
- On Unix systems, the memory directory is created with owner-only permissions (700).
- Set `SQLSENTINEL_MEMORY_ENABLED=false` to disable all memory persistence.
- Memory data has automatic TTL-based expiration (30 days default, 90 days for tagged captures).

### Docker Deployment

The Docker image runs as a non-root user (`sentinel`, UID 1654) for security. The memory path defaults to `/home/sentinel/.sqlsentinel/memory/`.

## Dependency Scanning

A GitHub Actions workflow (`.github/workflows/security.yml`) runs `dotnet list package --vulnerable --include-transitive` weekly and on changes to package references.
