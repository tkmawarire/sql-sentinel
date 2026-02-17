using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;

namespace SqlServer.Profiler.Mcp.Tools;

/// <summary>
/// MCP Tools for checking and managing SQL Server profiler permissions.
/// </summary>
[McpServerToolType]
public partial class PermissionTools
{
    private static readonly string[] RequiredPermissions =
    [
        "ALTER ANY EVENT SESSION",
        "VIEW SERVER STATE"
    ];

    [McpServerTool(Name = "sqlsentinel_check_permissions")]
    [Description("""
        Check if the current SQL Server login has the required permissions for profiling.

        Required permissions for SQL Sentinel:
        - ALTER ANY EVENT SESSION: Create, modify, and drop Extended Events sessions
        - VIEW SERVER STATE: Read from ring buffer targets and DMVs

        Returns the current permission status and provides GRANT statements if permissions are missing.
        """)]
    public async Task<string> CheckPermissions(
        [Description("SQL Server connection string")] string connectionString)
    {
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            // Get current login name
            string currentLogin;
            await using (var loginCmd = new SqlCommand("SELECT SUSER_SNAME()", conn))
            {
                currentLogin = (string)(await loginCmd.ExecuteScalarAsync() ?? "unknown");
            }

            // Check permissions
            var grantedPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const string permissionQuery = """
                SELECT permission_name
                FROM fn_my_permissions(NULL, 'SERVER')
                WHERE permission_name IN ('ALTER ANY EVENT SESSION', 'VIEW SERVER STATE')
                """;

            await using (var permCmd = new SqlCommand(permissionQuery, conn))
            await using (var reader = await permCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    grantedPermissions.Add(reader.GetString(0));
                }
            }

            // Build response
            var permissions = new Dictionary<string, object>
            {
                ["alterAnyEventSession"] = new
                {
                    granted = grantedPermissions.Contains("ALTER ANY EVENT SESSION"),
                    description = "Create, modify, and drop Extended Events sessions"
                },
                ["viewServerState"] = new
                {
                    granted = grantedPermissions.Contains("VIEW SERVER STATE"),
                    description = "Read from ring buffer targets and DMVs"
                }
            };

            var allGranted = grantedPermissions.Count == RequiredPermissions.Length;
            var missingPermissions = RequiredPermissions
                .Where(p => !grantedPermissions.Contains(p))
                .ToList();

            var result = new Dictionary<string, object>
            {
                ["success"] = true,
                ["currentLogin"] = currentLogin,
                ["permissions"] = permissions,
                ["allPermissionsGranted"] = allGranted
            };

            if (allGranted)
            {
                result["message"] = "All required permissions are granted. You can use all SQL Sentinel tools.";
            }
            else
            {
                result["message"] = "Missing required permissions. See grantStatements for the SQL needed.";
                result["grantStatements"] = missingPermissions
                    .Select(p => $"GRANT {p} TO [{EscapeBrackets(currentLogin)}];")
                    .ToList();
                result["suggestion"] = "Run the grant statements using a sysadmin connection, or use sqlsentinel_grant_permissions tool.";
            }

            // Check blocked process threshold setting
            try
            {
                await using var bptCmd = new SqlCommand(
                    "SELECT CAST(value_in_use AS INT) FROM sys.configurations WHERE name = 'blocked process threshold (s)'",
                    conn);
                var bptValue = await bptCmd.ExecuteScalarAsync();
                var threshold = bptValue is int val ? val : 0;
                result["blockedProcessThreshold"] = threshold;
                if (threshold == 0)
                {
                    result["blockedProcessWarning"] = "Blocked process threshold is 0 (disabled). To capture blocking events, run: EXEC sp_configure 'blocked process threshold', 5; RECONFIGURE;";
                }
            }
            catch
            {
                // Non-critical — skip if we can't read the config
            }

            return JsonSerializer.Serialize(result, JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message,
                suggestion = "Check connection string credentials and SQL Server accessibility."
            }, JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "sqlsentinel_grant_permissions")]
    [Description("""
        Grant the required profiling permissions to a specified SQL Server login.

        IMPORTANT: The connection used must have elevated privileges (sysadmin role or CONTROL SERVER permission).

        Grants:
        - ALTER ANY EVENT SESSION: Create, modify, and drop Extended Events sessions
        - VIEW SERVER STATE: Read from ring buffer targets and DMVs
        """)]
    public async Task<string> GrantPermissions(
        [Description("SQL Server connection string (must use a login with sysadmin or CONTROL SERVER)")] string connectionString,
        [Description("The SQL Server login to grant permissions to (e.g., 'app_user' or 'DOMAIN\\\\Username')")] string targetLogin)
    {
        try
        {
            // Validate login name format (basic SQL injection prevention)
            if (!IsValidLoginName(targetLogin))
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = "Invalid login name format.",
                    targetLogin,
                    suggestion = "Login name should contain only alphanumeric characters, underscores, backslashes (for domain), and hyphens."
                }, JsonOptions.Default);
            }

            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            // Get current login for response
            string grantingLogin;
            await using (var loginCmd = new SqlCommand("SELECT SUSER_SNAME()", conn))
            {
                grantingLogin = (string)(await loginCmd.ExecuteScalarAsync() ?? "unknown");
            }

            // Check if current connection can grant permissions
            const string canGrantQuery = """
                SELECT CASE
                    WHEN IS_SRVROLEMEMBER('sysadmin') = 1 THEN 1
                    WHEN EXISTS (
                        SELECT 1 FROM fn_my_permissions(NULL, 'SERVER')
                        WHERE permission_name = 'CONTROL SERVER'
                    ) THEN 1
                    ELSE 0
                END
                """;

            await using (var canGrantCmd = new SqlCommand(canGrantQuery, conn))
            {
                var canGrant = (int)(await canGrantCmd.ExecuteScalarAsync() ?? 0);
                if (canGrant != 1)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"Current login '{grantingLogin}' does not have permission to grant server-level permissions.",
                        currentLogin = grantingLogin,
                        suggestion = "Connect using a sysadmin account (e.g., 'sa') or a login with CONTROL SERVER permission."
                    }, JsonOptions.Default);
                }
            }

            // Check if target login exists
            await using (var existsCmd = new SqlCommand(
                "SELECT 1 FROM sys.server_principals WHERE name = @login", conn))
            {
                existsCmd.Parameters.AddWithValue("@login", targetLogin);
                var exists = await existsCmd.ExecuteScalarAsync();
                if (exists == null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"Login '{targetLogin}' does not exist on this SQL Server instance.",
                        targetLogin,
                        suggestion = "Verify the login name. Use 'DOMAIN\\Username' format for Windows logins. Check sys.server_principals for existing logins."
                    }, JsonOptions.Default);
                }
            }

            // Grant permissions
            var escapedLogin = EscapeBrackets(targetLogin);
            var grantedPermissions = new List<string>();
            var failedPermissions = new List<(string permission, string error)>();

            foreach (var permission in RequiredPermissions)
            {
                try
                {
                    var grantSql = $"GRANT {permission} TO [{escapedLogin}]";
                    await using var grantCmd = new SqlCommand(grantSql, conn);
                    await grantCmd.ExecuteNonQueryAsync();
                    grantedPermissions.Add(permission);
                }
                catch (Exception ex)
                {
                    failedPermissions.Add((permission, ex.Message));
                }
            }

            if (failedPermissions.Count == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    targetLogin,
                    grantedBy = grantingLogin,
                    permissionsGranted = grantedPermissions,
                    message = $"Successfully granted profiling permissions to [{targetLogin}]. The login can now use all SQL Sentinel tools."
                }, JsonOptions.Default);
            }
            else if (grantedPermissions.Count > 0)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"Partially completed. Failed to grant: {string.Join(", ", failedPermissions.Select(f => f.permission))}",
                    targetLogin,
                    partialGrant = grantedPermissions,
                    failedGrant = failedPermissions.Select(f => new { permission = f.permission, error = f.error }),
                    suggestion = "Check SQL Server error logs for more details."
                }, JsonOptions.Default);
            }
            else
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = $"Failed to grant permissions: {failedPermissions[0].error}",
                    targetLogin,
                    suggestion = "Check SQL Server error logs for more details."
                }, JsonOptions.Default);
            }
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                success = false,
                error = ex.Message,
                suggestion = "Check connection string credentials and ensure you have sysadmin privileges."
            }, JsonOptions.Default);
        }
    }

    private static bool IsValidLoginName(string loginName)
    {
        if (string.IsNullOrWhiteSpace(loginName) || loginName.Length > 128)
            return false;

        // Allow alphanumeric, underscore, hyphen, backslash (for domain), at sign (for email-like), and dot
        return ValidLoginRegex().IsMatch(loginName);
    }

    private static string EscapeBrackets(string identifier)
    {
        // Escape ] as ]] for SQL Server bracket identifiers
        return identifier.Replace("]", "]]");
    }

    [GeneratedRegex(@"^[\w\-\\@\.]+$")]
    private static partial Regex ValidLoginRegex();
}
