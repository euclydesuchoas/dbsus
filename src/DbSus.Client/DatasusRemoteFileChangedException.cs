using System.Diagnostics.CodeAnalysis;
using DbSus.Client.Transport;

namespace DbSus.Client;

/// <summary>
/// The file changed on the server (its size or its modification time) while it was being read,
/// so the bytes already read and those that would follow belong to different versions. Internal
/// until reading without a download becomes public.
/// </summary>
[SuppressMessage("Design", "CA1032", Justification = "Created only by the client with both versions.")]
[SuppressMessage("Design", "CA1064", Justification = "Internal until reading without a download becomes public, with its API reviewed.")]
internal sealed class DatasusRemoteFileChangedException : DatasusTransferException
{
    internal DatasusRemoteFileChangedException(string message, RemoteFileInfo expected, RemoteFileInfo current)
        : base(message, ftpReplyCode: null, innerException: null)
    {
        Expected = expected;
        Current = current;
    }

    /// <summary>Gets the version the read started with.</summary>
    public RemoteFileInfo Expected { get; }

    /// <summary>Gets the version the server has now.</summary>
    public RemoteFileInfo Current { get; }
}
