// Downloads the pinned DATASUS samples listed in tests/data/expected.json into tests/data/local/.
//
// Usage, from anywhere inside the repository:
//   dotnet run tools/fetch-samples/fetch-samples.cs
//
// Only entries with a "source" path are fetched. Each download must match the pinned size and
// SHA-256; DATASUS republishes files, so on a mismatch the tool reports it and stops. It never
// updates expected.json: new hashes come only from the oracle in tools/oracle.

// FtpWebRequest is obsolete but still the only FTP client in the BCL, and this is a dev tool.
#pragma warning disable SYSLIB0014

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

var root = FindRepositoryRoot(Directory.GetCurrentDirectory());
var expectedPath = Path.Combine(root, "tests", "data", "expected.json");
var targetDirectory = Path.Combine(root, "tests", "data", "local");
Directory.CreateDirectory(targetDirectory);

using var document = JsonDocument.Parse(File.ReadAllText(expectedPath));
var host = document.RootElement.GetProperty("ftpHost").GetString();

foreach (var entry in document.RootElement.GetProperty("files").EnumerateArray())
{
    var file = entry.GetProperty("file").GetString()!;
    if (!entry.TryGetProperty("source", out var sourceElement))
    {
        Console.WriteLine($"skip   {file} (no FTP source pinned)");
        continue;
    }

    var expectedSha256 = entry.GetProperty("dbcSha256").GetString()!;
    var expectedSize = entry.GetProperty("dbcSize").GetInt64();
    var target = Path.Combine(targetDirectory, file);

    if (File.Exists(target))
    {
        if (Matches(target, expectedSize, expectedSha256, out var existingDescription))
        {
            Console.WriteLine($"ok     {file} (already present)");
            continue;
        }

        return Fail($"{file} already exists in tests/data/local but does not match the pinned sample ({existingDescription}). Remove it to download again.");
    }

    var url = $"ftp://{host}{sourceElement.GetString()}";
    var temporary = target + ".download";
    try
    {
        Download(url, temporary);
        if (!Matches(temporary, expectedSize, expectedSha256, out var downloadedDescription))
        {
            File.Delete(temporary);
            return Fail($"{file} on the FTP no longer matches the pinned sample ({downloadedDescription}). DATASUS may have republished it; re-run the oracle and review before changing expected.json.");
        }

        File.Move(temporary, target);
        Console.WriteLine($"fetch  {file} ({expectedSize.ToString("N0", CultureInfo.InvariantCulture)} bytes)");
    }
    catch (WebException exception)
    {
        File.Delete(temporary);
        return Fail($"{file}: download from {url} failed: {exception.Message}");
    }
}

return 0;

static bool Matches(string path, long expectedSize, string expectedSha256, out string description)
{
    var size = new FileInfo(path).Length;
    string sha256;
    using (var stream = File.OpenRead(path))
    {
        sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    description = $"expected {expectedSize} bytes / {expectedSha256}, got {size} bytes / {sha256}";
    return size == expectedSize && sha256 == expectedSha256;
}

static void Download(string url, string destination)
{
    var request = (FtpWebRequest)WebRequest.Create(url);
    request.Method = WebRequestMethods.Ftp.DownloadFile;
    request.UseBinary = true;
    request.Credentials = new NetworkCredential("anonymous", "anonymous");

    using var response = (FtpWebResponse)request.GetResponse();
    using var body = response.GetResponseStream();
    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
    body.CopyTo(output);
}

static string FindRepositoryRoot(string start)
{
    for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "DbSus.slnx")))
        {
            return directory.FullName;
        }
    }

    throw new InvalidOperationException("Run this tool from inside the DbSus repository.");
}

static int Fail(string message)
{
    Console.Error.WriteLine($"error: {message}");
    return 1;
}
