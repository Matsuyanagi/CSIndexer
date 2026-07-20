namespace CsIndex.Storage;

public sealed class IndexDatabaseException(string message, Exception? innerException = null)
    : Exception(message, innerException);
