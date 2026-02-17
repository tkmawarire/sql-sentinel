# syntax=docker/dockerfile:1

# ============================================================
# Stage 1: Build
# ============================================================
FROM mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS build
WORKDIR /src

# Copy project files first for layer caching
COPY SqlServer.Profiler.Mcp.slnx .
COPY SqlServer.Profiler.Mcp/SqlServer.Profiler.Mcp.csproj SqlServer.Profiler.Mcp/
COPY SqlServer.Profiler.Mcp/Directory.Build.props SqlServer.Profiler.Mcp/

# Restore NuGet packages (cached unless .csproj changes)
RUN dotnet restore SqlServer.Profiler.Mcp/SqlServer.Profiler.Mcp.csproj

# Copy source
COPY SqlServer.Profiler.Mcp/ SqlServer.Profiler.Mcp/

# Multi-arch: map Docker platform to .NET RID
ARG TARGETARCH
RUN case "$TARGETARCH" in \
      "amd64") RID="linux-x64" ;; \
      "arm64") RID="linux-arm64" ;; \
      *) echo "Unsupported architecture: $TARGETARCH" && exit 1 ;; \
    esac && \
    dotnet publish SqlServer.Profiler.Mcp/SqlServer.Profiler.Mcp.csproj \
      -c Release \
      -r $RID \
      --no-self-contained \
      -p:PublishSingleFile=false \
      -p:PublishReadyToRun=true \
      -o /app/publish

# ============================================================
# Stage 2: Runtime
# Debian bookworm-slim (NOT Alpine) - Microsoft.Data.SqlClient
# uses native Kerberos/GSSAPI libs that crash on Alpine's musl.
# ============================================================
FROM mcr.microsoft.com/dotnet/runtime:9.0-bookworm-slim AS runtime
WORKDIR /app

# ICU for globalization (SqlClient needs it for collation)
RUN apt-get update \
    && apt-get install -y --no-install-recommends libicu72 \
    && rm -rf /var/lib/apt/lists/*

# OCI + MCP labels
LABEL io.modelcontextprotocol.server.name="io.github.tkmawarire/sql-sentinel"
LABEL org.opencontainers.image.title="SQL Sentinel MCP Server"
LABEL org.opencontainers.image.description="SQL Server monitoring and diagnostics for AI agents using Extended Events"
LABEL org.opencontainers.image.version="2.0.0"
LABEL org.opencontainers.image.source="https://github.com/tkmawarire/sql-sentinel"
LABEL org.opencontainers.image.licenses="MIT"

COPY --from=build /app/publish .

# stdio transport: reads stdin, writes stdout. No ports exposed.
# Usage: docker run -i --rm ghcr.io/tkmawarire/sql-sentinel-mcp:latest
# Network: use --network host to reach SQL Server on the host machine.
ENTRYPOINT ["dotnet", "sql-sentinel-mcp.dll"]
