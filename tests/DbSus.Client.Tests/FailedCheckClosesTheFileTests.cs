using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

// The check of a downloaded .dbc closes the file on every way out, the opening that fails included:
// the file is then deleted, and on Windows a handle left open keeps it. It would stay, whole, and the
// next download would find every byte there, transfer nothing and fail the check again. On Linux a
// file open can be deleted, so what these tests see of a leak is Windows'; DbcFileTests shows, on
// every system, that OpenAsDbf disposes the stream of an opening that fails.
public sealed class FailedCheckClosesTheFileTests : IDisposable
{
    private const string FilePath = "/X/RDMG2407.dbc";
    private static readonly byte[] Good = ClientFixture.Dbc([.. Enumerable.Range(0, 100).Select(i => i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))]);
    private readonly ClientFixture _fixture = new();

    public enum Damage
    {
        // The opening fails: the length of the header is below the minimum.
        HeaderTooShort,

        // The opening fails: the header would go beyond the end of the file.
        HeaderBeyondTheFile,

        // The opening passes, and the reading fails.
        CompressedData,

        // The opening and the reading pass, and the CRC-32 at the end does not match.
        Checksum,
    }

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData(Damage.HeaderTooShort, true)]
    [InlineData(Damage.HeaderTooShort, false)]
    [InlineData(Damage.HeaderBeyondTheFile, true)]
    [InlineData(Damage.HeaderBeyondTheFile, false)]
    [InlineData(Damage.CompressedData, true)]
    [InlineData(Damage.CompressedData, false)]
    [InlineData(Damage.Checksum, true)]
    [InlineData(Damage.Checksum, false)]
    public async Task FirstDownloadFailsTheCheck_TheFileIsDeleted_AndTheSecondDownloadEndsWithTheFinalFile(Damage damage, bool cache)
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.Server.Add(FilePath, Damaged(damage));
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer == 2)
            {
                _fixture.Server.Add(FilePath, Good);
            }
        };

        var path = await _fixture.Client(cache).DownloadAsync(FilePath, ct);

        Assert.Equal([0L, 0L], _fixture.Server.TransferOffsets);
        Assert.Equal(Good, File.ReadAllBytes(path));
        Assert.Empty(PartialFiles());
    }

    [Theory]
    [InlineData(Damage.HeaderTooShort, true)]
    [InlineData(Damage.HeaderTooShort, false)]
    [InlineData(Damage.HeaderBeyondTheFile, true)]
    [InlineData(Damage.HeaderBeyondTheFile, false)]
    [InlineData(Damage.CompressedData, true)]
    [InlineData(Damage.CompressedData, false)]
    [InlineData(Damage.Checksum, true)]
    [InlineData(Damage.Checksum, false)]
    public async Task BothDownloadsFailTheCheck_NothingIsLeft_AndTheNextCallDownloadsAgain(Damage damage, bool cache)
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.Server.Add(FilePath, Damaged(damage));
        var client = _fixture.Client(cache);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(FilePath, ct));

        Assert.Equal([0L, 0L], _fixture.Server.TransferOffsets);
        Assert.Empty(PartialFiles());

        _fixture.Server.Add(FilePath, Good);
        Assert.Equal(Good, File.ReadAllBytes(await client.DownloadAsync(FilePath, ct)));
    }

    private static byte[] Damaged(Damage damage)
    {
        Assert.True(Good.Length < 0xFFFF, Good.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var bytes = (byte[])Good.Clone();
        var compressed = (Good[8] | (Good[9] << 8)) + 4;
        switch (damage)
        {
            case Damage.HeaderTooShort:
                bytes[8] = 0;
                bytes[9] = 0;
                break;
            case Damage.HeaderBeyondTheFile:
                bytes[8] = 0xFF;
                bytes[9] = 0xFF;
                break;
            case Damage.CompressedData:
                bytes[compressed] = 0xFF;
                bytes[compressed + 1] = 0xFF;
                break;
            case Damage.Checksum:
                bytes[compressed - 1] ^= 0xFF;
                break;
        }

        return bytes;
    }

    private List<string> PartialFiles() =>
        Directory.Exists(_fixture.Workspace) ? [.. Directory.EnumerateFiles(_fixture.Workspace, "*.partial", SearchOption.AllDirectories)] : [];
}
