using System;
using System.Collections.Generic;

namespace Stellar.Collections
{
    internal static class ThrowHelper
    {
        internal static ArgumentException DuplicateKey<TKey>(TKey key) where TKey : struct => new ArgumentException($"An item with the same key '{key}' has already been added.");
        internal static KeyNotFoundException KeyNotFound<TKey>(TKey key) where TKey : struct => new KeyNotFoundException($"The given key '{key}' was not present in the collection.");

        internal static InvalidOperationException CollectionClosed(string collection) => new InvalidOperationException($"Collection '{collection}' is closed and cannot be accessed.");
        internal static InvalidOperationException CollectionReadOnly(string collection) => new InvalidOperationException($"Collection '{collection}' is read-only and cannot be modified.");
        internal static InvalidOperationException CollectionOpen(string collection) => new InvalidOperationException($"Collection '{collection}' is already open.");
        internal static InvalidOperationException DatabaseClosed(string database) => new InvalidOperationException($"Database '{database}' is closed and cannot be accessed.");
        internal static InvalidOperationException DatabaseReadOnly(string database) => new InvalidOperationException($"Database '{database}' is read-only and cannot be modified.");
        internal static InvalidOperationException DecryptionFailed() => new InvalidOperationException($"The file cannot be decrypted. Verify the encryption password and specified encryption algorithm.");
        internal static ArgumentOutOfRangeException EncryptionPasswordRequired() => new ArgumentOutOfRangeException($"Encryption password may not be null");
        internal static ArgumentOutOfRangeException DatabaseNameMayNotContainSpecialCharacters(string database) => new ArgumentOutOfRangeException($"Database name is a directory and may not contain special characters ('{database}')");
        internal static ArgumentOutOfRangeException DatabaseNameMayNotBeNullOrEmpty() => new ArgumentOutOfRangeException($"Database name may not be null or empty.");

    }
}
