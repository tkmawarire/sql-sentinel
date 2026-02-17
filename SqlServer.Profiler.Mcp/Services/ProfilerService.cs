using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using SqlServer.Profiler.Mcp.Models;

namespace SqlServer.Profiler.Mcp.Services;

/// <summary>
/// Service for managing SQL Server Extended Events profiling sessions.
/// </summary>
public interface IProfilerService
{
    Task<Dictionary<string, object>> CreateSessionAsync(string connectionString, SessionConfig config);
    Task<Dictionary<string, object>> StartSessionAsync(string connectionString, string sessionName);
    Task<Dictionary<string, object>> StopSessionAsync(string connectionString, string sessionName);
    Task<Dictionary<string, object>> DropSessionAsync(string connectionString, string sessionName);
    Task<List<SessionInfo>> ListSessionsAsync(string connectionString);
    Task<List<ProfilerEvent>> GetEventsAsync(string connectionString, string sessionName, EventFilters filters, List<string>? excludePatterns = null);
    Task<Dictionary<string, object>> GetConnectionInfoAsync(string connectionString, string infoType);
    Task<List<DeadlockEvent>> GetDeadlocksAsync(string connectionString, string sessionName);
    Task<List<BlockingEvent>> GetBlockingEventsAsync(string connectionString, string sessionName);
}

public record EventFilters
{
    public string? Database { get; init; }
    public string? Application { get; init; }
    public string? Login { get; init; }
    public string? TextContains { get; init; }
    public string? TextNotContains { get; init; }
    public int? MinDurationMs { get; init; }
    public DateTime? StartTime { get; init; }
    public DateTime? EndTime { get; init; }
}

public partial class ProfilerService : IProfilerService
{
    private const string SessionPrefix = "mcp_sentinel_";
    private readonly IQueryFingerprintService _fingerprintService;

    // Standard events that use the full action list and predicates
    private static readonly Dictionary<EventType, string> StandardEventMap = new()
    {
        [EventType.SqlBatchCompleted] = "sqlserver.sql_batch_completed",
        [EventType.RpcCompleted] = "sqlserver.rpc_completed",
        [EventType.SqlStatementCompleted] = "sqlserver.sql_statement_completed",
        [EventType.SpStatementCompleted] = "sqlserver.sp_statement_completed",
        [EventType.Attention] = "sqlserver.attention",
        [EventType.ErrorReported] = "sqlserver.error_reported",
        [EventType.LoginEvent] = "sqlserver.login",
        [EventType.Recompile] = "sqlserver.sql_statement_recompile",
        [EventType.AutoStats] = "sqlserver.auto_stats"
    };

    // XML-payload events: different XE definition shape (no sql_text action, no predicates)
    private static readonly Dictionary<EventType, string> XmlPayloadEventMap = new()
    {
        [EventType.Deadlock] = "sqlserver.xml_deadlock_report",
        [EventType.BlockedProcess] = "sqlserver.blocked_process_report"
    };

    // SchemaChange maps to multiple XE events
    private static readonly List<string> SchemaChangeEvents =
    [
        "sqlserver.object_altered",
        "sqlserver.object_created",
        "sqlserver.object_dropped"
    ];

    public ProfilerService(IQueryFingerprintService fingerprintService)
    {
        _fingerprintService = fingerprintService;
    }

    public async Task<Dictionary<string, object>> CreateSessionAsync(string connectionString, SessionConfig config)
    {
        var fullSessionName = $"{SessionPrefix}{config.SessionName}";

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        // Check if session already exists
        await using var checkCmd = new SqlCommand(
            "SELECT 1 FROM sys.server_event_sessions WHERE name = @name",
            conn);
        checkCmd.Parameters.AddWithValue("@name", fullSessionName);

        if (await checkCmd.ExecuteScalarAsync() != null)
        {
            throw new InvalidOperationException(
                $"Session '{config.SessionName}' already exists. Drop it first or use a different name.");
        }

        // Expand All to include every event type
        var eventTypes = config.EventTypes.Contains(EventType.All)
            ? Enum.GetValues<EventType>().Where(e => e != EventType.All).ToList()
            : config.EventTypes;

        var predicate = BuildPredicateClause(config);
        var eventDefinitions = new List<string>();

        foreach (var eventType in eventTypes)
        {
            if (eventType == EventType.SchemaChange)
            {
                foreach (var xeEventName in SchemaChangeEvents)
                {
                    eventDefinitions.Add(BuildStandardEventDef(xeEventName, predicate));
                }
            }
            else if (XmlPayloadEventMap.TryGetValue(eventType, out var xmlEventName))
            {
                eventDefinitions.Add(BuildXmlPayloadEventDef(xmlEventName));
            }
            else if (StandardEventMap.TryGetValue(eventType, out var eventName))
            {
                eventDefinitions.Add(BuildStandardEventDef(eventName, predicate));
            }
        }

        var createDdl = $"""
            CREATE EVENT SESSION [{fullSessionName}] ON SERVER
            {string.Join(",\n", eventDefinitions)}
            ADD TARGET package0.ring_buffer(
                SET max_memory = {config.RingBufferMb * 1024}
            )
            WITH (
                MAX_MEMORY = {config.RingBufferMb}MB,
                EVENT_RETENTION_MODE = ALLOW_SINGLE_EVENT_LOSS,
                MAX_DISPATCH_LATENCY = 1 SECONDS,
                STARTUP_STATE = OFF
            )
            """;

        await using var createCmd = new SqlCommand(createDdl, conn);
        await createCmd.ExecuteNonQueryAsync();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["sessionName"] = config.SessionName,
            ["fullSessionName"] = fullSessionName,
            ["message"] = $"Session '{config.SessionName}' created successfully. Use sqlsentinel_start_session to begin capture."
        };
    }

    public async Task<Dictionary<string, object>> StartSessionAsync(string connectionString, string sessionName)
    {
        var fullSessionName = $"{SessionPrefix}{sessionName}";

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            $"ALTER EVENT SESSION [{fullSessionName}] ON SERVER STATE = START",
            conn);
        await cmd.ExecuteNonQueryAsync();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["sessionName"] = sessionName,
            ["startedAt"] = DateTime.UtcNow.ToString("o"),
            ["message"] = $"Session '{sessionName}' is now capturing events."
        };
    }

    public async Task<Dictionary<string, object>> StopSessionAsync(string connectionString, string sessionName)
    {
        var fullSessionName = $"{SessionPrefix}{sessionName}";

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        await using var cmd = new SqlCommand(
            $"ALTER EVENT SESSION [{fullSessionName}] ON SERVER STATE = STOP",
            conn);
        await cmd.ExecuteNonQueryAsync();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["sessionName"] = sessionName,
            ["stoppedAt"] = DateTime.UtcNow.ToString("o"),
            ["message"] = $"Session '{sessionName}' stopped. Events are retained until session is dropped."
        };
    }

    public async Task<Dictionary<string, object>> DropSessionAsync(string connectionString, string sessionName)
    {
        var fullSessionName = $"{SessionPrefix}{sessionName}";

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        // Stop first if running
        var stopSql = $"""
            IF EXISTS (
                SELECT 1 FROM sys.dm_xe_sessions WHERE name = '{fullSessionName}'
            )
            ALTER EVENT SESSION [{fullSessionName}] ON SERVER STATE = STOP
            """;

        await using var stopCmd = new SqlCommand(stopSql, conn);
        await stopCmd.ExecuteNonQueryAsync();

        await using var dropCmd = new SqlCommand(
            $"DROP EVENT SESSION [{fullSessionName}] ON SERVER",
            conn);
        await dropCmd.ExecuteNonQueryAsync();

        return new Dictionary<string, object>
        {
            ["success"] = true,
            ["sessionName"] = sessionName,
            ["message"] = $"Session '{sessionName}' dropped. All captured events have been discarded."
        };
    }

    public async Task<List<SessionInfo>> ListSessionsAsync(string connectionString)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var query = $"""
            SELECT
                s.name as session_name,
                CASE WHEN ds.name IS NOT NULL THEN 'RUNNING' ELSE 'STOPPED' END as state,
                s.create_time,
                ISNULL(st.target_data_size, 0) as buffer_used_bytes
            FROM sys.server_event_sessions s
            LEFT JOIN sys.dm_xe_sessions ds ON s.name = ds.name
            LEFT JOIN sys.dm_xe_session_targets st ON ds.address = st.event_session_address
            WHERE s.name LIKE '{SessionPrefix}%'
            ORDER BY s.create_time DESC
            """;

        await using var cmd = new SqlCommand(query, conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        var sessions = new List<SessionInfo>();
        while (await reader.ReadAsync())
        {
            var sessionName = reader.GetString(0);
            var bufferBytes = reader.GetInt64(3);

            sessions.Add(new SessionInfo
            {
                SessionName = sessionName,
                DisplayName = sessionName.Replace(SessionPrefix, ""),
                State = reader.GetString(1),
                CreateTime = reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                BufferUsedBytes = bufferBytes,
                BufferUsedFormatted = FormatBytes(bufferBytes)
            });
        }

        return sessions;
    }

    public async Task<List<ProfilerEvent>> GetEventsAsync(
        string connectionString,
        string sessionName,
        EventFilters filters,
        List<string>? excludePatterns = null)
    {
        var fullSessionName = $"{SessionPrefix}{sessionName}";

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var query = $"""
            ;WITH EventData AS (
                SELECT
                    CAST(target_data AS XML) as event_xml
                FROM sys.dm_xe_session_targets st
                INNER JOIN sys.dm_xe_sessions s ON s.address = st.event_session_address
                WHERE s.name = '{fullSessionName}'
                AND st.target_name = 'ring_buffer'
            )
            SELECT
                event_node.value('(@name)[1]', 'varchar(100)') as event_name,
                event_node.value('(@timestamp)[1]', 'datetime2') as event_timestamp,
                event_node.value('(data[@name="duration"]/value)[1]', 'bigint') as duration_us,
                event_node.value('(data[@name="cpu_time"]/value)[1]', 'bigint') as cpu_time_us,
                event_node.value('(data[@name="logical_reads"]/value)[1]', 'bigint') as logical_reads,
                event_node.value('(data[@name="physical_reads"]/value)[1]', 'bigint') as physical_reads,
                event_node.value('(data[@name="writes"]/value)[1]', 'bigint') as writes,
                event_node.value('(data[@name="row_count"]/value)[1]', 'bigint') as row_count,
                event_node.value('(data[@name="batch_text"]/value)[1]', 'nvarchar(max)') as batch_text,
                event_node.value('(data[@name="statement"]/value)[1]', 'nvarchar(max)') as statement,
                event_node.value('(action[@name="database_name"]/value)[1]', 'nvarchar(256)') as database_name,
                event_node.value('(action[@name="client_app_name"]/value)[1]', 'nvarchar(256)') as client_app_name,
                event_node.value('(action[@name="client_hostname"]/value)[1]', 'nvarchar(256)') as client_hostname,
                event_node.value('(action[@name="server_principal_name"]/value)[1]', 'nvarchar(256)') as login_name,
                event_node.value('(action[@name="session_id"]/value)[1]', 'int') as session_id,
                event_node.value('(action[@name="transaction_id"]/value)[1]', 'bigint') as transaction_id,
                event_node.value('(action[@name="request_id"]/value)[1]', 'int') as request_id,
                event_node.value('(data[@name="result"]/text)[1]', 'varchar(50)') as result,
                event_node.value('(data[@name="error_number"]/value)[1]', 'int') as error_number,
                event_node.value('(data[@name="message"]/value)[1]', 'nvarchar(max)') as error_message,
                event_node.value('(data[@name="object_name"]/value)[1]', 'nvarchar(256)') as object_name,
                event_node.value('(action[@name="sql_text"]/value)[1]', 'nvarchar(max)') as action_sql_text
            FROM EventData
            CROSS APPLY event_xml.nodes('//RingBufferTarget/event[not(@name="xml_deadlock_report" or @name="blocked_process_report")]') AS events(event_node)
            ORDER BY event_timestamp
            """;

        await using var cmd = new SqlCommand(query, conn);
        cmd.CommandTimeout = 120; // XML parsing can be slow

        await using var reader = await cmd.ExecuteReaderAsync();

        var events = new List<ProfilerEvent>();
        while (await reader.ReadAsync())
        {
            var eventName = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var batchText = reader.IsDBNull(8) ? null : reader.GetString(8);
            var statement = reader.IsDBNull(9) ? null : reader.GetString(9);
            var objectName = reader.IsDBNull(20) ? null : reader.GetString(20);
            var actionSqlText = reader.IsDBNull(21) ? null : reader.GetString(21);

            // For schema change events, use object name if no sql text
            var sqlText = batchText ?? statement ?? actionSqlText ?? "";
            if (string.IsNullOrEmpty(sqlText) && !string.IsNullOrEmpty(objectName))
            {
                sqlText = $"{eventName}: {objectName}";
            }

            var eventTimestamp = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1);
            var databaseName = reader.IsDBNull(10) ? "" : reader.GetString(10);
            var clientAppName = reader.IsDBNull(11) ? "" : reader.GetString(11);
            var loginName = reader.IsDBNull(13) ? "" : reader.GetString(13);
            var durationUs = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);

            // Apply filters
            if (!string.IsNullOrEmpty(filters.Database) && databaseName != filters.Database)
                continue;
            if (!string.IsNullOrEmpty(filters.Application) && !clientAppName.Contains(filters.Application, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrEmpty(filters.Login) && loginName != filters.Login)
                continue;
            if (!string.IsNullOrEmpty(filters.TextContains) && !sqlText.Contains(filters.TextContains, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrEmpty(filters.TextNotContains) && sqlText.Contains(filters.TextNotContains, StringComparison.OrdinalIgnoreCase))
                continue;
            if (filters.MinDurationMs.HasValue && durationUs < filters.MinDurationMs.Value * 1000)
                continue;
            if (filters.StartTime.HasValue && eventTimestamp < filters.StartTime.Value)
                continue;
            if (filters.EndTime.HasValue && eventTimestamp > filters.EndTime.Value)
                continue;

            // Apply exclude patterns
            if (excludePatterns != null && MatchesPatterns(sqlText, excludePatterns))
                continue;

            var evt = new ProfilerEvent
            {
                EventName = eventName,
                EventTimestamp = eventTimestamp,
                DurationUs = durationUs,
                CpuTimeUs = reader.IsDBNull(3) ? 0L : reader.GetInt64(3),
                LogicalReads = reader.IsDBNull(4) ? 0L : reader.GetInt64(4),
                PhysicalReads = reader.IsDBNull(5) ? 0L : reader.GetInt64(5),
                Writes = reader.IsDBNull(6) ? 0L : reader.GetInt64(6),
                RowCount = reader.IsDBNull(7) ? 0L : reader.GetInt64(7),
                SqlText = sqlText,
                DatabaseName = databaseName,
                ClientAppName = clientAppName,
                ClientHostname = reader.IsDBNull(12) ? "" : reader.GetString(12),
                LoginName = loginName,
                SessionId = reader.IsDBNull(14) ? 0 : reader.GetInt32(14),
                TransactionId = reader.IsDBNull(15) ? null : reader.GetInt64(15),
                RequestId = reader.IsDBNull(16) ? null : reader.GetInt32(16),
                Result = reader.IsDBNull(17) ? null : reader.GetString(17),
                ErrorNumber = reader.IsDBNull(18) ? null : reader.GetInt32(18),
                ErrorMessage = reader.IsDBNull(19) ? null : reader.GetString(19),
                QueryFingerprint = _fingerprintService.GenerateFingerprint(sqlText),
                DurationFormatted = FormatDuration(durationUs)
            };

            events.Add(evt);
        }

        return events;
    }

    public async Task<List<DeadlockEvent>> GetDeadlocksAsync(string connectionString, string sessionName)
    {
        var fullSessionName = $"{SessionPrefix}{sessionName}";

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var query = $"""
            ;WITH EventData AS (
                SELECT CAST(target_data AS XML) as event_xml
                FROM sys.dm_xe_session_targets st
                INNER JOIN sys.dm_xe_sessions s ON s.address = st.event_session_address
                WHERE s.name = '{fullSessionName}'
                AND st.target_name = 'ring_buffer'
            )
            SELECT
                event_node.value('(@timestamp)[1]', 'datetime2') as event_timestamp,
                event_node.value('(data[@name="xml_report"]/value)[1]', 'nvarchar(max)') as deadlock_xml
            FROM EventData
            CROSS APPLY event_xml.nodes('//RingBufferTarget/event[@name="xml_deadlock_report"]') AS events(event_node)
            ORDER BY event_timestamp
            """;

        await using var cmd = new SqlCommand(query, conn);
        cmd.CommandTimeout = 120;

        await using var reader = await cmd.ExecuteReaderAsync();
        var deadlocks = new List<DeadlockEvent>();

        while (await reader.ReadAsync())
        {
            var timestamp = reader.IsDBNull(0) ? (DateTime?)null : reader.GetDateTime(0);
            var xmlString = reader.IsDBNull(1) ? null : reader.GetString(1);

            if (string.IsNullOrEmpty(xmlString))
                continue;

            try
            {
                var deadlock = ParseDeadlockXml(xmlString, timestamp);
                deadlocks.Add(deadlock);
            }
            catch
            {
                // Malformed or truncated XML — skip this event
                deadlocks.Add(new DeadlockEvent
                {
                    EventTimestamp = timestamp,
                    RawXml = xmlString,
                    VictimSpid = "parse_error"
                });
            }
        }

        return deadlocks;
    }

    public async Task<List<BlockingEvent>> GetBlockingEventsAsync(string connectionString, string sessionName)
    {
        var fullSessionName = $"{SessionPrefix}{sessionName}";

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var query = $"""
            ;WITH EventData AS (
                SELECT CAST(target_data AS XML) as event_xml
                FROM sys.dm_xe_session_targets st
                INNER JOIN sys.dm_xe_sessions s ON s.address = st.event_session_address
                WHERE s.name = '{fullSessionName}'
                AND st.target_name = 'ring_buffer'
            )
            SELECT
                event_node.value('(@timestamp)[1]', 'datetime2') as event_timestamp,
                event_node.value('(data[@name="blocked_process"]/value)[1]', 'nvarchar(max)') as blocking_xml
            FROM EventData
            CROSS APPLY event_xml.nodes('//RingBufferTarget/event[@name="blocked_process_report"]') AS events(event_node)
            ORDER BY event_timestamp
            """;

        await using var cmd = new SqlCommand(query, conn);
        cmd.CommandTimeout = 120;

        await using var reader = await cmd.ExecuteReaderAsync();
        var blockingEvents = new List<BlockingEvent>();

        while (await reader.ReadAsync())
        {
            var timestamp = reader.IsDBNull(0) ? (DateTime?)null : reader.GetDateTime(0);
            var xmlString = reader.IsDBNull(1) ? null : reader.GetString(1);

            if (string.IsNullOrEmpty(xmlString))
                continue;

            try
            {
                var blocking = ParseBlockingXml(xmlString, timestamp);
                blockingEvents.Add(blocking);
            }
            catch
            {
                blockingEvents.Add(new BlockingEvent
                {
                    EventTimestamp = timestamp,
                    RawXml = xmlString
                });
            }
        }

        return blockingEvents;
    }

    public async Task<Dictionary<string, object>> GetConnectionInfoAsync(string connectionString, string infoType)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        var result = new Dictionary<string, object>();

        if (infoType is "databases" or "all")
        {
            var databases = new List<ConnectionInfoItem>();
            await using var cmd = new SqlCommand("""
                SELECT name, state_desc, recovery_model_desc
                FROM sys.databases
                WHERE database_id > 4
                ORDER BY name
                """, conn);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                databases.Add(new ConnectionInfoItem
                {
                    Name = reader.GetString(0),
                    State = reader.GetString(1),
                    RecoveryModel = reader.GetString(2),
                    Count = 0
                });
            }
            result["databases"] = databases;
        }

        if (infoType is "applications" or "all")
        {
            var apps = new List<ConnectionInfoItem>();
            await using var cmd = new SqlCommand("""
                SELECT DISTINCT program_name, COUNT(*) as connection_count
                FROM sys.dm_exec_sessions
                WHERE is_user_process = 1
                AND program_name IS NOT NULL
                AND program_name != ''
                GROUP BY program_name
                ORDER BY connection_count DESC
                """, conn);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                apps.Add(new ConnectionInfoItem
                {
                    Name = reader.GetString(0),
                    Count = reader.GetInt32(1)
                });
            }
            result["applications"] = apps;
        }

        if (infoType is "logins" or "all")
        {
            var logins = new List<ConnectionInfoItem>();
            await using var cmd = new SqlCommand("""
                SELECT DISTINCT login_name, COUNT(*) as session_count
                FROM sys.dm_exec_sessions
                WHERE is_user_process = 1
                GROUP BY login_name
                ORDER BY session_count DESC
                """, conn);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                logins.Add(new ConnectionInfoItem
                {
                    Name = reader.GetString(0),
                    Count = reader.GetInt32(1)
                });
            }
            result["logins"] = logins;
        }

        if (infoType is "sessions" or "all")
        {
            var sessions = new List<ActiveSession>();
            await using var cmd = new SqlCommand("""
                SELECT
                    s.session_id,
                    s.login_name,
                    s.host_name,
                    s.program_name,
                    DB_NAME(s.database_id) as database_name,
                    s.status,
                    s.cpu_time,
                    s.reads,
                    s.writes,
                    s.login_time,
                    s.last_request_start_time
                FROM sys.dm_exec_sessions s
                WHERE s.is_user_process = 1
                ORDER BY s.last_request_start_time DESC
                """, conn);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                sessions.Add(new ActiveSession
                {
                    SessionId = reader.GetInt32(0),
                    LoginName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    HostName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    ProgramName = reader.IsDBNull(3) ? null : reader.GetString(3),
                    DatabaseName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Status = reader.IsDBNull(5) ? null : reader.GetString(5),
                    CpuTime = reader.GetInt32(6),
                    Reads = reader.GetInt64(7),
                    Writes = reader.GetInt64(8),
                    LoginTime = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                    LastRequestStartTime = reader.IsDBNull(10) ? null : reader.GetDateTime(10)
                });
            }
            result["activeSessions"] = sessions;
        }

        if (infoType is "blocking" or "all")
        {
            var blocking = new List<ActiveBlockingInfo>();
            await using var cmd = new SqlCommand("""
                SELECT
                    r.session_id as blocked_spid,
                    r.blocking_session_id as blocking_spid,
                    r.wait_type,
                    r.wait_time as wait_time_ms,
                    r.wait_resource,
                    s.login_name as blocked_login,
                    DB_NAME(r.database_id) as blocked_database,
                    SUBSTRING(t.text, (r.statement_start_offset/2)+1,
                        ((CASE r.statement_end_offset
                            WHEN -1 THEN DATALENGTH(t.text)
                            ELSE r.statement_end_offset
                        END - r.statement_start_offset)/2)+1) AS current_sql
                FROM sys.dm_exec_requests r
                INNER JOIN sys.dm_exec_sessions s ON r.session_id = s.session_id
                OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
                WHERE r.blocking_session_id > 0
                """, conn);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                blocking.Add(new ActiveBlockingInfo
                {
                    BlockedSpid = reader.GetInt16(0),
                    BlockingSpid = reader.GetInt16(1),
                    WaitType = reader.IsDBNull(2) ? null : reader.GetString(2),
                    WaitTimeMs = reader.GetInt64(3),
                    WaitResource = reader.IsDBNull(4) ? null : reader.GetString(4),
                    BlockedLoginName = reader.IsDBNull(5) ? null : reader.GetString(5),
                    BlockedDatabase = reader.IsDBNull(6) ? null : reader.GetString(6),
                    BlockedSqlText = reader.IsDBNull(7) ? null : reader.GetString(7)
                });
            }
            result["activeBlocking"] = blocking;
        }

        return result;
    }

    // ──────────────────────────────────────────────
    // XE session DDL builders
    // ──────────────────────────────────────────────

    private static string BuildStandardEventDef(string eventName, string predicate)
    {
        return $"""
            ADD EVENT {eventName}(
                ACTION(
                    sqlserver.database_name,
                    sqlserver.client_app_name,
                    sqlserver.client_hostname,
                    sqlserver.server_principal_name,
                    sqlserver.session_id,
                    sqlserver.sql_text,
                    sqlserver.transaction_id,
                    sqlserver.request_id
                )
                {predicate}
            )
            """;
    }

    private static string BuildXmlPayloadEventDef(string eventName)
    {
        return $"""
            ADD EVENT {eventName}(
                ACTION(
                    sqlserver.session_id,
                    sqlserver.server_principal_name
                )
            )
            """;
    }

    // ──────────────────────────────────────────────
    // XML parsers for deadlock and blocking events
    // ──────────────────────────────────────────────

    private static DeadlockEvent ParseDeadlockXml(string xmlString, DateTime? timestamp)
    {
        var doc = XDocument.Parse(xmlString);
        var deadlockNode = doc.Descendants("deadlock").FirstOrDefault() ?? doc.Root!;

        // Find victim process id
        var victimId = deadlockNode.Descendants("victimProcess")
            .FirstOrDefault()?.Attribute("id")?.Value ?? "";

        // Parse processes
        var processes = new List<DeadlockProcess>();
        foreach (var processNode in deadlockNode.Descendants("process"))
        {
            var processId = processNode.Attribute("id")?.Value ?? "";
            var spidStr = processNode.Attribute("spid")?.Value;
            int.TryParse(spidStr, out var spid);

            var waitTimeStr = processNode.Attribute("waittime")?.Value;
            int.TryParse(waitTimeStr, out var waitTime);

            var inputBuf = processNode.Element("inputbuf")?.Value?.Trim();

            processes.Add(new DeadlockProcess
            {
                ProcessId = processId,
                Spid = spid,
                LoginName = processNode.Attribute("loginname")?.Value,
                HostName = processNode.Attribute("hostname")?.Value,
                ApplicationName = processNode.Attribute("clientapp")?.Value,
                DatabaseName = processNode.Attribute("currentdb")?.Value,
                WaitResource = processNode.Attribute("waitresource")?.Value,
                LockMode = processNode.Attribute("lockMode")?.Value,
                WaitTime = waitTime,
                SqlText = inputBuf,
                IsVictim = processId == victimId
            });
        }

        return new DeadlockEvent
        {
            EventTimestamp = timestamp,
            VictimSpid = processes.FirstOrDefault(p => p.IsVictim)?.Spid.ToString() ?? victimId,
            Processes = processes,
            RawXml = xmlString
        };
    }

    private static BlockingEvent ParseBlockingXml(string xmlString, DateTime? timestamp)
    {
        var doc = XDocument.Parse(xmlString);
        var root = doc.Root!;

        var blockedNode = root.Descendants("blocked-process").FirstOrDefault()?.Element("process");
        var blockingNode = root.Descendants("blocking-process").FirstOrDefault()?.Element("process");

        var blockedInfo = new BlockedProcessInfo();
        if (blockedNode != null)
        {
            int.TryParse(blockedNode.Attribute("spid")?.Value, out var spid);
            int.TryParse(blockedNode.Attribute("waittime")?.Value, out var waitTime);

            blockedInfo = new BlockedProcessInfo
            {
                Spid = spid,
                WaitResource = blockedNode.Attribute("waitresource")?.Value,
                WaitTimeMs = waitTime,
                LoginName = blockedNode.Attribute("loginname")?.Value,
                HostName = blockedNode.Attribute("hostname")?.Value,
                DatabaseName = blockedNode.Attribute("currentdb")?.Value,
                SqlText = blockedNode.Element("inputbuf")?.Value?.Trim(),
                LockMode = blockedNode.Attribute("lockMode")?.Value
            };
        }

        var blockingInfo = new BlockingProcessInfo();
        if (blockingNode != null)
        {
            int.TryParse(blockingNode.Attribute("spid")?.Value, out var spid);

            blockingInfo = new BlockingProcessInfo
            {
                Spid = spid,
                LoginName = blockingNode.Attribute("loginname")?.Value,
                HostName = blockingNode.Attribute("hostname")?.Value,
                DatabaseName = blockingNode.Attribute("currentdb")?.Value,
                SqlText = blockingNode.Element("inputbuf")?.Value?.Trim(),
                Status = blockingNode.Attribute("status")?.Value
            };
        }

        return new BlockingEvent
        {
            EventTimestamp = timestamp,
            BlockedProcess = blockedInfo,
            BlockingProcess = blockingInfo,
            RawXml = xmlString
        };
    }

    // ──────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────

    private static string BuildPredicateClause(SessionConfig config)
    {
        var predicates = new List<string>();

        if (config.Databases.Count > 0)
        {
            var dbFilter = string.Join(" OR ", config.Databases.Select(db => $"[database_name]=N'{db}'"));
            predicates.Add($"({dbFilter})");
        }

        if (config.Applications.Count > 0)
        {
            var appFilter = string.Join(" OR ", config.Applications.Select(app => $"[client_app_name] LIKE N'%{app}%'"));
            predicates.Add($"({appFilter})");
        }

        if (config.Logins.Count > 0)
        {
            var loginFilter = string.Join(" OR ", config.Logins.Select(login => $"[server_principal_name]=N'{login}'"));
            predicates.Add($"({loginFilter})");
        }

        if (config.Hosts.Count > 0)
        {
            var hostFilter = string.Join(" OR ", config.Hosts.Select(host => $"[client_hostname]=N'{host}'"));
            predicates.Add($"({hostFilter})");
        }

        if (config.MinDurationMs > 0)
        {
            predicates.Add($"[duration]>={config.MinDurationMs * 1000}");
        }

        // Exclude system queries
        predicates.Add("[sqlserver].[is_system]=(0)");

        return predicates.Count > 0 ? "WHERE " + string.Join(" AND ", predicates) : "";
    }

    private static bool MatchesPatterns(string text, List<string> patterns)
    {
        if (string.IsNullOrEmpty(text) || patterns.Count == 0)
            return false;

        foreach (var pattern in patterns)
        {
            try
            {
                if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                    return true;
            }
            catch
            {
                // Invalid regex, skip
            }
        }

        return false;
    }

    public static string FormatDuration(long microseconds)
    {
        return microseconds switch
        {
            < 1000 => $"{microseconds}µs",
            < 1_000_000 => $"{microseconds / 1000.0:F2}ms",
            _ => $"{microseconds / 1_000_000.0:F2}s"
        };
    }

    public static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes}B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1}MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2}GB"
        };
    }

    public static string FormatMilliseconds(long ms)
    {
        return ms switch
        {
            < 1000 => $"{ms}ms",
            < 60_000 => $"{ms / 1000.0:F2}s",
            < 3_600_000 => $"{ms / 60_000.0:F1}min",
            _ => $"{ms / 3_600_000.0:F1}hr"
        };
    }
}
