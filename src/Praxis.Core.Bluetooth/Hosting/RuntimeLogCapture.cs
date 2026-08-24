using System.Text;

namespace Praxis.Core.Hosting;

internal sealed class RuntimeLogCapture : IDisposable
{
    private const long MaximumLogBytes = 5 * 1024 * 1024;
    private readonly TextWriter _originalOut;
    private readonly TextWriter _originalError;
    private readonly StreamWriter _file;
    private bool _disposed;

    private RuntimeLogCapture(TextWriter originalOut, TextWriter originalError, StreamWriter file)
    {
        _originalOut = originalOut;
        _originalError = originalError;
        _file = file;
    }

    public static RuntimeLogCapture Start(string dataDirectory)
    {
        var logDirectory = Path.Combine(dataDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        var path = Path.Combine(logDirectory, "runtime.log");
        var previousPath = Path.Combine(logDirectory, "runtime.previous.log");

        if (File.Exists(path) && new FileInfo(path).Length >= MaximumLogBytes)
        {
            File.Move(path, previousPath, overwrite: true);
        }

        var originalOut = Console.Out;
        var originalError = Console.Error;
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var file = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        var writer = TextWriter.Synchronized(new TeeTextWriter(originalOut, file));
        Console.SetOut(writer);
        Console.SetError(writer);
        return new RuntimeLogCapture(originalOut, originalError, file);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        _file.Dispose();
    }

    private sealed class TeeTextWriter(TextWriter first, TextWriter second) : TextWriter
    {
        public override Encoding Encoding => second.Encoding;

        public override void Write(char value)
        {
            first.Write(value);
            second.Write(value);
        }

        public override void Write(string? value)
        {
            first.Write(value);
            second.Write(value);
        }

        public override void WriteLine(string? value)
        {
            first.WriteLine(value);
            second.WriteLine(value);
        }

        public override void Flush()
        {
            first.Flush();
            second.Flush();
        }
    }
}
