if (args.Length != 5)
{
    throw new ArgumentException("Expected data directory, file name, ready, locked and release paths.");
}

var dataDirectory = args[0];
var fileName = args[1];
var readyPath = args[2];
var lockedPath = args[3];
var releasePath = args[4];
var deadline = DateTime.UtcNow.AddSeconds(30);

File.WriteAllText(readyPath, "ready");
FileStream? backupLock = null;
while (backupLock is null && DateTime.UtcNow < deadline)
{
    var backupDirectory = Directory.GetDirectories(dataDirectory, ".backup-*").FirstOrDefault();
    var backupFile = backupDirectory is null ? null : Path.Combine(backupDirectory, fileName);
    if (backupFile is not null && File.Exists(backupFile))
    {
        try
        {
            backupLock = File.Open(backupFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
        }
    }

    if (backupLock is null)
    {
        Thread.Sleep(2);
    }
}

if (backupLock is null)
{
    throw new TimeoutException("Timed out while locking a backup file.");
}

using (backupLock)
{
    File.WriteAllText(lockedPath, "locked");
    while (!File.Exists(releasePath) && DateTime.UtcNow < deadline)
    {
        Thread.Sleep(2);
    }

    if (!File.Exists(releasePath))
    {
        throw new TimeoutException("Timed out while waiting to release the backup file.");
    }
}

Console.WriteLine("LOCKED");
