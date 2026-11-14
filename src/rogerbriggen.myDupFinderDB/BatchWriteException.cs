// Roger Briggen license this file to you under the MIT license.
//

using System;

namespace RogerBriggen.MyDupFinderDB;

/// <summary>
/// A batch of database operations could not be written. The whole batch was rolled back and the operations are still pending.
/// This is fatal for a run: continuing would only collect more work that cannot be stored.
/// </summary>
public class BatchWriteException : Exception
{
    public BatchWriteException()
    {
    }

    public BatchWriteException(string message) : base(message)
    {
    }

    public BatchWriteException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public BatchWriteException(string message, int pendingOperationCount, Exception innerException) : base(message, innerException) => PendingOperationCount = pendingOperationCount;

    /// <summary>
    /// Gets the number of operations that were in the failed batch and are still pending.
    /// </summary>
    public int PendingOperationCount { get; }
}
