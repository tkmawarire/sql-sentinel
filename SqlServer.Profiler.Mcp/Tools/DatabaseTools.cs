using System.ComponentModel;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;
using SqlServer.Profiler.Mcp.Models;
using SqlServer.Profiler.Mcp.Utilities;

namespace SqlServer.Profiler.Mcp.Tools;

/// <summary>
/// MCP Tools for SQL Server database CRUD operations.
/// Provides schema inspection, data querying, and data manipulation capabilities.
/// </summary>
[McpServerToolType]
public class DatabaseTools
{
    private const string ListTablesQuery = """
        SELECT TABLE_SCHEMA, TABLE_NAME
        FROM INFORMATION_SCHEMA.TABLES
        WHERE TABLE_TYPE = 'BASE TABLE'
        ORDER BY TABLE_SCHEMA, TABLE_NAME
        """;

    [McpServerTool(Name = "sqlsentinel_list_tables",
        Title = "List Tables",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false)]
    [Description("Lists all user tables in the SQL Server database. Returns schema-qualified table names (e.g., 'dbo.Users').")]
    public static async Task<string> ListTables(
        [Description("SQL Server connection string")] string connectionString)
    {
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            await using var cmd = new SqlCommand(ListTablesQuery, conn);
            using var reader = await cmd.ExecuteReaderAsync();

            var tables = new List<string>();
            while (await reader.ReadAsync())
            {
                tables.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
            }

            return JsonSerializer.Serialize(
                new DbOperationResult(success: true, data: tables), JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(
                new DbOperationResult(success: false, error: ex.Message), JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "sqlsentinel_describe_table",
        Title = "Describe Table",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false)]
    [Description("Returns detailed schema information for a table including columns, indexes, constraints, and foreign keys.")]
    public static async Task<string> DescribeTable(
        [Description("SQL Server connection string")] string connectionString,
        [Description("Table name, optionally schema-qualified (e.g., 'dbo.Users' or 'Users')")] string name)
    {
        string? schema = null;
        if (name.Contains('.'))
        {
            var parts = name.Split('.');
            if (parts.Length > 1)
            {
                schema = parts[0];
                name = parts[1];
            }
        }

        const string tableInfoQuery = """
            SELECT t.object_id AS id, t.name, s.name AS [schema],
                   p.value AS description, t.type, u.name AS owner
            FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            LEFT JOIN sys.extended_properties p
                ON p.major_id = t.object_id AND p.minor_id = 0 AND p.name = 'MS_Description'
            LEFT JOIN sys.sysusers u ON t.principal_id = u.uid
            WHERE t.name = @TableName AND (s.name = @TableSchema OR @TableSchema IS NULL)
            """;

        const string columnsQuery = """
            SELECT c.name, ty.name AS type, c.max_length AS length,
                   c.precision, c.scale, c.is_nullable AS nullable, p.value AS description
            FROM sys.columns c
            INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
            LEFT JOIN sys.extended_properties p
                ON p.major_id = c.object_id AND p.minor_id = c.column_id AND p.name = 'MS_Description'
            WHERE c.object_id = (
                SELECT object_id FROM sys.tables t
                INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
                WHERE t.name = @TableName AND (s.name = @TableSchema OR @TableSchema IS NULL))
            """;

        const string indexesQuery = """
            SELECT i.name, i.type_desc AS type, p.value AS description,
                STUFF((SELECT ',' + c.name FROM sys.index_columns ic
                    INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                    WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                    ORDER BY ic.key_ordinal FOR XML PATH('')), 1, 1, '') AS keys
            FROM sys.indexes i
            LEFT JOIN sys.extended_properties p
                ON p.major_id = i.object_id AND p.minor_id = i.index_id AND p.name = 'MS_Description'
            WHERE i.object_id = (
                SELECT object_id FROM sys.tables t
                INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
                WHERE t.name = @TableName AND (s.name = @TableSchema OR @TableSchema IS NULL))
                AND i.is_primary_key = 0 AND i.is_unique_constraint = 0
            """;

        const string constraintsQuery = """
            SELECT kc.name, kc.type_desc AS type,
                STUFF((SELECT ',' + c.name FROM sys.index_columns ic
                    INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                    WHERE ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
                    ORDER BY ic.key_ordinal FOR XML PATH('')), 1, 1, '') AS keys
            FROM sys.key_constraints kc
            WHERE kc.parent_object_id = (
                SELECT object_id FROM sys.tables t
                INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
                WHERE t.name = @TableName AND (s.name = @TableSchema OR @TableSchema IS NULL))
            """;

        const string foreignKeysQuery = """
            SELECT fk.name AS name,
                SCHEMA_NAME(tp.schema_id) AS [schema],
                tp.name AS table_name,
                STRING_AGG(cp.name, ', ') WITHIN GROUP (ORDER BY fkc.constraint_column_id) AS column_names,
                SCHEMA_NAME(tr.schema_id) AS referenced_schema,
                tr.name AS referenced_table,
                STRING_AGG(cr.name, ', ') WITHIN GROUP (ORDER BY fkc.constraint_column_id) AS referenced_column_names
            FROM sys.foreign_keys AS fk
            JOIN sys.foreign_key_columns AS fkc ON fk.object_id = fkc.constraint_object_id
            JOIN sys.tables AS tp ON fkc.parent_object_id = tp.object_id
            JOIN sys.columns AS cp ON fkc.parent_object_id = cp.object_id AND fkc.parent_column_id = cp.column_id
            JOIN sys.tables AS tr ON fkc.referenced_object_id = tr.object_id
            JOIN sys.columns AS cr ON fkc.referenced_object_id = cr.object_id AND fkc.referenced_column_id = cr.column_id
            WHERE (SCHEMA_NAME(tp.schema_id) = @TableSchema OR @TableSchema IS NULL)
                AND tp.name = @TableName
            GROUP BY fk.name, tp.schema_id, tp.name, tr.schema_id, tr.name
            """;

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            var result = new Dictionary<string, object>();

            // Table info
            await using (var cmd = new SqlCommand(tableInfoQuery, conn))
            {
                cmd.Parameters.AddWithValue("@TableName", name);
                cmd.Parameters.AddWithValue("@TableSchema", (object?)schema ?? DBNull.Value);
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    result["table"] = new
                    {
                        id = reader["id"],
                        name = reader["name"],
                        schema = reader["schema"],
                        owner = reader["owner"],
                        type = reader["type"],
                        description = reader["description"] is DBNull ? null : reader["description"]
                    };
                }
                else
                {
                    return JsonSerializer.Serialize(
                        new DbOperationResult(success: false, error: $"Table '{name}' not found."),
                        JsonOptions.Default);
                }
            }

            // Columns
            await using (var cmd = new SqlCommand(columnsQuery, conn))
            {
                cmd.Parameters.AddWithValue("@TableName", name);
                cmd.Parameters.AddWithValue("@TableSchema", (object?)schema ?? DBNull.Value);
                using var reader = await cmd.ExecuteReaderAsync();
                var columns = new List<object>();
                while (await reader.ReadAsync())
                {
                    columns.Add(new
                    {
                        name = reader["name"],
                        type = reader["type"],
                        length = reader["length"],
                        precision = reader["precision"],
                        scale = reader["scale"],
                        nullable = (bool)reader["nullable"],
                        description = reader["description"] is DBNull ? null : reader["description"]
                    });
                }
                result["columns"] = columns;
            }

            // Indexes
            await using (var cmd = new SqlCommand(indexesQuery, conn))
            {
                cmd.Parameters.AddWithValue("@TableName", name);
                cmd.Parameters.AddWithValue("@TableSchema", (object?)schema ?? DBNull.Value);
                using var reader = await cmd.ExecuteReaderAsync();
                var indexes = new List<object>();
                while (await reader.ReadAsync())
                {
                    indexes.Add(new
                    {
                        name = reader["name"],
                        type = reader["type"],
                        description = reader["description"] is DBNull ? null : reader["description"],
                        keys = reader["keys"]
                    });
                }
                result["indexes"] = indexes;
            }

            // Constraints
            await using (var cmd = new SqlCommand(constraintsQuery, conn))
            {
                cmd.Parameters.AddWithValue("@TableName", name);
                cmd.Parameters.AddWithValue("@TableSchema", (object?)schema ?? DBNull.Value);
                using var reader = await cmd.ExecuteReaderAsync();
                var constraints = new List<object>();
                while (await reader.ReadAsync())
                {
                    constraints.Add(new
                    {
                        name = reader["name"],
                        type = reader["type"],
                        keys = reader["keys"]
                    });
                }
                result["constraints"] = constraints;
            }

            // Foreign Keys
            await using (var cmd = new SqlCommand(foreignKeysQuery, conn))
            {
                cmd.Parameters.AddWithValue("@TableName", name);
                cmd.Parameters.AddWithValue("@TableSchema", (object?)schema ?? DBNull.Value);
                using var reader = await cmd.ExecuteReaderAsync();
                var foreignKeys = new List<object>();
                while (await reader.ReadAsync())
                {
                    foreignKeys.Add(new
                    {
                        name = reader["name"],
                        schema = reader["schema"],
                        tableName = reader["table_name"],
                        columnNames = reader["column_names"],
                        referencedSchema = reader["referenced_schema"],
                        referencedTable = reader["referenced_table"],
                        referencedColumnNames = reader["referenced_column_names"]
                    });
                }
                result["foreignKeys"] = foreignKeys;
            }

            return JsonSerializer.Serialize(
                new DbOperationResult(success: true, data: result), JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(
                new DbOperationResult(success: false, error: ex.Message), JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "sqlsentinel_create_table",
        Title = "Create Table",
        ReadOnly = false,
        Destructive = false)]
    [Description("Creates a new table in the SQL Server database. Expects a valid CREATE TABLE SQL statement.")]
    public static async Task<string> CreateTable(
        [Description("SQL Server connection string")] string connectionString,
        [Description("CREATE TABLE SQL statement")] string sql)
    {
        if (!SqlInputValidator.StartsWithKeyword(sql, "CREATE"))
        {
            return JsonSerializer.Serialize(
                new DbOperationResult(success: false, error: "SQL statement must start with CREATE."),
                JsonOptions.Default);
        }

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            await using var cmd = new SqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();

            return JsonSerializer.Serialize(
                new DbOperationResult(success: true), JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(
                new DbOperationResult(success: false, error: ex.Message), JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "sqlsentinel_insert_data",
        Title = "Insert Data",
        ReadOnly = false,
        Destructive = false)]
    [Description("Inserts data into a table. Expects a valid INSERT SQL statement.")]
    public static async Task<string> InsertData(
        [Description("SQL Server connection string")] string connectionString,
        [Description("INSERT SQL statement")] string sql)
    {
        if (!SqlInputValidator.StartsWithKeyword(sql, "INSERT"))
        {
            return JsonSerializer.Serialize(
                new DbOperationResult(success: false, error: "SQL statement must start with INSERT."),
                JsonOptions.Default);
        }

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            await using var cmd = new SqlCommand(sql, conn);
            var rows = await cmd.ExecuteNonQueryAsync();

            return JsonSerializer.Serialize(
                new DbOperationResult(success: true, rowsAffected: rows), JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(
                new DbOperationResult(success: false, error: ex.Message), JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "sqlsentinel_read_data",
        Title = "Read Data",
        ReadOnly = true,
        Idempotent = true,
        Destructive = false)]
    [Description("Executes a SELECT query against the SQL Server database and returns the results.")]
    public static async Task<string> ReadData(
        [Description("SQL Server connection string")] string connectionString,
        [Description("SELECT SQL query to execute")] string sql)
    {
        if (!SqlInputValidator.StartsWithKeyword(sql, "SELECT", "WITH"))
        {
            return JsonSerializer.Serialize(
                new DbOperationResult(success: false, error: "SQL statement must start with SELECT or WITH (for CTEs)."),
                JsonOptions.Default);
        }

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            await using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();

            var results = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                results.Add(row);
            }

            return JsonSerializer.Serialize(
                new DbOperationResult(success: true, data: results), JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(
                new DbOperationResult(success: false, error: ex.Message), JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "sqlsentinel_update_data",
        Title = "Update Data",
        ReadOnly = false,
        Destructive = true)]
    [Description("Updates data in a table. Expects a valid UPDATE SQL statement.")]
    public static async Task<string> UpdateData(
        [Description("SQL Server connection string")] string connectionString,
        [Description("UPDATE SQL statement")] string sql)
    {
        if (!SqlInputValidator.StartsWithKeyword(sql, "UPDATE"))
        {
            return JsonSerializer.Serialize(
                new DbOperationResult(success: false, error: "SQL statement must start with UPDATE."),
                JsonOptions.Default);
        }

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            await using var cmd = new SqlCommand(sql, conn);
            var rows = await cmd.ExecuteNonQueryAsync();

            return JsonSerializer.Serialize(
                new DbOperationResult(success: true, rowsAffected: rows), JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(
                new DbOperationResult(success: false, error: ex.Message), JsonOptions.Default);
        }
    }

    [McpServerTool(Name = "sqlsentinel_drop_table",
        Title = "Drop Table",
        ReadOnly = false,
        Destructive = true)]
    [Description("Drops a table from the SQL Server database. Expects a valid DROP TABLE SQL statement.")]
    public static async Task<string> DropTable(
        [Description("SQL Server connection string")] string connectionString,
        [Description("DROP TABLE SQL statement")] string sql)
    {
        if (!SqlInputValidator.StartsWithKeyword(sql, "DROP"))
        {
            return JsonSerializer.Serialize(
                new DbOperationResult(success: false, error: "SQL statement must start with DROP."),
                JsonOptions.Default);
        }

        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            await using var cmd = new SqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();

            return JsonSerializer.Serialize(
                new DbOperationResult(success: true), JsonOptions.Default);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(
                new DbOperationResult(success: false, error: ex.Message), JsonOptions.Default);
        }
    }
}
