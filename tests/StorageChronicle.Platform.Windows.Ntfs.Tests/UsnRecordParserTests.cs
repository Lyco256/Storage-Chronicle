using System.Buffers.Binary;
using System.Text;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Windows.Ntfs;
using Xunit;

namespace StorageChronicle.Platform.Windows.Ntfs.Tests;

public sealed class UsnRecordParserTests
{
    [Fact]
    public void ParsesValidRecordAndRejectsMalformedBounds()
    {
        var bytes = new byte[80];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 80);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), 2);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), 3);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), 1);
        var name = Encoding.Unicode.GetBytes("file.txt");
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(56), (short)name.Length);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(58), 64);
        name.CopyTo(bytes.AsSpan(64));
        Assert.Equal("file.txt", Assert.Single(new UsnRecordParser().Parse(bytes)).Name);
        Assert.Throws<InvalidDataException>(() => new UsnRecordParser().Parse(bytes.AsSpan(0, 70)));
    }

    [Fact]
    public void Windows10CapabilityDoesNotRequireWindows11() => Assert.True(new Windows10CapabilityDetector().IsSupported("FsctlReadUsnJournal"));
}
