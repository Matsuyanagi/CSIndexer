using System.Text;

namespace CsIndex.Cli;

internal sealed class OutputException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed class OutputDestination : IDisposable
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string? _destinationPath;
    private readonly TextWriter? _standardOutputWriter;
    private readonly Func<Stream, TextWriter>? _writerFactory;
    private TextWriter? _innerWriter;
    private FileStream? _stream;
    private string? _temporaryPath;
    private TextWriter? _writer;
    private bool _committed;
    private bool _disposed;

    private OutputDestination(
        string? destinationPath,
        TextWriter? standardOutputWriter,
        Func<Stream, TextWriter>? writerFactory)
    {
        _destinationPath = destinationPath;
        _standardOutputWriter = standardOutputWriter;
        _writerFactory = writerFactory;
    }

    public TextWriter Writer
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_destinationPath is null)
            {
                return _standardOutputWriter!;
            }

            return _writer ??= OpenTemporaryWriter();
        }
    }

    public static OutputDestination Create(string? outputPath, string databasePath) =>
        Create(outputPath, databasePath, CreateStreamWriter);

    internal static OutputDestination Create(
        string? outputPath,
        string databasePath,
        Func<Stream, TextWriter> writerFactory)
    {
        ArgumentNullException.ThrowIfNull(databasePath);
        ArgumentNullException.ThrowIfNull(writerFactory);
        if (outputPath is null)
        {
            return new OutputDestination(destinationPath: null, Console.Out, writerFactory: null);
        }

        var destinationPath = ResolveFullPath(outputPath, "output");
        var normalizedDatabasePath = ResolveFullPath(databasePath, "database");
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(destinationPath, normalizedDatabasePath, comparison))
        {
            throw new CliUsageException("Output file path must not match the active database path.");
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (directory is null || !Directory.Exists(directory))
        {
            throw new OutputException($"Output directory does not exist: {directory ?? destinationPath}");
        }

        return new OutputDestination(destinationPath, standardOutputWriter: null, writerFactory);
    }

    public void Commit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed || _destinationPath is null)
        {
            return;
        }

        _ = Writer;
        try
        {
            _innerWriter!.Flush();
            _stream!.Flush(flushToDisk: true);
            CloseTemporaryWriter();

            if (File.Exists(_destinationPath))
            {
                File.Replace(_temporaryPath!, _destinationPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(_temporaryPath!, _destinationPath);
            }

            _temporaryPath = null;
            _writer = null;
            _committed = true;
        }
        catch (OutputException)
        {
            var cleanupFailure = CleanupTemporaryFile();
            if (cleanupFailure is not null)
            {
                throw new OutputException(
                    $"Output failed and temporary-file cleanup also failed: {cleanupFailure.Message}",
                    cleanupFailure);
            }

            throw;
        }
        catch (Exception exception) when (IsOutputIoException(exception))
        {
            var cleanupFailure = CleanupTemporaryFile();
            var suffix = cleanupFailure is null
                ? string.Empty
                : $" Temporary-file cleanup also failed: {cleanupFailure.Message}";
            throw new OutputException(
                $"Could not commit output file '{_destinationPath}': {exception.Message}{suffix}",
                exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_committed || _destinationPath is null)
        {
            return;
        }

        var cleanupFailure = CleanupTemporaryFile();
        if (cleanupFailure is not null)
        {
            throw new OutputException(
                $"Could not clean up temporary output for '{_destinationPath}': {cleanupFailure.Message}",
                cleanupFailure);
        }
    }

    private static TextWriter CreateStreamWriter(Stream stream) => new StreamWriter(
        stream,
        Utf8WithoutBom,
        bufferSize: 1024,
        leaveOpen: true);

    private static string ResolveFullPath(string path, string description)
    {
        try
        {
            return Path.GetFullPath(path, Environment.CurrentDirectory);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new OutputException($"Invalid {description} path '{path}': {exception.Message}", exception);
        }
    }

    private TextWriter OpenTemporaryWriter()
    {
        var directory = Path.GetDirectoryName(_destinationPath)!;
        _temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            _stream = new FileStream(
                _temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            _innerWriter = _writerFactory!(_stream);
            return new OutputErrorTextWriter(_innerWriter, _destinationPath!);
        }
        catch (Exception exception) when (IsOutputIoException(exception))
        {
            var cleanupFailure = CleanupTemporaryFile();
            var suffix = cleanupFailure is null
                ? string.Empty
                : $" Temporary-file cleanup also failed: {cleanupFailure.Message}";
            throw new OutputException(
                $"Could not open output file '{_destinationPath}': {exception.Message}{suffix}",
                exception);
        }
    }

    private void CloseTemporaryWriter()
    {
        try
        {
            _innerWriter?.Dispose();
        }
        finally
        {
            _innerWriter = null;
            _stream?.Dispose();
            _stream = null;
        }
    }

    private Exception? CleanupTemporaryFile()
    {
        Exception? failure = null;
        try
        {
            CloseTemporaryWriter();
        }
        catch (Exception exception) when (IsOutputIoException(exception))
        {
            failure = exception;
        }

        _writer = null;
        if (_temporaryPath is null)
        {
            return failure;
        }

        try
        {
            File.Delete(_temporaryPath);
            _temporaryPath = null;
        }
        catch (Exception exception) when (IsOutputIoException(exception))
        {
            failure ??= exception;
        }

        return failure;
    }

    private static bool IsOutputIoException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException;

    private sealed class OutputErrorTextWriter(TextWriter innerWriter, string destinationPath) : TextWriter
    {
        public override Encoding Encoding => innerWriter.Encoding;

        public override IFormatProvider FormatProvider => innerWriter.FormatProvider;

        public override void Flush() => Execute(innerWriter.Flush);

        public override void Write(char value) => Execute(() => innerWriter.Write(value));

        public override void Write(char[] buffer, int index, int count) =>
            Execute(() => innerWriter.Write(buffer, index, count));

        public override void Write(string? value) => Execute(() => innerWriter.Write(value));

        public override void WriteLine() => Execute(innerWriter.WriteLine);

        public override void WriteLine(string? value) => Execute(() => innerWriter.WriteLine(value));

        private static bool IsIoFailure(Exception exception) =>
            exception is IOException or UnauthorizedAccessException or NotSupportedException;

        private OutputException ToOutputException(Exception exception) => new(
            $"Could not write output file '{destinationPath}': {exception.Message}",
            exception);

        private void Execute(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception) when (IsIoFailure(exception))
            {
                throw ToOutputException(exception);
            }
        }
    }
}
