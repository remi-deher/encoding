using System.Security.Cryptography;

namespace Encoding.NodeAgent.Runtime;

public static class SafeMediaPaths
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,byte> ActiveLocks=new(StringComparer.OrdinalIgnoreCase);
    public static string Resolve(string root,string relative)
    {
        var segments=Encoding.Contracts.Libraries.MediaPathRules.Normalize(relative).Split('/');
        var directory=Path.GetFullPath(root);
        if(new DirectoryInfo(directory).LinkTarget is not null) throw new IOException("Le point d’accès ne doit pas être un lien symbolique.");
        var current=directory;
        foreach(var segment in segments)
        {
            current=Path.Combine(current,segment);
            if(File.Exists(current) && new FileInfo(current).LinkTarget is not null || Directory.Exists(current) && new DirectoryInfo(current).LinkTarget is not null)
                throw new IOException("Les liens symboliques ne sont pas acceptés dans un chemin média.");
        }
        return current;
    }
    public static async Task<string> DigestAsync(string path,CancellationToken ct)
    {
        await using var file=File.OpenRead(path); return Convert.ToHexStringLower(await SHA256.HashDataAsync(file,ct));
    }
    public sealed class MediaLockBusyException() : IOException("Ce média est déjà utilisé par un autre traitement.");
    private sealed class MediaLock(string path,FileStream stream) : IDisposable
    {
        public void Dispose() { stream.Dispose(); ActiveLocks.TryRemove(path,out _); }
    }
    public static IDisposable Lock(string path)
    {
        if(OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Le verrouillage média requiert Linux ou Windows.");
        if(!ActiveLocks.TryAdd(path,0)) throw new MediaLockBusyException();
        FileStream stream;
        try { stream=new FileStream(path+".encoding.lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.ReadWrite); }
        catch { ActiveLocks.TryRemove(path,out _); throw; }
        try { stream.Lock(0,1); return new MediaLock(path,stream); }
        catch(IOException) { stream.Dispose(); ActiveLocks.TryRemove(path,out _); throw new MediaLockBusyException(); }
        catch { stream.Dispose(); ActiveLocks.TryRemove(path,out _); throw; }
    }
}
