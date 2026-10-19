using System;
using System.IO;

// Append-only diagnostics log next to settings.ini; rotated once it passes MaxBytes.
static class Log
{
    const long MaxBytes = 256 * 1024;
    static readonly object Lock = new object();
    public static readonly string FilePath = Path.Combine(Settings.Dir, "log.txt");

    public static void Info(string message) { Write("INFO ", message); }

    public static void Error(string context, Exception ex)
    {
        Write("ERROR", context + ": " + (ex != null ? ex.ToString() : "(unknown)"));
    }

    static void Write(string level, string message)
    {
        lock (Lock)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var old = FilePath + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(FilePath, old);
                }
                File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + level + " " + message + Environment.NewLine);
            }
            catch { }   // logging must never take the app down
        }
    }
}
