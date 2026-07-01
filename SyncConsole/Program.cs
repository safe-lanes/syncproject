using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Serilog;
using Serilog.Events;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SyncConsole;

internal static class Program
{
    private sealed record Settings(
        string Connection,
        string CentralDb,
        string ShipDb,
        string Domain,
        string ShipImo,
        string Direction,
        string Environment,
        int Batch,
        string LogLevel
    );

    private static int PrintHelp()
    {
        Console.WriteLine(
@"Usage:
  SyncConsole.exe                      (reads appsettings.json)
  SyncConsole.exe [--overrides...]

Options:
  --connection   ""Server=127.0.0.1;User Id=root;Password=***;AllowUserVariables=true;""
  --central_db   ""client_main""
  --ship_db      ""client_9340415""
  --domain       client
  --ship_imo     9340415
  --direction    ship_to_online | online_to_ship
  --env          online | ship
  --batch        200
  --logLevel     Trace|Debug|Information|Warning|Error|Critical|None
  --diagnose     Run diagnostic tool to analyze sync issues (no actual sync)

Environment Variables:
  SAIL_SYNC_LOG_DIR    Override default log directory
  SAIL_SYNC_HEADLESS   Set to '1' to disable console output (for Node.js child_process)

Default log paths:
  Windows: C:\SAIL\Logs\Sync
  Linux:   /var/log/sail/sync (fallback: /tmp/sail/logs/sync)
  macOS:   /var/log/sail/sync (fallback: /tmp/sail/logs/sync)

Example:
  SyncConsole.exe --central_db rsms --ship_db rsms_9340415 --domain rsms --ship_imo 9340415 --direction ship_to_online --env online
");
        return 0;
    }

    private static Dictionary<string, string> ParseCli(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "--help" or "-h" or "/?")
            {
                dict["__help"] = "1";
                break;
            }
            if (a.StartsWith("--"))
            {
                var key = a[2..];
                if (key.Contains("="))
                {
                    var parts = key.Split('=', 2);
                    dict[parts[0]] = parts[1];
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    dict[key] = args[++i];
                }
                else dict[key] = "1";
            }
        }
        return dict;
    }

    private static Settings Merge(Settings? json, Dictionary<string, string> cli)
    {
        string Get(string key, string? current)
            => cli.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : (current ?? "");
        int GetInt(string key, int current)
            => cli.TryGetValue(key, out var v) && int.TryParse(v, out var val) && val > 0 ? val : current;

        var seed = json ?? new Settings("", "", "", "", "", "ship_to_online", "online", 200, "Information");

        //var seed = json ?? new Settings("Server=localhost;User Id=sailadmin;password=sailadmin;AllowUserVariables=true;", "rsms", "rsmsnew", "rsms", "9340415", "ship_to_online", "online", 200, "Information");

        //online to ship
        //var seed = json ?? new Settings("Server=localhost;User Id=sailadmin;password=sailadmin;AllowUserVariables=true;", "rsms", "rsmsnew", "rsms", "9340415", "online_to_ship", "ship", 200, "Information");

        return seed with
        {
            Connection = Get("connection", seed.Connection),
            CentralDb = Get("central_db", seed.CentralDb),
            ShipDb = Get("ship_db", seed.ShipDb),
            Domain = Get("domain", seed.Domain),
            ShipImo = Get("ship_imo", seed.ShipImo),
            Direction = Get("direction", seed.Direction),
            Environment = Get("env", seed.Environment),
            Batch = GetInt("batch", seed.Batch),
            LogLevel = Get("logLevel", seed.LogLevel)
        };
    }

    private static string[] Validate(Settings s)
    {
        var errs = new List<string>();
        if (string.IsNullOrWhiteSpace(s.Connection)) errs.Add("Connection required");
        if (string.IsNullOrWhiteSpace(s.CentralDb)) errs.Add("CentralDb required");
        if (string.IsNullOrWhiteSpace(s.ShipDb)) errs.Add("ShipDb required");
        if (string.IsNullOrWhiteSpace(s.Domain)) errs.Add("Domain required");
        if (string.IsNullOrWhiteSpace(s.ShipImo)) errs.Add("ShipImo required");
        if (!new[] { "ship_to_online", "online_to_ship" }.Contains(s.Direction, StringComparer.OrdinalIgnoreCase))
            errs.Add("Direction must be ship_to_online or online_to_ship");
        if (!new[] { "online", "ship" }.Contains(s.Environment, StringComparer.OrdinalIgnoreCase))
            errs.Add("Environment must be online or ship");
        if (s.Batch <= 0) errs.Add("Batch must be > 0");
        return errs.ToArray();
    }

    private static async Task<Settings?> LoadJsonAsync()
    {
        if (!File.Exists("appsettings.json")) return null;
        try
        {
            var text = await File.ReadAllTextAsync("appsettings.json");
            return JsonSerializer.Deserialize<Settings>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    /// <summary>
    /// Get platform-specific default log directory
    /// FIX #7: Cross-platform log paths
    /// </summary>
    private static string GetDefaultLogDirectory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return @"C:\SAIL\Logs\Sync";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                 RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Try /var/log/sail/sync first
            var varLog = "/var/log/sail/sync";
            try
            {
                Directory.CreateDirectory(varLog);
                return varLog;
            }
            catch
            {
                // Fallback to /tmp/sail/logs/sync
                return "/tmp/sail/logs/sync";
            }
        }
        else
        {
            // Unknown platform
            return Path.Combine(Path.GetTempPath(), "SAIL", "Logs", "Sync");
        }
    }

    /// <summary>
    /// Configure Serilog with cross-platform log directory support
    /// </summary>
    private static void ConfigureSerilog(LogLevel minLevel)
    {
        // Get log directory: env var → platform default → temp fallback
        var logDir = Environment.GetEnvironmentVariable("SAIL_SYNC_LOG_DIR");
        if (string.IsNullOrWhiteSpace(logDir))
        {
            logDir = GetDefaultLogDirectory();
        }

        // Ensure log directory exists
        try
        {
            Directory.CreateDirectory(logDir);
        }
        catch (Exception ex)
        {
            // Final fallback to temp directory
            var fallbackMsg = $"⚠️ Cannot create log directory '{logDir}': {ex.Message}";
            logDir = Path.Combine(Path.GetTempPath(), "SAIL", "Logs", "Sync");

            try
            {
                Directory.CreateDirectory(logDir);
                if (Environment.GetEnvironmentVariable("SAIL_SYNC_HEADLESS") != "1")
                {
                    Console.Error.WriteLine(fallbackMsg);
                    Console.Error.WriteLine($"   Using fallback: {logDir}");
                }
            }
            catch
            {
                // Last resort: current directory
                logDir = Environment.CurrentDirectory;
            }
        }

        // Convert to Serilog level
        var serilogLevel = minLevel switch
        {
            LogLevel.Trace => LogEventLevel.Verbose,
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Information => LogEventLevel.Information,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error => LogEventLevel.Error,
            LogLevel.Critical => LogEventLevel.Fatal,
            LogLevel.None => LogEventLevel.Fatal + 1,
            _ => LogEventLevel.Information
        };

        var isHeadless = Environment.GetEnvironmentVariable("SAIL_SYNC_HEADLESS") == "1";

        var loggerConfig = new LoggerConfiguration()
            .MinimumLevel.Is(serilogLevel);

        // Console sink (optional)
        if (!isHeadless && minLevel != LogLevel.None)
        {
            loggerConfig = loggerConfig.WriteTo.Console(
                outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
            );
        }

        // File sink (always enabled unless None)
        if (minLevel != LogLevel.None)
        {
            loggerConfig = loggerConfig.WriteTo.File(
                path: Path.Combine(logDir, "sync-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                shared: false,
                flushToDiskInterval: TimeSpan.FromSeconds(1)
            );
        }

        Log.Logger = loggerConfig
            .Enrich.FromLogContext()
            .CreateLogger();

        // Log startup info with platform
        if (minLevel != LogLevel.None)
        {
            var platform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" :
                          RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" :
                          RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS" : "Unknown";

            Log.Information("📁 Log directory: {LogDir} (Platform: {Platform})", logDir, platform);
            Log.Information("   Files: sync-YYYYMMDD.log (rolling daily, 30-day retention)");

            if (isHeadless)
            {
                Log.Information("🔇 Running in HEADLESS mode (console output disabled)");
            }
        }
    }

    private static async Task<int> RunDiagnosticsAsync(Settings cfg, ILogger log)
    {
        try
        {
            log.LogInformation("🔗 Connecting to MySQL for diagnostics...");
            var enhancedConnection = Db.EnhanceConnectionString(cfg.Connection);
            using var conn = new MySqlConnection(enhancedConnection);
            await conn.OpenAsync();
            await Db.SetSessionAsync(conn, log);

            await DiagnosticTool.RunDiagnosticsAsync(conn, cfg.CentralDb, cfg.ShipDb, log);

            return 0;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "❌ Diagnostics failed: {Msg}", ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunAsync(Settings cfg, ILogger log)
    {
        try
        {
            log.LogInformation("🔗 Connecting to MySQL...");
           // FIX: Enhance connection string with zero datetime support
           var enhancedConnection = Db.EnhanceConnectionString(cfg.Connection);
            using var conn = new MySqlConnection(enhancedConnection);
            await conn.OpenAsync();

            
            await Db.SetSessionAsync(conn, log);

            var targetDb = cfg.Direction == "ship_to_online" ? cfg.CentralDb : cfg.ShipDb;
            log.LogInformation("📊 Target database: {TargetDb} (direction: {Direction})", targetDb, cfg.Direction);

            log.LogInformation("🔧 Creating sync tables in {TargetDb}...", targetDb);
            await Db.EnsureSyncTablesAsync(conn, targetDb, log);
            log.LogInformation("✓ Sync tables ready");

            var tables = await Db.GetActiveTablesAsync(conn, cfg.Direction, log);
            if (tables.Count == 0)
            {
                log.LogWarning("No active tables found");
                return 0;
            }

            var engine = new SyncEngine(
                (cfg.CentralDb, cfg.ShipDb),
                cfg.Domain,
                cfg.ShipImo,
                cfg.Direction,
                cfg.Environment,
                cfg.Batch,
                conn,
                log
            );

            log.LogInformation("⚙️ Sync starting ({Count} tables)...", tables.Count);

            int totalInserts = 0, totalUpdates = 0, totalDeletes = 0, totalConflicts = 0;
            int tableIndex = 0;
            foreach (var t in tables)
            {
                tableIndex++;
                log.LogDebug("Processing table {Index}/{Total}: {Table}", tableIndex, tables.Count, t.Table);
                var result = await engine.SyncTableAsync(t.Table, t.BusinessKey);
                totalInserts += result.Inserts;
                totalUpdates += result.Updates;
                totalDeletes += result.Deletes;
                totalConflicts += result.Conflicts;
            }

            // Re-enable foreign key checks after sync completes
            await Db.EnableForeignKeyChecksAsync(conn, log);

            log.LogInformation("✅ Sync finished!");
            log.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            log.LogInformation("Domain:        {Domain}", cfg.Domain);
            log.LogInformation("Ship:          {ShipImo}", cfg.ShipImo);
            log.LogInformation("Direction:     {Direction}", cfg.Direction);
            log.LogInformation("Environment:   {Environment}", cfg.Environment);
            log.LogInformation("Tables Synced: {Count}", tables.Count);
            log.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            log.LogInformation("Records Inserted:  {Inserts}", totalInserts);
            log.LogInformation("Records Updated:   {Updates}", totalUpdates);
            log.LogInformation("Records Deleted:   {Deletes}", totalDeletes);
            log.LogInformation("Conflicts Logged:  {Conflicts}", totalConflicts);
            log.LogInformation("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            return 0;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "❌ Sync failed: {Msg}", ex.Message);
            return 1;
        }
    }

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var cli = ParseCli(args);
            if (cli.ContainsKey("__help")) return PrintHelp();

            var json = await LoadJsonAsync();
            var cfg = Merge(json, cli);

            var errors = Validate(cfg);
            if (errors.Length > 0)
            {
                var tempLevel = Enum.TryParse(cfg.LogLevel, true, out LogLevel lvl) ? lvl : LogLevel.Information;
                ConfigureSerilog(tempLevel);

                using var tempLoggerFactory = LoggerFactory.Create(builder =>
                {
                    builder.AddSerilog(dispose: false);
                    builder.SetMinimumLevel(tempLevel);
                });
                var tempLog = tempLoggerFactory.CreateLogger("SyncConsole");

                tempLog.LogError("⚠️ Configuration errors:");
                foreach (var e in errors)
                {
                    tempLog.LogError("  - {Error}", e);
                }
                tempLog.LogError("Use --help for usage information");

                return 2;
            }

            var level = Enum.TryParse(cfg.LogLevel, true, out LogLevel parsedLevel) ? parsedLevel : LogLevel.Information;
            ConfigureSerilog(level);

            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddSerilog(dispose: false);
                builder.SetMinimumLevel(level);
            });
            var log = loggerFactory.CreateLogger("SyncConsole");

            // Check for --diagnose flag
            if (cli.ContainsKey("diagnose"))
            {
                return await RunDiagnosticsAsync(cfg, log);
            }

            return await RunAsync(cfg, log);
        }
        finally
        {
            Log.CloseAndFlush();
            //Console.WriteLine("\nPress any key to exit...");
            //Console.ReadKey();
        }
    }
}