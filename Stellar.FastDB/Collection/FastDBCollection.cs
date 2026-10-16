using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Stellar.Collections
{
    /// <summary>Represents a thread-safe collection of keys and values persisted to the file system.</summary>
    /// <typeparam name="TKey">The type of the keys in the collection.</typeparam>
    /// <typeparam name="TValue">The type of the values in the collection.</typeparam>
    /// <remarks>
    /// All public and protected members of <see cref="FastDBCollection{TKey,TValue}"/> are thread-safe and may be used
    /// concurrently from multiple threads.
    /// </remarks>
    internal sealed class FastDBCollection<TKey, TValue> : IFastDBCollection<TKey, TValue> where TKey : struct
    {
        public string Name { get; private set; }
        public bool IsClosed { get; private set; }
        public FastDBOptions Options { get; private set; }

        private ConcurrentDictionary<TKey, TValue> _Values { get; set; }

        private FastDBStream<TKey, TValue> _FastDBStream;
        private bool _WriteToFileSystem => !Options.IsMemoryOnlyEnabled;
        private bool _IsLoaded = false;

        public FastDBCollection(string name, FastDBOptions options)
        {
            Name = name;
            Options = options;

            if (string.IsNullOrEmpty(name))
            {
                Name = typeof(TValue).Name;
                if (Options.GeneratedFileNameCreationFunction != null)
                    Name = Options.GeneratedFileNameCreationFunction(Name);
            }
        }

        /// <summary>
        /// Reopens a FastDB collection instance that was previously closed.
        /// This method should only be used if the collection has been explicitly closed earlier.
        /// </summary>
        public void Load()
        {
            if (_IsLoaded)
                throw ThrowHelper.CollectionOpen(Name);
            else
                _IsLoaded = true;

            IsClosed = false;

            string filePath = null;
            if (_WriteToFileSystem)
            {
                if (!Directory.Exists(Options.DirectoryPath))
                    Directory.CreateDirectory(Options.DirectoryPath);

                filePath = Path.Combine(Options.DirectoryPath, $"{Name}.{Options.FileExtension}");
            }

            _FastDBStream = new FastDBStream<TKey, TValue>(filePath, Options);
            _Values = _FastDBStream.Load();
        }

        public async Task LoadAsync()
        {
            await Task.Run(Load);
        }

        public void Clear()
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            _FastDBStream.Clear();
        }

        public void Flush()
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);

            _FastDBStream.Flush();
        }

        public async Task FlushAsync()
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);

            await _FastDBStream.FlushAsync();
        }

        public void Close()
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);

            IsClosed = true;
            _FastDBStream.Flush();
            _FastDBStream.Dispose();
            _FastDBStream = null;
            _Values = null;
            _IsLoaded = false;
        }

        public async Task CloseAsync()
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);

            IsClosed = true;
            await _FastDBStream.FlushAsync();
            _FastDBStream.Dispose();
            _FastDBStream = null;
            _Values = null;
            _IsLoaded = false;
        }

        public void Delete()
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            _FastDBStream.Delete();
            _FastDBStream = null;
            _Values = null;
        }

        public async Task DeleteAsync()
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            await _FastDBStream.DeleteAsync();
            _FastDBStream = null;
            _Values = null;
        }

        public bool Add(TKey key, TValue value)
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            return _FastDBStream.Add(key, value);
        }

        public ValueTask<bool> AddAsync(TKey key, TValue value)
        {
            if (IsClosed)
                return ValueTask.FromException<bool>(ThrowHelper.CollectionClosed(Name));
            if (Options.IsReadOnlyEnabled)
                return ValueTask.FromException<bool>(ThrowHelper.CollectionReadOnly(Name));

            return _FastDBStream.AddAsync(key, value);
        }

        public ValueTask<bool> AddBulkAsync(IDictionary<TKey, TValue> dictionary)
        {
            if (IsClosed)
                return ValueTask.FromException<bool>(ThrowHelper.CollectionClosed(Name));
            if (Options.IsReadOnlyEnabled)
                return ValueTask.FromException<bool>(ThrowHelper.CollectionReadOnly(Name));

            return _FastDBStream.AddBulkAsync(dictionary);
        }

        public bool Update(TKey key, TValue value)
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            return _FastDBStream.Update(key, value);
        }

        public ValueTask<bool> UpdateAsync(TKey key, TValue value)
        {
            if (IsClosed)
                return ValueTask.FromException<bool>(ThrowHelper.CollectionClosed(Name));
            if (Options.IsReadOnlyEnabled)
                return ValueTask.FromException<bool>(ThrowHelper.CollectionReadOnly(Name));

            return _FastDBStream.UpdateAsync(key, value);
        }

        public bool AddOrUpdate(TKey key, TValue value)
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            return _FastDBStream.AddOrUpdate(key, value);
        }

        public ValueTask<bool> AddOrUpdateAsync(TKey key, TValue value)
        {
            if (IsClosed)
                return ValueTask.FromException<bool>(ThrowHelper.CollectionClosed(Name));
            if (Options.IsReadOnlyEnabled)
                return ValueTask.FromException<bool>(ThrowHelper.CollectionReadOnly(Name));

            return _FastDBStream.AddOrUpdateAsync(key, value);
        }

        public bool Remove(TKey key, out TValue value)
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            return _FastDBStream.Remove(key, out value);
        }

        public ValueTask<bool> RemoveAsync(TKey key)
        {
            if (IsClosed)
                return ValueTask.FromException<bool>(ThrowHelper.CollectionClosed(Name));
            if (Options.IsReadOnlyEnabled)
                return ValueTask.FromException<bool>(ThrowHelper.CollectionReadOnly(Name));

            return _FastDBStream.RemoveAsync(key);
        }

        public void RemoveBulk(IEnumerable<TKey> keys)
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            _FastDBStream.RemoveBulk(keys);
        }

        public ValueTask RemoveBulkAsync(IEnumerable<TKey> keys)
        {
            if (IsClosed)
                return ValueTask.FromException(ThrowHelper.CollectionClosed(Name));
            if (Options.IsReadOnlyEnabled)
                return ValueTask.FromException(ThrowHelper.CollectionReadOnly(Name));

            return _FastDBStream.RemoveBulkAsync(keys);
        }

        public bool TryGet(TKey key, out TValue value)
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);

            return _Values.TryGetValue(key, out value);
        }

        public TValue this[TKey key]
        {
            get
            {
                if (IsClosed)
                    throw ThrowHelper.CollectionClosed(Name);
                return _Values[key];

            }
            set
            {
                if (IsClosed)
                    throw ThrowHelper.CollectionClosed(Name);
                if (Options.IsReadOnlyEnabled)
                    throw ThrowHelper.CollectionReadOnly(Name);
                AddOrUpdate(key, value);
            }
        }

        public bool ContainsKey(TKey key)
        {
            return _Values.ContainsKey(key);
        }

        public IEnumerator<TValue> GetEnumerator()
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);

            foreach (var kvp in _Values)
                yield return kvp.Value;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);

            return GetEnumerator();
        }

        public IEnumerable<KeyValuePair<TKey, TValue>> AsEnumerable()
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);

            return _Values;
        }

        public void Compact()
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            _FastDBStream.Compact();
        }

        public async Task CompactAsync()
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            await _FastDBStream.CompactAsync();
        }

        public int Count
        {
            get
            {
                if (IsClosed)
                    throw ThrowHelper.CollectionClosed(Name);

                return _Values.Count;
            }
        }

        public long GetSizeBytes()
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);

            return _FastDBStream.GetSizeBytes();
        }

        public ushort Version
        {
            get
            {
                if (_FastDBStream != null)
                    return _FastDBStream.Version;
                else
                    return 0;
            }
        }

        public string FilePath
        {
            get
            {
                if (_FastDBStream != null)
                    return _FastDBStream.FilePath;
                else
                    return null;
            }
        }

        public bool IsEncrypted
        {
            get
            {
                if (_FastDBStream != null)
                    return _FastDBStream.IsEncrypted;
                else
                    return Options.IsEncryptionEnabled;
            }
        }

        public bool IsCompressed
        {
            get
            {
                if (_FastDBStream != null)
                    return _FastDBStream.IsCompressed;
                else
                    return Options.IsCompressionEnabled;
            }
        }

        public override string ToString()
        {
            return $"{Name}[{_Values.Count}]";
        }
    }
}
