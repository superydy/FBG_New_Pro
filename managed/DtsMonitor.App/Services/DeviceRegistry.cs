using System.IO;
using System.Text.Json;
using DtsMonitor.App.Models;

namespace DtsMonitor.App.Services;

public sealed class DeviceRegistry
{
    private const int SchemaVersion = 1;
    private readonly string _devicesPath;

    public DeviceRegistry(string devicesPath)
    {
        _devicesPath = devicesPath;
    }

    public IReadOnlyList<DeviceDefinition> LoadOrCreateDefault(string legacyUiStatePath)
    {
        if (File.Exists(_devicesPath))
        {
            return Load();
        }

        string root = Path.GetDirectoryName(_devicesPath) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(root);

        DeviceDefinition device = new()
        {
            DeviceId = "default",
            Name = "设备1",
            DbPath = Path.Combine(root, "hg_fbg_monitor_default.db"),
            UiStatePath = Path.Combine(root, "ui_state_default.json"),
            WorkerPipeName = "hg_fbg_worker_default",
            CreatedUtc = DateTime.UtcNow,
            LastModifiedUtc = DateTime.UtcNow
        };

        if (File.Exists(legacyUiStatePath))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(legacyUiStatePath));
                if (document.RootElement.TryGetProperty("Ip", out JsonElement ipElement))
                {
                    device.Ip = ipElement.GetString() ?? string.Empty;
                }

                if (!File.Exists(device.UiStatePath))
                {
                    File.Copy(legacyUiStatePath, device.UiStatePath, overwrite: true);
                }
            }
            catch
            {
            }
        }

        Save(new[] { device });
        return new[] { device };
    }

    public IReadOnlyList<DeviceDefinition> Load()
    {
        try
        {
            DevicesSnapshot? snapshot = JsonSerializer.Deserialize<DevicesSnapshot>(File.ReadAllText(_devicesPath));
            if (snapshot?.Devices is { Count: > 0 })
            {
                return snapshot.Devices;
            }
        }
        catch
        {
        }

        return Array.Empty<DeviceDefinition>();
    }

    public void Save(IEnumerable<DeviceDefinition> devices)
    {
        string? dir = Path.GetDirectoryName(_devicesPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        DevicesSnapshot snapshot = new()
        {
            SchemaVersion = SchemaVersion,
            Devices = devices.ToList()
        };

        string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(_devicesPath, json);
    }
}
