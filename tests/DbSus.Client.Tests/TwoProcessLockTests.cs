using System.Diagnostics;
using DbSus.Client.Caching;
using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

/// <summary>
/// Locks and "in use" checks between two real processes: this test process and
/// DbSus.Client.LockHelper, started as a separate process for every scenario.
/// </summary>
public sealed class TwoProcessLockTests : IDisposable
{
    private const string Dados = "/SIHSUS/200801_/Dados";
    private readonly ClientFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DownloadAsync_LockHeldByAnotherProcess_WaitsThenProceedsWhenReleased()
    {
        var content = ClientFixture.Dbc("A1");
        _fixture.Server.Add(Dados + "/RDRR2206.dbc", content, FakeServer.Time(0));
        var client = _fixture.Client(configure: o => o.LockTimeout = TimeSpan.FromSeconds(30));
        var lockPath = EntryFile("RDRR2206.dbc", content.Length, "entry.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        using var helper = Helper.Start("lock", lockPath);
        Assert.Equal("locked", await helper.ReadLineAsync());

        var download = client.DownloadAsync(Dados + "/RDRR2206.dbc", Ct);
        await Task.Delay(500, Ct);
        Assert.False(download.IsCompleted);
        Assert.Equal(0, _fixture.Server.Transfers);

        await helper.WriteLineAsync("release");
        Assert.Equal("released", await helper.ReadLineAsync());

        Assert.Equal(content, File.ReadAllBytes(await download));
    }

    [Fact]
    public async Task DownloadAsync_LockHeldByAnotherProcess_TimesOut()
    {
        _fixture.Server.Add(Dados + "/RDRR2206.dbc", ClientFixture.Dbc("A1"), FakeServer.Time(0));
        var client = _fixture.Client(configure: o => o.LockTimeout = TimeSpan.FromMilliseconds(500));
        var lockPath = EntryFile("RDRR2206.dbc", ClientFixture.Dbc("A1").Length, "entry.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        using var helper = Helper.Start("lock", lockPath);
        Assert.Equal("locked", await helper.ReadLineAsync());

        await Assert.ThrowsAsync<TimeoutException>(() => client.DownloadAsync(Dados + "/RDRR2206.dbc", Ct));
        await helper.WriteLineAsync("release");
    }

    [Fact]
    public async Task TryAcquire_InAnotherProcess_SeesThisProcessLock()
    {
        var lockPath = Path.Combine(_fixture.Workspace, "entry.lock");

        using (await FileLocks.AcquireAsync(lockPath, TimeSpan.FromSeconds(5), Ct))
        {
            using var busy = Helper.Start("try-lock", lockPath);
            Assert.Equal("busy", await busy.ReadLineAsync());
        }

        using var free = Helper.Start("try-lock", lockPath);
        Assert.Equal("acquired", await free.ReadLineAsync());
    }

    [Fact]
    public async Task CacheCleanup_FileOpenByAReaderInAnotherProcess_IsNotRemoved()
    {
        _fixture.Server.Add(Dados + "/RDRR2206.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();
        var local = await client.DownloadAsync(Dados + "/RDRR2206.dbc", Ct);

        using (var reader = Helper.Start("open", local))
        {
            Assert.Equal("open", await reader.ReadLineAsync());
            await client.ClearCacheAsync(Ct);
            Assert.True(File.Exists(local));
            await reader.WriteLineAsync("close");
        }

        await client.ClearCacheAsync(Ct);
        Assert.False(File.Exists(local));
    }

    [Fact]
    public async Task TryDeleteIfUnused_InAnotherProcess_RespectsThisProcessReader()
    {
        var file = Path.Combine(_fixture.Workspace, "data.dbc");
        File.WriteAllBytes(file, [1, 2, 3]);

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using var inUse = Helper.Start("try-delete", file);
            Assert.Equal("in-use", await inUse.ReadLineAsync());
        }

        using var deleted = Helper.Start("try-delete", file);
        Assert.Equal("deleted", await deleted.ReadLineAsync());
        Assert.False(File.Exists(file));
    }

    private string EntryFile(string name, long size, string file) =>
        Path.Combine(_fixture.CacheDirectory, "SIHSUS", "200801_", "Dados", name, $"{size}-{FakeServer.Time(0):yyyyMMddHHmmss}", file);

    private sealed class Helper : IDisposable
    {
        private readonly Process _process;

        private Helper(Process process)
        {
            _process = process;
        }

        public static Helper Start(string command, string path)
        {
            // The helper is built next to this project, with the same configuration and framework.
            var directory = AppContext.BaseDirectory.Replace("DbSus.Client.Tests", "DbSus.Client.LockHelper");
            var dll = Path.Combine(directory, "DbSus.Client.LockHelper.dll");
            var exe = Path.Combine(directory, "DbSus.Client.LockHelper.exe");
#if NET
            var info = new ProcessStartInfo("dotnet") { ArgumentList = { dll, command, path } };
#else
            var info = new ProcessStartInfo(exe, $"{command} \"{path}\"");
#endif
            info.UseShellExecute = false;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.CreateNoWindow = true;
            return new Helper(Process.Start(info) ?? throw new InvalidOperationException("Could not start the lock helper."));
        }

        public async Task<string?> ReadLineAsync()
        {
            var line = _process.StandardOutput.ReadLineAsync();
            var finished = await Task.WhenAny(line, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
            return finished == line ? await line.ConfigureAwait(false) : throw new TimeoutException("The lock helper did not answer.");
        }

        public Task WriteLineAsync(string line) => _process.StandardInput.WriteLineAsync(line);

        public void Dispose()
        {
            try
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(10_000))
                {
                    _process.Kill();
                }
            }
            finally
            {
                _process.Dispose();
            }
        }
    }
}
