using System;
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

        private ConcurrentDictionary<TKey, TValue> _Values { get; set; } = new ConcurrentDictionary<TKey, TValue>();

        private FastDBStream<TKey, TValue> _FastDBStream;
        private bool _WriteToFileSystem => !Options.IsMemoryOnlyEnabled;
        private bool _IsLoaded = false;

        public FastDBCollection(string name, FastDBOptions options)
        {
            Name = name;
            Options = options;

            if (string.IsNullOrEmpty(name))
                Name = typeof(TValue).Name;
        }

        public void Load()
        {
            if (_IsLoaded)
                throw ThrowHelper.CollectionOpen(Name);
            else
                _IsLoaded = true;

            if (_WriteToFileSystem)
            {
                if (!Directory.Exists(Options.DirectoryPath))
                    Directory.CreateDirectory(Options.DirectoryPath);

                string filePath = Path.Combine(Options.DirectoryPath, $"{Name}.{Options.FileExtension}");
                _FastDBStream = new FastDBStream<TKey, TValue>(filePath, Options);
                _FastDBStream.Load(_Values);
            }
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

            if (_WriteToFileSystem)
                _FastDBStream.Clear();

            _Values.Clear();
        }

        public void Flush()
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);

            if (_FastDBStream != null && _WriteToFileSystem)
                _FastDBStream.Flush();
        }

        public async Task FlushAsync()
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);

            if (_FastDBStream != null && _WriteToFileSystem)
                await _FastDBStream.FlushAsync();
        }

        public void Close()
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);

            IsClosed = true;
            if (_FastDBStream != null && _WriteToFileSystem)
            {
                _FastDBStream.Flush();
                _FastDBStream.Dispose();
                _FastDBStream = null;
            }
            _Values = null;
        }

        public async Task CloseAsync()
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);

            IsClosed = true;
            if (_FastDBStream != null && _WriteToFileSystem)
            {
                await _FastDBStream.FlushAsync();
                _FastDBStream.Dispose();
                _FastDBStream = null;
            }
            _Values = null;
        }

        public void Delete()
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            if (_FastDBStream != null && _WriteToFileSystem)
            {
                _FastDBStream.Delete();
                _FastDBStream = null;
            }
            _Values = null;
        }

        public async Task DeleteAsync()
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            if (_FastDBStream != null && _WriteToFileSystem)
            {
                await _FastDBStream.DeleteAsync();
                _FastDBStream = null;
            }
            _Values = null;
        }

        public bool Add(TKey key, TValue value)
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            if (_Values.TryAdd(key, value))
            {
                if (_WriteToFileSystem)
                    _FastDBStream.Add(key, value);
                return true;
            }
            else if (Options.AddDuplicateKeyBehavior == DuplicateKeyBehaviorType.Upsert)
            {
                _Values[key] = value;
                if (_WriteToFileSystem)
                    _FastDBStream.Update(key, value);
                return true;
            }
            else if (Options.AddDuplicateKeyBehavior == DuplicateKeyBehaviorType.ReturnFalse)
                return false;
            else
                throw ThrowHelper.DuplicateKey(key);
        }

        public async Task<bool> AddAsync(TKey key, TValue value)
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            if (_Values.TryAdd(key, value))
            {
                if (_WriteToFileSystem)
                    return await _FastDBStream.AddAsync(key, value);
                return true;
            }
            else if (Options.AddDuplicateKeyBehavior == DuplicateKeyBehaviorType.Upsert)
            {
                _Values[key] = value;
                _FastDBStream.Update(key, value);
                return true;
            }
            else if (Options.AddDuplicateKeyBehavior == DuplicateKeyBehaviorType.ReturnFalse)
                return false;
            else
                throw ThrowHelper.DuplicateKey(key);
        }

        public async Task<bool> AddBulkAsync(IDictionary<TKey, TValue> dictionary)
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            if (Options.BufferMode != BufferModeType.Disabled)
                await FlushAsync();

            if (Options.BulkAddDuplicateKeyBehavior == DuplicateKeyBehaviorType.Upsert)
            {
                // DuplicateKeyBehaviorType.Upsert is not atomic
                Dictionary<TKey, TValue> bulkAddDictionary = new Dictionary<TKey, TValue>();
                Dictionary<TKey, TValue> bulkUpsertDictionary = new Dictionary<TKey, TValue>();

                foreach (var kvp in dictionary)
                {
                    if (_Values.ContainsKey(kvp.Key))
                        bulkUpsertDictionary[kvp.Key] = kvp.Value;
                    else
                        bulkAddDictionary[kvp.Key] = kvp.Value;

                    _Values[kvp.Key] = kvp.Value;
                }

                foreach (var kvp in bulkUpsertDictionary)
                    _FastDBStream.Update(kvp.Key, kvp.Value);

                if (Options.BufferMode != BufferModeType.Disabled)
                    await FlushAsync();

                if (_WriteToFileSystem)
                    return _FastDBStream.AddBulk(bulkAddDictionary);
                return true;
            }
            else if (Options.BulkAddDuplicateKeyBehavior == DuplicateKeyBehaviorType.ReturnFalse)
            {
                foreach (var kvp in dictionary)
                    if (_Values.ContainsKey(kvp.Key))
                        return false;

                foreach (var kvp in dictionary)
                    _Values[kvp.Key] = kvp.Value;

                if (_WriteToFileSystem)
                    return _FastDBStream.AddBulk(dictionary);
                return true;
            }
            else if (Options.BulkAddDuplicateKeyBehavior == DuplicateKeyBehaviorType.Exception)
            {
                foreach (var kvp in dictionary)
                    if (_Values.ContainsKey(kvp.Key))
                        throw ThrowHelper.DuplicateKey(kvp.Key);

                foreach (var kvp in dictionary)
                    _Values[kvp.Key] = kvp.Value;

                if (_WriteToFileSystem)
                    return _FastDBStream.AddBulk(dictionary);
                return true;
            }
            else
                throw new ArgumentOutOfRangeException();
        }

        public bool Update(TKey key, TValue value)
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            if (_Values.ContainsKey(key))
            {
                _Values[key] = value;
                if (_WriteToFileSystem)
                    return _FastDBStream.Update(key, value);
                return true;
            }
            else if (Options.KeyNotFoundBehavior == ErrorBehaviorType.Exception)
                throw ThrowHelper.KeyNotFound(key);
            else
                return false;
        }

        public async Task<bool> UpdateAsync(TKey key, TValue value)
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            if (_Values.TryGetValue(key, out _))
            {
                _Values[key] = value;
                if (_WriteToFileSystem)
                    return await _FastDBStream.UpdateAsync(key, value);
                return true;
            }
            else if (Options.KeyNotFoundBehavior == ErrorBehaviorType.Exception)
                throw new KeyNotFoundException();
            else
                return false;
        }

        public bool AddOrUpdate(TKey key, TValue value)
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            if (_Values.ContainsKey(key))
                return Update(key, value);
            else
                return Add(key, value);
        }

        public async Task<bool> AddOrUpdateAsync(TKey key, TValue value)
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            if (_Values.ContainsKey(key))
                return await UpdateAsync(key, value);
            else
                return await AddAsync(key, value);
        }

        public bool Remove(TKey key, out TValue value)
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            if (_Values.TryRemove(key, out TValue v))
            {
                value = v;
                if (_WriteToFileSystem)
                    _FastDBStream.Remove(key);
                return true;
            }
            else if (Options.KeyNotFoundBehavior == ErrorBehaviorType.Exception)
                throw new KeyNotFoundException();
            else
            {
                value = default;
                return false;
            }
        }

        public async Task<bool> RemoveAsync(TKey key)
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.CollectionReadOnly(Name);

            if (_Values.TryRemove(key, out _))
            {
                if (_WriteToFileSystem)
                    await _FastDBStream.RemoveAsync(key);
                return true;
            }
            else if (Options.KeyNotFoundBehavior == ErrorBehaviorType.Exception)
                throw new KeyNotFoundException();
            else
                return false;
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

        public async Task DefragmentMemoryAsync()
        {
            if (IsClosed)
                throw ThrowHelper.CollectionClosed(Name);

            if (_FastDBStream != null)
                await _FastDBStream.DefragmentMemory();
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

            if (_WriteToFileSystem)
                return _FastDBStream.GetSizeBytes();
            return 0;
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
