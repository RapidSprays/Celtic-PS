using System.Text.Json;
using CH.Protocol;

namespace CH.Server;

/// <summary>
/// Append-only JSONL log of every application message in and out, for protocol iteration and test fixtures.
/// Login bodies are never dumped: they carry credentials, the device id and the Steam id.
/// </summary>
public sealed class FrameLog : IDisposable
{
    private static readonly HashSet<Opcode> Redacted = [Opcode.Login, Opcode.LoginAdvanced, Opcode.CreateAccount, Opcode.AccountOptionsChange, Opcode.ClearGuestAccount];

    private readonly StreamWriter? _out;
    private readonly object _gate = new();

    public FrameLog(string? directory)
    {
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, $"frames-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");
        _out = new StreamWriter(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        Path = path;
    }

    public string? Path { get; }

    public void Inbound(long connection, Opcode opcode, byte[] payload) => Write("in", connection, opcode, payload);

    public void Outbound(long connection, Opcode opcode, byte[] payload) => Write("out", connection, opcode, payload);

    private void Write(string dir, long connection, Opcode opcode, byte[] payload)
    {
        if (_out is null) return;
        var known = Enum.IsDefined(opcode);
        var line = JsonSerializer.Serialize(new
        {
            t = DateTime.UtcNow.ToString("O"),
            dir,
            conn = connection,
            op = (uint)opcode,
            name = known ? opcode.ToString() : null,
            len = payload.Length,
            hex = Redacted.Contains(opcode) ? "[REDACTED]" : Convert.ToHexString(payload),
        });
        lock (_gate) _out.WriteLine(line);
    }

    public void Dispose() => _out?.Dispose();
}
