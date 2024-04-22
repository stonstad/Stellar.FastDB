using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Stellar.Collections
{
    public sealed class FastDB : IAsyncDisposable
    {
        public string Name { get; private set; } = "FastDB";
        public bool IsClosed { get; private set; }

        private ConcurrentDictionary<string, IFastDBCollection> _Collections = new ConcurrentDictionary<string, IFastDBCollection>();
        public FastDBOptions Options { get; private set; }

        private readonly object _CollectionLock = new object();

        public FastDB() : this(new FastDBOptions())
        {
        }

        public FastDB(string databaseName) : this(new FastDBOptions() { DatabaseName = databaseName })
        {
        }

        public FastDB(FastDBOptions options)
        {
            if (options.IsEncryptionEnabled)
            {
                if (string.IsNullOrEmpty(options.EncryptionPassword))
                    throw new ArgumentOutOfRangeException($"{nameof(options.EncryptionPassword)} may not be null");
                if (string.IsNullOrEmpty(options.EncryptionSalt))
                    throw new ArgumentOutOfRangeException($"{nameof(options.EncryptionSalt)} may not be null");
            }

            Options = options;
        }

        public IFastDBCollection<K, V> GetCollection<K, V>(string collectionName = null, FastDBOptions options = null) where K : struct
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);

            if (collectionName == null)
                collectionName = typeof(V).Name;

            if (options == null)
                options = Options;

            if (_Collections.TryGetValue(collectionName, out IFastDBCollection collection))
                return collection as IFastDBCollection<K, V>;
            else
            {
                lock (_CollectionLock)
                {
                    if (_Collections.TryGetValue(collectionName, out collection))
                        return collection as IFastDBCollection<K, V>;
                    else
                    {
                        IFastDBCollection<K, V> newCollection = new FastDBCollection<K, V>(collectionName, options);
                        newCollection.Load();
                        _Collections[collectionName] = newCollection;
                        return newCollection;
                    }
                }
            }
        }

        public async Task<IFastDBCollection<K, V>> GetCollectionAsync<K, V>(string collectionName = null, FastDBOptions options = null) where K : struct
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);

            if (collectionName == null)
                collectionName = typeof(V).Name;

            if (options == null)
                options = Options;

            if (_Collections.TryGetValue(collectionName, out IFastDBCollection collection))
                return collection as IFastDBCollection<K, V>;
            else
            {
                if (_Collections.TryGetValue(collectionName, out collection))
                    return collection as IFastDBCollection<K, V>;
                else
                {
                    IFastDBCollection<K, V> newCollection = new FastDBCollection<K, V>(collectionName, options);
                    await newCollection.LoadAsync();
                    _Collections[collectionName] = newCollection;
                    return newCollection;
                }
            }
        }

        public int Count()
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);

            int count = 0;
            foreach (var collection in _Collections)
                count += collection.Value.Count;
            return count;
        }

        public void Clear()
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.DatabaseReadOnly(Name);

            foreach (var collection in _Collections)
                collection.Value.Clear();
        }

        public void Flush()
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);

            foreach (var collection in _Collections)
                collection.Value.Flush();
        }

        public async Task FlushAsync()
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);

            foreach (var collection in _Collections)
                await collection.Value.FlushAsync();
        }

        public void Close()
        {
            if (!IsClosed)
            {
                IsClosed = true;
                foreach (var collection in _Collections)
                    collection.Value.Close();
                _Collections.Clear();
            }
        }

        public async Task CloseAsync()
        {
            if (!IsClosed)
            {
                IsClosed = true;
                foreach (var collection in _Collections)
                    await collection.Value.CloseAsync();
                _Collections.Clear();
            }
        }

        public void Delete()
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);

            IsClosed = true;
            foreach (var collection in _Collections)
                collection.Value.Delete();
        }

        public async Task DeleteAsync()
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);

            IsClosed = true;
            foreach (var collection in _Collections)
                await collection.Value.DeleteAsync();
        }

        public async Task DefragmentMemoryAsync()
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);

            foreach (var collection in _Collections)
                await collection.Value.DefragmentMemoryAsync();
        }

        public long GetFileSizeBytes()
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);

            long sizeBytes = 0;
            foreach (var collection in _Collections)
                sizeBytes += collection.Value.GetSizeBytes();
            return sizeBytes;
        }

        public async ValueTask DisposeAsync()
        {
            if (!IsClosed)
                await CloseAsync();
        }
    }
}
