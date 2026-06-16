using System.Text.Json;

namespace SyncConsole;

public sealed class SyncConfig
{
    public string Connection { get; init; } = "";        // single-connection mode
    public string CentralDb { get; init; } = "";
    public string ShipDb { get; init; } = "";

    // legacy/dual-connection (still supported if present)
    public string? Central { get; init; }
    public string? Ship { get; init; }

    public string Domain { get; init; } = "";
    public string ShipImo { get; init; } = "";
    public string Direction { get; init; } = "ship_to_online";
    public string Environment { get; init; } = "online";
    public int BatchSize { get; init; } = 200;
    public string LogLevel { get; init; } = "Information";

    public static SyncConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        var cfg = JsonSerializer.Deserialize<SyncConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new SyncConfig();

        // sanity: at least single-connection fields must exist
        if (string.IsNullOrWhiteSpace(cfg.Connection) &&
            (string.IsNullOrWhiteSpace(cfg.Central) || string.IsNullOrWhiteSpace(cfg.Ship)))
        {
            throw new InvalidOperationException("Provide either single-connection {connection, central_db, ship_db} or dual-connection {central, ship}.");
        }
        if (!string.IsNullOrWhiteSpace(cfg.Connection))
        {
            if (string.IsNullOrWhiteSpace(cfg.CentralDb) || string.IsNullOrWhiteSpace(cfg.ShipDb))
                throw new InvalidOperationException("Single-connection mode requires central_db and ship_db.");
        }
        return cfg;
    }
}
