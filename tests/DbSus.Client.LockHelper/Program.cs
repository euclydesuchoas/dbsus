// Second process for DbSus.Client.Tests.TwoProcessLockTests: uses the client's own lock primitives
// from another process, so the tests exercise real cross-process behaviour (share modes on Windows,
// flock on Linux and macOS).
//
//   lock <lock file>      take the exclusive lock, print "locked", hold it until a line arrives on stdin
//   try-lock <lock file>  print "acquired" or "busy" and exit
//   open <data file>      open the file as a reader does, print "open", keep it until a line on stdin
//   try-delete <file>     print "deleted" or "in-use" and exit
using DbSus.Client.Caching;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: lock|try-lock|open|try-delete <path>");
    return 2;
}

var (command, path) = (args[0], args[1]);
switch (command)
{
    case "lock":
        using (await FileLocks.AcquireAsync(path, TimeSpan.FromSeconds(30), CancellationToken.None))
        {
            Console.WriteLine("locked");
            Console.Out.Flush();
            Console.In.ReadLine();
        }

        Console.WriteLine("released");
        return 0;

    case "try-lock":
        using (var held = FileLocks.TryAcquire(path))
        {
            Console.WriteLine(held is null ? "busy" : "acquired");
        }

        return 0;

    case "open":
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Console.WriteLine("open");
            Console.Out.Flush();
            Console.In.ReadLine();
        }

        return 0;

    case "try-delete":
        Console.WriteLine(FileLocks.TryDeleteIfUnused(path) ? "deleted" : "in-use");
        return 0;

    default:
        Console.Error.WriteLine($"unknown command '{command}'");
        return 2;
}
