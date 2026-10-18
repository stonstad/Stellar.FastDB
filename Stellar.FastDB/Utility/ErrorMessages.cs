using System;
using System.Collections.Generic;
using System.IO;

namespace Stellar.Collections
{
    internal static class ThrowHelper
    {
        internal static ArgumentException DuplicateKey<TKey>(TKey key) where TKey : struct => new ArgumentException($"An item with the same key '{key}' has already been added.");
        internal static KeyNotFoundException KeyNotFound<TKey>(TKey key) where TKey : struct => new KeyNotFoundException($"The given key '{key}' was not present in the collection.");

        internal static InvalidOperationException CollectionClosed(string collection) => new InvalidOperationException($"Collection '{collection}' is closed and cannot be accessed.");
        internal static InvalidOperationException CollectionReadOnly(string collection) => new InvalidOperationException($"Collection '{collection}' is read-only and cannot be modified.");
        internal static InvalidOperationException CollectionTypeMismatch(string collection, Type keyType, Type valueType) => new InvalidOperationException($"Collection '{collection}' is already open with types other than <{keyType.Name}, {valueType.Name}>.");
        internal static InvalidOperationException DatabaseClosed(string database) => new InvalidOperationException($"Database '{database}' is closed and cannot be accessed.");
        internal static InvalidOperationException DatabaseReadOnly(string database) => new InvalidOperationException($"Database '{database}' is read-only and cannot be modified.");
        internal static InvalidOperationException DecryptionFailed() => new InvalidOperationException($"The file cannot be decrypted. Verify the encryption password and specified encryption algorithm.");
        internal static ArgumentOutOfRangeException EncryptionPasswordRequired() => new ArgumentOutOfRangeException($"Encryption password may not be null");
        internal static ArgumentOutOfRangeException DatabaseNameMayNotContainSpecialCharacters(string database) => new ArgumentOutOfRangeException($"Database name is a directory and may not contain special characters ('{database}')");
        internal static ArgumentOutOfRangeException DatabaseNameMayNotBeNullOrEmpty() => new ArgumentOutOfRangeException($"Database name may not be null or empty.");
        internal static InvalidDataException UnsupportedFileVersion(string filePath, int version) => new InvalidDataException($"File '{filePath}' uses FastDB file format version {version}, which this version of FastDB can't open.");
        internal static InvalidDataException NotAFastDBFile(string filePath) => new InvalidDataException($"File '{filePath}' is not a FastDB file.");
        internal static IOException WriteFailed(string filePath, Exception innerException) => new IOException($"Writing to file '{filePath}' failed. The change is in memory but not in the file.", innerException);
        internal static CompactionBlockedException CompactionBlocked(string filePath, int records) => new CompactionBlockedException($"File '{filePath}' can't be compacted: {records} record(s) couldn't be read, not even their keys, when it was opened. Fix the types or serializer settings and reopen it.");
        internal static InvalidDataException CorruptBlock(string filePath) => new InvalidDataException($"File '{filePath}' contains a block that passed its checksum but can't be parsed.");

    }

    // a collection's Compact() refusing because of records it couldn't read; FastDB.Compact() skips such a collection
    internal sealed class CompactionBlockedException : InvalidOperationException
    {
        public CompactionBlockedException(string message) : base(message)
        {
        }
    }
}
