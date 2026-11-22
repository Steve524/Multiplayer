using System;
using System.IO;
using System.Text;
using Verse;

namespace Multiplayer.Client.Util;

internal static class MpDiagLog
{
    private const long MaxBytes = 64L * 1024 * 1024;
    private const int FlushInterval = 256;
    private static readonly object writeLock = new();

    private static StreamWriter writer;
    private static long written;
    private static int linesSinceFlush;
    private static bool started;
    private static volatile bool failed;

    public static bool Enabled => Multiplayer.settings is { diagnosticLogging: true } && !failed;

    public static void Write(string line)
    {
        if (!Enabled) return;

        lock (writeLock)
        {
            if (!Enabled) return;

            try
            {
                int bytes = GetLineSize(line);
                if (writer == null)
                    OpenWriter(rotate: !started);
                if (written + bytes > MaxBytes)
                    OpenWriter(rotate: true);

                writer.WriteLine(line);
                written += bytes;
                if (++linesSinceFlush >= FlushInterval)
                    FlushWriter();
            }
            catch (Exception e)
            {
                Disable(e);
            }
        }
    }

    public static void Close()
    {
        lock (writeLock)
        {
            if (writer == null) return;

            try
            {
                CloseWriter();
            }
            catch (Exception e)
            {
                Disable(e);
            }
        }
    }

    private static void OpenWriter(bool rotate)
    {
        CloseWriter();

        var directory = Multiplayer.LogsDir;
        Directory.CreateDirectory(directory);
        // A local arbiter can share the host's save-data directory.
        string name = Multiplayer.arbiterInstance ? "MpDiagnostics-arbiter" : "MpDiagnostics";
        string current = Path.Combine(directory, name + ".log");
        string previous = Path.Combine(directory, name + "-prev.log");
        if (rotate && File.Exists(current))
        {
            File.Delete(previous);
            File.Move(current, previous);
        }

        writer = new StreamWriter(current, append: !rotate);
        written = writer.BaseStream.Length;
        if (written == 0)
        {
            string header = $"# MpDiagnostics started {DateTime.UtcNow:O}";
            writer.WriteLine(header);
            written = GetLineSize(header);
        }
        linesSinceFlush = 0;
        started = true;
    }

    private static int GetLineSize(string line) => Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;

    private static void CloseWriter()
    {
        var activeWriter = writer;
        writer = null;
        activeWriter?.Dispose();
    }

    private static void FlushWriter()
    {
        writer.Flush();
        linesSinceFlush = 0;
    }

    private static void Disable(Exception exception)
    {
        failed = true;
        try { CloseWriter(); } catch { }
        Log.Warning($"MP: diagnostic log disabled after write failure: {exception.Message}");
    }
}
