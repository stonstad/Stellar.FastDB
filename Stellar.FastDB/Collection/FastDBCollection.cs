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

        private ConcurrentDictionary<TKey, TValue> _Values { get; set; }

        private FastDBStream<TKey, TValue> _FastDBStream;
        private bool _WriteToFileSystem => !Options.IsMemoryOnlyEnabled;
        private readonly FastDB _Owner;         // forgets this collection once it's closed or deleted

        public FastDBCollection(FastDB owner, string name, FastDBOptions options)
        {
            _Owner = owner;
            Name = name;
            Options = options;
        }

        // FastDB loads each collection once, under its name's lock, before anything else can reach it. A closed
        // collection isn't loaded again: GetCollection() opens its name afresh.
        internal void Load()
        {
            string filePath = null;
            if (_WriteToFileSystem)
            {
                if (!Directory.Exists(Options.DirectoryPath))
                    Directory.CreateDirectory(Options.DirectoryPath);

                filePath = Path.Combine(Options.DirectoryPath, $"{Name}.{Options.FileExtension}");
            }

            FastDBStream<TKey, TValue> stream = new FastDBStream<TKey, TValue>(Name, filePath, Options);
            _Values = stream.Load();
            _FastDBStream = stream;
        }

        internal Task LoadAsync()
        {
            return Task.Run(Load);
        }

        // Close() or Delete() on another thread can release the values at any moment, so each call reads them once
        private ConcurrentDictionary<TKey, TValue> OpenValues()
        {
            ConcurrentDictionary<TKey, TValue> values = _Values;
            if (IsClosed || values == null)
                throw ThrowHelper.CollectionClosed(Name);
            return values;
        }

        // Close() or Delete() on another thread can release the stream at any moment, so each call reads it once. A
        // write that reaches it after it's closed is refused by the stream itself. Async calls return the refusal in
        // their task rather than throw it.
        private bool TryOpenStream(bool write, out FastDBStream<TKey, TValue> stream, out Exception refusal)
        {
            stream = _FastDBStream;
            refusal = null;
            if (IsClosed || stream == null)
                refusal = ThrowHelper.CollectionClosed(Name);
            else if (write && Options.IsReadOnlyEnabled)
                refusal = ThrowHelper.CollectionReadOnly(Name);
            return refusal == null;
        }

        private FastDBStream<TKey, TValue> OpenStream(bool write = false)
        {
            if (!TryOpenStream(write, out FastDBStream<TKey, TValue> stream, out Exception refusal))
                throw refusal;
            return stream;
        }

        public void Clear()
        {
            OpenStream(write: true).Clear();
        }

        public void Flush()
        {
            OpenStream().Flush();
        }

        public Task FlushAsync()
        {
            if (!TryOpenStream(false, out FastDBStream<TKey, TValue> stream, out Exception refusal))
                return Task.FromException(refusal);
            return stream.FlushAsync();
        }

        // FastDB passes throwIfClosed false when it closes or deletes every collection, skipping one closed on its own
        // meanwhile
        public void Close(bool throwIfClosed = true)
        {
            Release(throwIfClosed, false, static stream =>
            {
                stream.EndWrites();
                try
                {
                    // reports a failed buffered write; the file is closed either way
                    stream.Flush();
                }
                finally
                {
                    stream.Dispose();
                }
            });
        }

        public Task CloseAsync(bool throwIfClosed = true)
        {
            return ReleaseAsync(throwIfClosed, false, static async stream =>
            {
                stream.EndWrites();
                try
                {
                    // reports a failed buffered write; the file is closed either way
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                finally
                {
                    stream.Dispose();
                }
            });
        }

        public void Delete(bool throwIfClosed = true)
        {
            Release(throwIfClosed, true, static stream => stream.Delete());
        }

        public Task DeleteAsync(bool throwIfClosed = true)
        {
            return ReleaseAsync(throwIfClosed, true, static stream => stream.DeleteAsync());
        }

        // closes or deletes the stream under the lock FastDB opens this collection's name under, so GetCollection() waits
        // for the file to close rather than return this collection or open the file a second time meanwhile
        private void Release(bool throwIfClosed, bool write, Action<FastDBStream<TKey, TValue>> release)
        {
            _Owner.EnterName(Name);
            try
            {
                FastDBStream<TKey, TValue> stream = BeginRelease(throwIfClosed, write);
                if (stream == null)
                    return;

                try
                {
                    release(stream);
                }
                finally
                {
                    Unload();
                }
            }
            finally
            {
                _Owner.ExitName(Name);
            }
        }

        // the name lock is held across these awaits, so they don't resume on the caller's context: a thread that waits
        // for the same lock there, in GetCollection(), would never let them finish
        private async Task ReleaseAsync(bool throwIfClosed, bool write, Func<FastDBStream<TKey, TValue>, Task> release)
        {
            await _Owner.EnterNameAsync(Name).ConfigureAwait(false);
            try
            {
                FastDBStream<TKey, TValue> stream = BeginRelease(throwIfClosed, write);
                if (stream == null)
                    return;

                try
                {
                    await release(stream).ConfigureAwait(false);
                }
                finally
                {
                    Unload();
                }
            }
            finally
            {
                _Owner.ExitName(Name);
            }
        }

        // caller holds the name's lock: returns the stream to close or delete, or null if the collection is already
        // closed and throwIfClosed is false. From here on the collection is closed, and its FastDB has forgotten it, so
        // neither GetCollection() nor a database-wide call reaches it.
        private FastDBStream<TKey, TValue> BeginRelease(bool throwIfClosed, bool write)
        {
            if (IsClosed && !throwIfClosed)
                return null;

            FastDBStream<TKey, TValue> stream = OpenStream(write);
            IsClosed = true;
            _Owner.Forget(Name, this);
            return stream;
        }

        // caller holds the name's lock, after the stream is released
        private void Unload()
        {
            _FastDBStream = null;
            _Values = null;
        }

        public bool Add(TKey key, TValue value)
        {
            return OpenStream(write: true).Add(key, value);
        }

        public ValueTask<bool> AddAsync(TKey key, TValue value)
        {
            if (!TryOpenStream(true, out FastDBStream<TKey, TValue> stream, out Exception refusal))
                return ValueTask.FromException<bool>(refusal);
            return stream.AddAsync(key, value);
        }

        public ValueTask<bool> AddBulkAsync(IDictionary<TKey, TValue> dictionary)
        {
            if (!TryOpenStream(true, out FastDBStream<TKey, TValue> stream, out Exception refusal))
                return ValueTask.FromException<bool>(refusal);
            return stream.AddBulkAsync(dictionary);
        }

        public bool Update(TKey key, TValue value)
        {
            return OpenStream(write: true).Update(key, value);
        }

        public ValueTask<bool> UpdateAsync(TKey key, TValue value)
        {
            if (!TryOpenStream(true, out FastDBStream<TKey, TValue> stream, out Exception refusal))
                return ValueTask.FromException<bool>(refusal);
            return stream.UpdateAsync(key, value);
        }

        public bool AddOrUpdate(TKey key, TValue value)
        {
            return OpenStream(write: true).AddOrUpdate(key, value);
        }

        public ValueTask<bool> AddOrUpdateAsync(TKey key, TValue value)
        {
            if (!TryOpenStream(true, out FastDBStream<TKey, TValue> stream, out Exception refusal))
                return ValueTask.FromException<bool>(refusal);
            return stream.AddOrUpdateAsync(key, value);
        }

        public bool Remove(TKey key, out TValue value)
        {
            return OpenStream(write: true).Remove(key, out value);
        }

        public ValueTask<bool> RemoveAsync(TKey key)
        {
            if (!TryOpenStream(true, out FastDBStream<TKey, TValue> stream, out Exception refusal))
                return ValueTask.FromException<bool>(refusal);
            return stream.RemoveAsync(key);
        }

        public void RemoveBulk(IEnumerable<TKey> keys)
        {
            OpenStream(write: true).RemoveBulk(keys);
        }

        public ValueTask RemoveBulkAsync(IEnumerable<TKey> keys)
        {
            if (!TryOpenStream(true, out FastDBStream<TKey, TValue> stream, out Exception refusal))
                return ValueTask.FromException(refusal);
            return stream.RemoveBulkAsync(keys);
        }

        public bool TryGet(TKey key, out TValue value)
        {
            return OpenValues().TryGetValue(key, out value);
        }

        public TValue this[TKey key]
        {
            get
            {
                return OpenValues()[key];
            }
            set
            {
                AddOrUpdate(key, value);
            }
        }

        public bool ContainsKey(TKey key)
        {
            return OpenValues().ContainsKey(key);
        }

        public IEnumerator<TValue> GetEnumerator()
        {
            foreach (var kvp in OpenValues())
                yield return kvp.Value;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public IEnumerable<KeyValuePair<TKey, TValue>> AsEnumerable()
        {
            return OpenValues();
        }

        public void Compact()
        {
            OpenStream(write: true).Compact();
        }

        public Task CompactAsync()
        {
            if (!TryOpenStream(true, out FastDBStream<TKey, TValue> stream, out Exception refusal))
                return Task.FromException(refusal);
            return stream.CompactAsync();
        }

        public int Count
        {
            get
            {
                return OpenValues().Count;
            }
        }

        public long GetSizeBytes()
        {
            return OpenStream().GetSizeBytes();
        }

        public ushort Version
        {
            get
            {
                FastDBStream<TKey, TValue> stream = _FastDBStream;
                if (stream != null)
                    return stream.Version;
                else
                    return 0;
            }
        }

        public string FilePath
        {
            get
            {
                FastDBStream<TKey, TValue> stream = _FastDBStream;
                if (stream != null)
                    return stream.FilePath;
                else
                    return null;
            }
        }

        public bool IsEncrypted
        {
            get
            {
                FastDBStream<TKey, TValue> stream = _FastDBStream;
                if (stream != null)
                    return stream.IsEncrypted;
                else
                    return Options.IsEncryptionEnabled;
            }
        }

        public bool IsCompressed
        {
            get
            {
                FastDBStream<TKey, TValue> stream = _FastDBStream;
                if (stream != null)
                    return stream.IsCompressed;
                else
                    return Options.IsCompressionEnabled;
            }
        }

        public override string ToString()
        {
            ConcurrentDictionary<TKey, TValue> values = _Values;
            if (values == null)
                return $"{Name}[closed]";
            return $"{Name}[{values.Count}]";
        }
    }
}
