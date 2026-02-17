using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using SqlServer.Profiler.Mcp.Services;
using SqlServer.Profiler.Mcp.Tools;

var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

// Build DI container (same services as the main MCP server)
var services = new ServiceCollection();
services.AddSingleton<IProfilerService, ProfilerService>();
services.AddSingleton<IQueryFingerprintService, QueryFingerprintService>();
services.AddSingleton<SessionConfigStore>();
services.AddTransient<SessionManagementTools>();
services.AddTransient<EventRetrievalTools>();
services.AddTransient<PermissionTools>();
var provider = services.BuildServiceProvider();

// Discover all MCP tools via reflection
var toolRegistry = DiscoverTools();

if (args.Length > 0)
{
    // Script mode: run single command and exit
    var exitCode = await RunScriptMode(args);
    Environment.Exit(exitCode);
}
else
{
    // Interactive REPL mode
    await RunReplMode();
}

return;

// ---------------------------------------------------------------------------
// Tool discovery
// ---------------------------------------------------------------------------

Dictionary<string, (Type ToolType, MethodInfo Method, string FullName, string Description)> DiscoverTools()
{
    var registry = new Dictionary<string, (Type, MethodInfo, string, string)>(StringComparer.OrdinalIgnoreCase);
    var assembly = typeof(SessionManagementTools).Assembly;

    foreach (var type in assembly.GetTypes())
    {
        if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null)
            continue;

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
            if (toolAttr is null)
                continue;

            var fullName = toolAttr.Name ?? method.Name;
            var shortName = fullName.StartsWith("sqlprofiler_", StringComparison.OrdinalIgnoreCase)
                ? fullName["sqlprofiler_".Length..]
                : fullName;

            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description?.Trim() ?? "";
            // Use first line of description for the summary
            var firstLine = description.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";

            registry[shortName] = (type, method, fullName, firstLine);
        }
    }

    return registry;
}

// ---------------------------------------------------------------------------
// Script mode
// ---------------------------------------------------------------------------

async Task<int> RunScriptMode(string[] cliArgs)
{
    var command = cliArgs[0].ToLowerInvariant();

    switch (command)
    {
        case "list":
            PrintToolList();
            return 0;

        case "help" when cliArgs.Length >= 2:
            PrintToolHelp(cliArgs[1]);
            return 0;

        case "help":
            PrintUsage();
            return 0;

        case "call" when cliArgs.Length >= 2:
            return await CallTool(cliArgs[1], cliArgs[2..]);

        case "call":
            Console.Error.WriteLine("Error: 'call' requires a tool name. Use 'list' to see available tools.");
            return 1;

        default:
            Console.Error.WriteLine($"Unknown command: {command}");
            PrintUsage();
            return 1;
    }
}

// ---------------------------------------------------------------------------
// Interactive REPL mode
// ---------------------------------------------------------------------------

async Task RunReplMode()
{
    Console.WriteLine("SQL Profiler Debug CLI");
    Console.WriteLine();

    var connectionString = Environment.GetEnvironmentVariable("SQL_PROFILER_CONNECTION_STRING");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Console.Write("Connection string: ");
        connectionString = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine("No connection string provided. Exiting.");
            return;
        }
    }
    else
    {
        Console.WriteLine($"Connection: {MaskConnectionString(connectionString)}");
    }

    Console.WriteLine("Type 'list' for tools, 'help <tool>' for details, 'exit' to quit.");
    Console.WriteLine();

    while (true)
    {
        Console.Write("> ");
        var line = Console.ReadLine();
        if (line is null)
            break;

        line = line.Trim();
        if (string.IsNullOrEmpty(line))
            continue;

        if (line.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
            line.Equals("quit", StringComparison.OrdinalIgnoreCase))
            break;

        if (line.Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            PrintToolList();
            continue;
        }

        if (line.StartsWith("help ", StringComparison.OrdinalIgnoreCase))
        {
            PrintToolHelp(line[5..].Trim());
            continue;
        }

        if (line.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            PrintUsage();
            continue;
        }

        // Parse as: <tool_name> [positional args] [--param value ...]
        var parts = SplitArgs(line);
        if (parts.Length == 0)
            continue;

        var toolName = parts[0];
        var toolArgs = parts[1..];

        // Auto-inject connection string
        var argsWithConnection = InjectConnectionString(toolArgs, connectionString);
        await CallTool(toolName, argsWithConnection, replMode: true);
    }
}

// ---------------------------------------------------------------------------
// Tool invocation
// ---------------------------------------------------------------------------

async Task<int> CallTool(string toolName, string[] cliArgs, bool replMode = false)
{
    if (!toolRegistry.TryGetValue(toolName, out var entry))
    {
        Console.Error.WriteLine($"Unknown tool: {toolName}");
        Console.Error.WriteLine("Use 'list' to see available tools.");
        return 1;
    }

    var (toolType, method, fullName, _) = entry;

    try
    {
        var parameters = method.GetParameters();
        var parsedArgs = ParseArguments(parameters, cliArgs);
        var invokeArgs = BuildInvokeArgs(parameters, parsedArgs);

        var instance = provider.GetRequiredService(toolType);
        var result = method.Invoke(instance, invokeArgs);

        // Handle async methods
        if (result is Task task)
        {
            await task;
            var resultProperty = task.GetType().GetProperty("Result");
            if (resultProperty is not null)
            {
                var taskResult = resultProperty.GetValue(task);
                PrintResult(taskResult);
            }
        }
        else
        {
            PrintResult(result);
        }

        return 0;
    }
    catch (TargetInvocationException ex) when (ex.InnerException is not null)
    {
        Console.Error.WriteLine($"Error: {ex.InnerException.Message}");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

// ---------------------------------------------------------------------------
// Argument parsing
// ---------------------------------------------------------------------------

Dictionary<string, string> ParseArguments(ParameterInfo[] parameters, string[] cliArgs)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    for (var i = 0; i < cliArgs.Length; i++)
    {
        var arg = cliArgs[i];

        if (!arg.StartsWith("--"))
            continue;

        var key = arg[2..];
        var paramName = MapCliArgToParamName(key, parameters);

        // For boolean parameters, check if next arg is a value or another flag
        var matchedParam = parameters.FirstOrDefault(p =>
            p.Name?.Equals(paramName, StringComparison.OrdinalIgnoreCase) == true);

        if (matchedParam?.ParameterType == typeof(bool) || matchedParam?.ParameterType == typeof(bool?))
        {
            if (i + 1 < cliArgs.Length && !cliArgs[i + 1].StartsWith("--"))
            {
                result[paramName] = cliArgs[++i];
            }
            else
            {
                result[paramName] = "true";
            }
        }
        else if (i + 1 < cliArgs.Length)
        {
            result[paramName] = cliArgs[++i];
        }
    }

    return result;
}

string MapCliArgToParamName(string cliKey, ParameterInfo[] parameters)
{
    // Special aliases
    if (cliKey.Equals("connection", StringComparison.OrdinalIgnoreCase) ||
        cliKey.Equals("connection-string", StringComparison.OrdinalIgnoreCase))
        return "connectionString";

    if (cliKey.Equals("session-name", StringComparison.OrdinalIgnoreCase))
        return "sessionName";

    // General: remove dashes and match case-insensitively
    var normalized = cliKey.Replace("-", "");
    foreach (var param in parameters)
    {
        if (param.Name is not null &&
            param.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            return param.Name;
    }

    // Fall back to the normalized form
    return normalized;
}

object?[] BuildInvokeArgs(ParameterInfo[] parameters, Dictionary<string, string> parsedArgs)
{
    var args = new object?[parameters.Length];

    for (var i = 0; i < parameters.Length; i++)
    {
        var param = parameters[i];
        var paramType = param.ParameterType;

        if (parsedArgs.TryGetValue(param.Name!, out var rawValue))
        {
            args[i] = ConvertValue(rawValue, paramType);
        }
        else if (param.HasDefaultValue)
        {
            args[i] = param.DefaultValue;
        }
        else if (IsNullableType(paramType))
        {
            args[i] = null;
        }
        else
        {
            throw new ArgumentException($"Missing required parameter: --{ToKebabCase(param.Name!)}");
        }
    }

    return args;
}

object? ConvertValue(string raw, Type targetType)
{
    var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

    if (underlying == typeof(string))
        return raw;
    if (underlying == typeof(int))
        return int.Parse(raw);
    if (underlying == typeof(long))
        return long.Parse(raw);
    if (underlying == typeof(bool))
        return bool.Parse(raw);
    if (underlying == typeof(double))
        return double.Parse(raw);

    return raw;
}

bool IsNullableType(Type type)
{
    return !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
}

string[] InjectConnectionString(string[] cliArgs, string connectionString)
{
    // If --connection or --connection-string already present, return as-is
    foreach (var arg in cliArgs)
    {
        if (arg.Equals("--connection", StringComparison.OrdinalIgnoreCase) ||
            arg.Equals("--connection-string", StringComparison.OrdinalIgnoreCase))
            return cliArgs;
    }

    return [..cliArgs, "--connection", connectionString];
}

// ---------------------------------------------------------------------------
// Display helpers
// ---------------------------------------------------------------------------

void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  SqlServer.Profiler.Mcp.Cli list                         List all available tools");
    Console.WriteLine("  SqlServer.Profiler.Mcp.Cli help <tool>                  Show tool parameter details");
    Console.WriteLine("  SqlServer.Profiler.Mcp.Cli call <tool> [--param value]  Invoke a tool");
    Console.WriteLine();
    Console.WriteLine("Environment:");
    Console.WriteLine("  SQL_PROFILER_CONNECTION_STRING   Default connection string");
    Console.WriteLine();
    Console.WriteLine("Run with no arguments for interactive REPL mode.");
}

void PrintToolList()
{
    Console.WriteLine($"Available tools ({toolRegistry.Count}):");
    var maxName = toolRegistry.Keys.Max(k => k.Length);
    foreach (var (shortName, (_, _, _, desc)) in toolRegistry.OrderBy(x => x.Key))
    {
        Console.WriteLine($"  {shortName.PadRight(maxName + 2)}{desc}");
    }
}

void PrintToolHelp(string toolName)
{
    if (!toolRegistry.TryGetValue(toolName, out var entry))
    {
        Console.Error.WriteLine($"Unknown tool: {toolName}");
        return;
    }

    var (_, method, fullName, _) = entry;

    // Full description
    var desc = method.GetCustomAttribute<DescriptionAttribute>()?.Description?.Trim() ?? "No description.";

    Console.WriteLine(fullName);
    Console.WriteLine($"  {desc.Replace("\n", "\n  ")}");
    Console.WriteLine();
    Console.WriteLine("Parameters:");

    foreach (var param in method.GetParameters())
    {
        var paramDesc = param.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
        var typeName = FormatTypeName(param.ParameterType);
        var required = !param.HasDefaultValue && !IsNullableType(param.ParameterType);
        var defaultStr = param.HasDefaultValue && param.DefaultValue is not null
            ? $", default: {param.DefaultValue}"
            : "";
        var reqStr = required ? ", required" : "";

        Console.WriteLine($"  --{ToKebabCase(param.Name!)} ({typeName}{reqStr}{defaultStr})");
        if (!string.IsNullOrWhiteSpace(paramDesc))
            Console.WriteLine($"      {paramDesc}");
    }
}

void PrintResult(object? result)
{
    if (result is null)
        return;

    var str = result.ToString() ?? "";

    // Try to pretty-print if it looks like JSON
    try
    {
        using var doc = JsonDocument.Parse(str);
        Console.WriteLine(JsonSerializer.Serialize(doc, jsonOptions));
    }
    catch
    {
        Console.WriteLine(str);
    }
}

string FormatTypeName(Type type)
{
    var underlying = Nullable.GetUnderlyingType(type);
    if (underlying is not null)
        return $"{underlying.Name.ToLowerInvariant()}?";
    return type.Name.ToLowerInvariant();
}

string ToKebabCase(string camelCase)
{
    var result = new System.Text.StringBuilder();
    for (var i = 0; i < camelCase.Length; i++)
    {
        var c = camelCase[i];
        if (char.IsUpper(c) && i > 0)
        {
            result.Append('-');
            result.Append(char.ToLowerInvariant(c));
        }
        else
        {
            result.Append(char.ToLowerInvariant(c));
        }
    }
    return result.ToString();
}

string MaskConnectionString(string cs)
{
    // Mask password in connection string for display
    var masked = System.Text.RegularExpressions.Regex.Replace(
        cs, @"(Password|Pwd)\s*=\s*[^;]+", "$1=***", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    return masked;
}

string[] SplitArgs(string line)
{
    // Simple shell-like splitting that respects double quotes
    var args = new List<string>();
    var current = new System.Text.StringBuilder();
    var inQuotes = false;

    for (var i = 0; i < line.Length; i++)
    {
        var c = line[i];
        if (c == '"')
        {
            inQuotes = !inQuotes;
        }
        else if (c == ' ' && !inQuotes)
        {
            if (current.Length > 0)
            {
                args.Add(current.ToString());
                current.Clear();
            }
        }
        else
        {
            current.Append(c);
        }
    }

    if (current.Length > 0)
        args.Add(current.ToString());

    return args.ToArray();
}
