using System.Text.Json;

namespace DtsMonitor.App.Models;

public static class DeviceIpcProtocol
{
    public const string Version = "1";
}

public sealed class DeviceIpcMessage
{
    public string ProtocolVersion { get; set; } = DeviceIpcProtocol.Version;
    public string MessageType { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public long TimestampMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public JsonElement? Payload { get; set; }
}

public sealed class CommandResultPayload<T>
{
    public bool Success { get; set; }
    public int ErrorCode { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public T? Payload { get; set; }
}

public sealed class EmptyPayload
{
}

public sealed class ConnectCommandPayload
{
    public string Ip { get; set; } = string.Empty;
    public bool Use1GInit { get; set; } = true;
}

public sealed class RunCommandPayload
{
    public int Channel { get; set; }
    public bool AllChannelsLowSpeed { get; set; }
}

public sealed class ChannelCommandPayload
{
    public int Channel { get; set; }
}

public sealed class CalibrationCommandPayload
{
    public int Channel { get; set; }
    public float Threshold { get; set; }
}

public sealed class SpectrumSensorCommandPayload
{
    public int SensorIndex { get; set; }
}

public sealed class AmplifierCommandPayload
{
    public int EdfaCurrentMa { get; set; }
    public int EdfaPaCurrentMa { get; set; }
}

public sealed class ThresholdCommandPayload
{
    public float Threshold { get; set; }
}

public sealed class StatusResponsePayload
{
    public DeviceStatusSnapshot Status { get; set; } = new();
}
