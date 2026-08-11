using System.Text;
using CsIndex.Core.Input;

namespace CsIndex.Cli;

internal sealed class OutputException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed class OutputDestination : IDisposable
{
    private const int MaximumTemporaryFileAttempts = 8;
    private const string CleanupFailureDataKey = "OutputDestination.CleanupFailure";
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string? _destinationPath;
    private readonly TextWriter? _standardOutputWriter;
    private readonly Func<Stream, TextWriter>? _writerFactory;
    private readonly Func<string>? _temporaryPathFactory;
    private TextWriter? _innerWriter;
    private FileStream? _stream;
    private string? _temporaryPath;
    private TextWriter? _writer;
    private Exception? _primaryFailure;
    private DestinationState _state;

    private OutputDestination(
        string? destinationPath,
        TextWriter? standardOutputWriter,
        Func<Stream, TextWriter>? writerFactory,
        Func<string>? temporaryPathFactory)
    {
        _destinationPath = destinationPath;
        _standardOutputWriter = standardOutputWriter;
        _writerFactory = writerFactory;
        _temporaryPathFactory = temporaryPathFactory;
    }

    public TextWriter Writer
    {
        get
        {
            ThrowIfDisposed();
            ThrowIfTerminal();
            if (_destinationPath is null)
            {
                _state = DestinationState.Open;
                return _standardOutputWriter!;
            }

            if (_state == DestinationState.Unopened)
            {
                _writer = OpenTemporaryWriter();
                _state = DestinationState.Open;
            }

            return _writer!;
        }
    }

    public static OutputDestination Create(string? outputPath, string databasePath) =>
        Create(outputPath, databasePath, CreateStreamWriter);

    internal static OutputDestination Create(
        string? outputPath,
        string databasePath,
        Func<Stream, TextWriter> writerFactory) =>
        Create(outputPath, databasePath, writerFactory, CreateTemporaryPath);

    internal static OutputDestination Create(
        string? outputPath,
        string databasePath,
        Func<Stream, TextWriter> writerFactory,
        Func<string, string> temporaryPathFactory)
    {
        ArgumentNullException.ThrowIfNull(databasePath);
        ArgumentNullException.ThrowIfNull(writerFactory);
        ArgumentNullException.ThrowIfNull(temporaryPathFactory);
        if (outputPath is null)
        {
            return new OutputDestination(
                destinationPath: null,
                Console.Out,
                writerFactory: null,
                temporaryPathFactory: null);
        }

        var destinationPath = ResolveFullPath(outputPath, "output");
        var destinationComparisonPath = CreatePathComparisonKey(destinationPath, "output");
        var databaseComparisonPath = CreatePathComparisonKey(databasePath, "database");
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(destinationComparisonPath, databaseComparisonPath, comparison))
        {
            throw new CliUsageException("Output file path must not match the active database path.");
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (directory is null || !Directory.Exists(directory))
        {
            throw new OutputException($"Output directory does not exist: {directory ?? destinationPath}");
        }

        return new OutputDestination(
            destinationPath,
            standardOutputWriter: null,
            writerFactory,
            () => temporaryPathFactory(destinationPath));
    }

    public void Commit() => Commit(default);

    public void Commit(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_state == DestinationState.Committed)
        {
            return;
        }

        ThrowIfTerminal();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_destinationPath is null)
            {
                _state = DestinationState.Committed;
                return;
            }

            _ = Writer;
            cancellationToken.ThrowIfCancellationRequested();
            _innerWriter!.Flush();
            _stream!.Flush(flushToDisk: true);
            var closeFailure = CloseTemporaryWriter();
            if (closeFailure is not null)
            {
                throw closeFailure;
            }

            cancellationToken.ThrowIfCancellationRequested();
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
            _state = DestinationState.Committed;
        }
        catch (OperationCanceledException exception)
        {
            Abort(exception);
            throw;
        }
        catch (OutputException exception)
        {
            Abort(exception);
            throw;
        }
        catch (Exception exception)
        {
            var outputException = new OutputException(
                $"Could not commit output file '{_destinationPath}': {exception.Message}",
                exception);
            Abort(outputException);
            throw outputException;
        }
    }

    internal void WritePayload(Action<TextWriter> formatter) => WritePayload(formatter, default);

    internal void WritePayload(Action<TextWriter> formatter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        try
        {
            formatter(Writer);
            Commit(cancellationToken);
        }
        catch (Exception exception)
        {
            Abort(exception);
            throw;
        }
    }

    public void Dispose()
    {
        if (_state == DestinationState.Disposed)
        {
            return;
        }

        OutputException? disposalException = null;
        if (_state != DestinationState.Committed && _destinationPath is not null)
        {
            var cleanupFailure = CleanupTemporaryFile();
            if (cleanupFailure is not null)
            {
                if (_primaryFailure is not null)
                {
                    AttachCleanupFailure(_primaryFailure, cleanupFailure);
                }
                else
                {
                    disposalException = new OutputException(
                        $"Could not clean up temporary output for '{_destinationPath}': {cleanupFailure.Message}",
                        cleanupFailure);
                }
            }
        }

        _state = DestinationState.Disposed;
        if (disposalException is not null)
        {
            throw disposalException;
        }
    }

    private static TextWriter CreateStreamWriter(Stream stream) => new StreamWriter(
        stream,
        Utf8WithoutBom,
        bufferSize: 1024,
        leaveOpen: true);

    private static string CreateTemporaryPath(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath)!;
        return Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
    }

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

    private static string CreatePathComparisonKey(string path, string description)
    {
        string normalizedPath;
        try
        {
            normalizedPath = PathNormalizer.Normalize(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new OutputException($"Invalid {description} path '{path}': {exception.Message}", exception);
        }

        if (!OperatingSystem.IsWindows() ||
            (!normalizedPath.StartsWith(@"\\?\", StringComparison.Ordinal) &&
             !normalizedPath.StartsWith(@"\\.\", StringComparison.Ordinal)))
        {
            return normalizedPath;
        }

        var unprefixedPath = normalizedPath[4..];
        if (unprefixedPath.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return PathNormalizer.Normalize(@"\\" + unprefixedPath[4..]);
        }

        if (unprefixedPath.Length >= 3 &&
            char.IsAsciiLetter(unprefixedPath[0]) &&
            unprefixedPath[1] == Path.VolumeSeparatorChar &&
            unprefixedPath[2] == Path.DirectorySeparatorChar)
        {
            return PathNormalizer.Normalize(unprefixedPath);
        }

        throw new CliUsageException(
            $"Unsupported Windows device path syntax for {description}: '{path}'.");
    }

    private TextWriter OpenTemporaryWriter()
    {
        for (var attempt = 0; attempt < MaximumTemporaryFileAttempts; attempt++)
        {
            string candidatePath;
            try
            {
                candidatePath = GetTemporaryCandidate();
            }
            catch (Exception exception)
            {
                throw FailOpen(exception);
            }

            FileStream stream;
            try
            {
                stream = new FileStream(
                    candidatePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
            }
            catch (IOException) when (
                attempt < MaximumTemporaryFileAttempts - 1 && File.Exists(candidatePath))
            {
                continue;
            }
            catch (Exception exception)
            {
                throw FailOpen(exception);
            }

            _stream = stream;
            _temporaryPath = candidatePath;
            try
            {
                _innerWriter = _writerFactory!(_stream);
                if (_innerWriter is null)
                {
                    throw new InvalidOperationException("The output writer factory returned null.");
                }

                return new OutputErrorTextWriter(_innerWriter, HandleWriteFailure);
            }
            catch (Exception exception)
            {
                throw FailOpen(exception);
            }
        }

        throw new InvalidOperationException("Temporary output allocation exhausted unexpectedly.");
    }

    private string GetTemporaryCandidate()
    {
        var candidatePath = _temporaryPathFactory!();
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            throw new InvalidOperationException("The temporary path factory returned an empty path.");
        }

        var fullCandidatePath = Path.GetFullPath(candidatePath, Environment.CurrentDirectory);
        var destinationDirectory = Path.GetDirectoryName(_destinationPath)!;
        var candidateDirectory = Path.GetDirectoryName(fullCandidatePath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(candidateDirectory, destinationDirectory, comparison))
        {
            throw new InvalidOperationException("The temporary output file must be in the destination directory.");
        }

        return fullCandidatePath;
    }

    private OutputException FailOpen(Exception exception)
    {
        var outputException = exception as OutputException ?? new OutputException(
            $"Could not open output file '{_destinationPath}': {exception.Message}",
            exception);
        Abort(outputException);
        return outputException;
    }

    private OutputException HandleWriteFailure(Exception exception)
    {
        var outputException = new OutputException(
            $"Could not write output file '{_destinationPath}': {exception.Message}",
            exception);
        Abort(outputException);
        return outputException;
    }

    private void Abort(Exception primaryFailure)
    {
        if (_state is DestinationState.Committed or DestinationState.Disposed)
        {
            return;
        }

        _primaryFailure ??= primaryFailure;
        _state = DestinationState.Faulted;
        var cleanupFailure = CleanupTemporaryFile();
        if (cleanupFailure is not null)
        {
            AttachCleanupFailure(primaryFailure, cleanupFailure);
        }
    }

    private Exception? CloseTemporaryWriter()
    {
        List<Exception>? failures = null;
        var innerWriter = _innerWriter;
        _innerWriter = null;
        if (innerWriter is not null)
        {
            try
            {
                innerWriter.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        var stream = _stream;
        _stream = null;
        if (stream is not null)
        {
            try
            {
                stream.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        return CombineFailures(failures);
    }

    private Exception? CleanupTemporaryFile()
    {
        List<Exception>? failures = null;
        var closeFailure = CloseTemporaryWriter();
        if (closeFailure is not null)
        {
            (failures ??= []).Add(closeFailure);
        }

        _writer = null;
        if (_temporaryPath is not null)
        {
            try
            {
                File.Delete(_temporaryPath);
                _temporaryPath = null;
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        return CombineFailures(failures);
    }

    private static Exception? CombineFailures(List<Exception>? failures) => failures?.Count switch
    {
        null or 0 => null,
        1 => failures[0],
        _ => new AggregateException(failures),
    };

    private static void AttachCleanupFailure(Exception primaryFailure, Exception cleanupFailure)
    {
        if (primaryFailure.Data[CleanupFailureDataKey] is Exception existingFailure)
        {
            primaryFailure.Data[CleanupFailureDataKey] = new AggregateException(existingFailure, cleanupFailure);
        }
        else
        {
            primaryFailure.Data[CleanupFailureDataKey] = cleanupFailure;
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_state == DestinationState.Disposed, this);

    private void ThrowIfTerminal()
    {
        if (_state == DestinationState.Committed)
        {
            throw new InvalidOperationException("The output destination has already been committed.");
        }

        if (_state == DestinationState.Faulted)
        {
            throw new InvalidOperationException("The output destination is faulted and cannot be reused.");
        }
    }

    private sealed class OutputErrorTextWriter(
        TextWriter innerWriter,
        Func<Exception, OutputException> onWriteFailure) : TextWriter
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

        private void Execute(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception) when (IsIoFailure(exception))
            {
                throw onWriteFailure(exception);
            }
        }
    }

    private enum DestinationState
    {
        Unopened,
        Open,
        Committed,
        Faulted,
        Disposed,
    }
}
