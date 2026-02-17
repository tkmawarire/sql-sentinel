using Microsoft.Data.SqlClient;
using SqlServer.Profiler.Mcp.Models;

namespace SqlServer.Profiler.Mcp.Services;

/// <summary>
/// Service for querying SQL Server wait statistics from DMVs.
/// </summary>
public interface IWaitStatsService
{
    Task<List<WaitStatEntry>> GetWaitStatsAsync(string connectionString, int topN = 20);
}

public class WaitStatsService : IWaitStatsService
{
    private static readonly Dictionary<string, string> WaitCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        // CPU
        ["SOS_SCHEDULER_YIELD"] = "CPU",
        ["THREADPOOL"] = "CPU",
        ["CXPACKET"] = "CPU",
        ["CXCONSUMER"] = "CPU",
        ["EXCHANGE"] = "CPU",

        // I/O
        ["ASYNC_IO_COMPLETION"] = "I/O",
        ["IO_COMPLETION"] = "I/O",
        ["PAGEIOLATCH_SH"] = "I/O",
        ["PAGEIOLATCH_UP"] = "I/O",
        ["PAGEIOLATCH_EX"] = "I/O",
        ["PAGEIOLATCH_DT"] = "I/O",
        ["PAGEIOLATCH_NL"] = "I/O",
        ["PAGEIOLATCH_KP"] = "I/O",
        ["WRITELOG"] = "I/O",
        ["BACKUPIO"] = "I/O",

        // Lock
        ["LCK_M_S"] = "Lock",
        ["LCK_M_X"] = "Lock",
        ["LCK_M_U"] = "Lock",
        ["LCK_M_IS"] = "Lock",
        ["LCK_M_IX"] = "Lock",
        ["LCK_M_IU"] = "Lock",
        ["LCK_M_SCH_M"] = "Lock",
        ["LCK_M_SCH_S"] = "Lock",
        ["LCK_M_SIX"] = "Lock",
        ["LCK_M_SIU"] = "Lock",
        ["LCK_M_UIX"] = "Lock",
        ["LCK_M_BU"] = "Lock",

        // Memory
        ["RESOURCE_SEMAPHORE"] = "Memory",
        ["RESOURCE_SEMAPHORE_QUERY_COMPILE"] = "Memory",
        ["CMEMTHREAD"] = "Memory",
        ["SOS_VIRTUALMEMORY_LOW"] = "Memory",

        // Network
        ["ASYNC_NETWORK_IO"] = "Network",
        ["NET_WAITFOR_PACKET"] = "Network",
    };

    private static readonly HashSet<string> BenignWaits = new(StringComparer.OrdinalIgnoreCase)
    {
        "SLEEP_TASK",
        "SLEEP_SYSTEMTASK",
        "SLEEP_DBSTARTUP",
        "SLEEP_DBRECOVER",
        "SLEEP_DBTASK",
        "SLEEP_TEMPDBSTARTUP",
        "SLEEP_MASTERDBREADY",
        "SLEEP_MASTERMDREADY",
        "SLEEP_MASTERUPGRADED",
        "SLEEP_MSDBSTARTUP",
        "WAITFOR",
        "BROKER_RECEIVE_WAITFOR",
        "BROKER_TO_FLUSH",
        "BROKER_EVENTHANDLER",
        "BROKER_TASK_STOP",
        "CHECKPOINT_QUEUE",
        "DBMIRROR_EVENTS_QUEUE",
        "DBMIRROR_WORKER_QUEUE",
        "SQLTRACE_BUFFER_FLUSH",
        "REQUEST_FOR_DEADLOCK_SEARCH",
        "RESOURCE_QUEUE",
        "SERVER_IDLE_CHECK",
        "DISPATCHER_QUEUE_SEMAPHORE",
        "XE_DISPATCHER_WAIT",
        "XE_TIMER_EVENT",
        "WAIT_XTP_OFFLINE_CKPT_NEW_LOG",
        "HADR_WORK_QUEUE",
        "HADR_FILESTREAM_IOMGR_IOCOMPLETION",
        "HADR_LOGCAPTURE_WAIT",
        "FT_IFTS_SCHEDULER_IDLE_WAIT",
        "SNI_HTTP_ACCEPT",
        "LOGMGR_QUEUE",
        "CLR_SEMAPHORE",
        "CLR_AUTO_EVENT",
        "CLR_MANUAL_EVENT",
        "LAZYWRITER_SLEEP",
        "DIRTY_PAGE_POLL",
        "SP_SERVER_DIAGNOSTICS_SLEEP",
        "SQLTRACE_INCREMENTAL_FLUSH_SLEEP",
        "QDS_PERSIST_TASK_MAIN_LOOP_SLEEP",
        "QDS_ASYNC_QUEUE",
        "QDS_CLEANUP_STALE_QUERIES_TASK_MAIN_LOOP_SLEEP",
        "ONDEMAND_TASK_QUEUE",
        "PREEMPTIVE_OS_LIBRARYOPS",
        "PREEMPTIVE_OS_COMOPS",
        "PREEMPTIVE_OS_CRYPTOPS",
        "PREEMPTIVE_OS_PIPEOPS",
        "PREEMPTIVE_OS_AUTHENTICATIONOPS",
        "PREEMPTIVE_OS_GENERICOPS",
        "PREEMPTIVE_OS_VERIFYTRUST",
        "PREEMPTIVE_OS_FILEOPS",
        "PREEMPTIVE_OS_DEVICEOPS",
        "PREEMPTIVE_OS_QUERYREGISTRY",
        "PREEMPTIVE_OS_WRITEFILE",
    };

    public async Task<List<WaitStatEntry>> GetWaitStatsAsync(string connectionString, int topN = 20)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        const string query = """
            SELECT
                wait_type,
                waiting_tasks_count,
                wait_time_ms,
                max_wait_time_ms,
                signal_wait_time_ms
            FROM sys.dm_os_wait_stats
            WHERE waiting_tasks_count > 0
              AND wait_time_ms > 0
            ORDER BY wait_time_ms DESC
            """;

        await using var cmd = new SqlCommand(query, conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        var allWaits = new List<WaitStatEntry>();
        while (await reader.ReadAsync())
        {
            var waitType = reader.GetString(0);

            // Skip benign waits
            if (BenignWaits.Contains(waitType))
                continue;

            var waitTimeMs = reader.GetInt64(2);
            var maxWaitTimeMs = reader.GetInt64(3);

            allWaits.Add(new WaitStatEntry
            {
                WaitType = waitType,
                WaitCategory = CategorizeWait(waitType),
                WaitingTasksCount = reader.GetInt64(1),
                WaitTimeMs = waitTimeMs,
                MaxWaitTimeMs = maxWaitTimeMs,
                SignalWaitTimeMs = reader.GetInt64(4),
                WaitTimeFormatted = ProfilerService.FormatMilliseconds(waitTimeMs),
                MaxWaitTimeFormatted = ProfilerService.FormatMilliseconds(maxWaitTimeMs)
            });
        }

        return allWaits.Take(topN).ToList();
    }

    private static string CategorizeWait(string waitType)
    {
        if (WaitCategories.TryGetValue(waitType, out var category))
            return category;

        // Pattern-based categorization
        if (waitType.StartsWith("LCK_", StringComparison.OrdinalIgnoreCase))
            return "Lock";
        if (waitType.StartsWith("PAGEIOLATCH_", StringComparison.OrdinalIgnoreCase))
            return "I/O";
        if (waitType.StartsWith("LATCH_", StringComparison.OrdinalIgnoreCase))
            return "Latch";
        if (waitType.StartsWith("PREEMPTIVE_", StringComparison.OrdinalIgnoreCase))
            return "Preemptive";

        return "Other";
    }
}
