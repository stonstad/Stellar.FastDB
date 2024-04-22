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

    }
}
