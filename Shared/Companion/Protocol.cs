using System;
using System.IO;
using System.Text;

namespace Shared.Companion;

[Flags]
public enum CompanionCapabilities : ulong
{
    None = 0, SharedProfiles = 1, Transfers = 2, RefineryAutomation = 4,
    ComponentAutomation = 8, LoadoutAutomation = 16, UtilityJobs = 32
}

public enum MessageKind : byte { Hello, HelloAck, GetProfile, PublishProfile, Result, ProfileChanged }
public enum ResultCode : byte
{
    Ok, NotFound, Denied, Conflict, Invalid, Busy, Expired, Unavailable, UnknownOutcome
}

public sealed class CompanionMessage
{
    public MessageKind Kind;
    public Guid Epoch;
    public Guid RequestId;
    public long DeadlineUtcTicks;
    public long AnchorEntityId;
    public long TerminalEntityId;
    public long Revision;
    public Guid ProfileId;
    public CompanionCapabilities Capabilities;
    public ResultCode Code;
    public byte[] Body = Array.Empty<byte>();
}

public static class CompanionProtocol
{
    public const ushort Channel = 48763;
    public const uint Magic = 0x55534350; // USCP
    public const ushort Version = 1;
    public const int MaxBodyBytes = 48 * 1024;
    public const int HeaderBytes = 100;
    public const int MaxPacketBytes = MaxBodyBytes + HeaderBytes;
    public const int RequestLifetimeSeconds = 60;

    public static byte[] Encode(CompanionMessage message)
    {
        if (message.Body == null || message.Body.Length > MaxBodyBytes)
            throw new InvalidDataException("Companion payload too large.");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Magic); writer.Write(Version); writer.Write((byte)message.Kind);
        writer.Write(message.Epoch.ToByteArray()); writer.Write(message.RequestId.ToByteArray());
        writer.Write(message.DeadlineUtcTicks); writer.Write(message.AnchorEntityId);
        writer.Write(message.TerminalEntityId); writer.Write(message.Revision);
        writer.Write(message.ProfileId.ToByteArray()); writer.Write((ulong)message.Capabilities);
        writer.Write((byte)message.Code); writer.Write(message.Body.Length); writer.Write(message.Body);
        return stream.ToArray();
    }

    public static bool TryDecode(byte[] bytes, out CompanionMessage message)
    {
        message = null;
        if (bytes == null || bytes.Length < HeaderBytes || bytes.Length > MaxPacketBytes) return false;
        try
        {
            using var stream = new MemoryStream(bytes, false);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != Magic || reader.ReadUInt16() != Version) return false;
            var value = new CompanionMessage
            {
                Kind = (MessageKind)reader.ReadByte(), Epoch = new Guid(reader.ReadBytes(16)),
                RequestId = new Guid(reader.ReadBytes(16)), DeadlineUtcTicks = reader.ReadInt64(),
                AnchorEntityId = reader.ReadInt64(), TerminalEntityId = reader.ReadInt64(),
                Revision = reader.ReadInt64(), ProfileId = new Guid(reader.ReadBytes(16)),
                Capabilities = (CompanionCapabilities)reader.ReadUInt64(), Code = (ResultCode)reader.ReadByte()
            };
            var length = reader.ReadInt32();
            if (!Enum.IsDefined(typeof(MessageKind), value.Kind) || !Enum.IsDefined(typeof(ResultCode), value.Code) ||
                length < 0 || length > MaxBodyBytes || length != stream.Length - stream.Position) return false;
            value.Body = reader.ReadBytes(length);
            message = value;
            return true;
        }
        catch (Exception e) when (e is IOException || e is ArgumentException) { return false; }
    }
}
