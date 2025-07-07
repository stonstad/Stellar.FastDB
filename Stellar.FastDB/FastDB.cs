using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.Collections
{
    public sealed class FastDB : IAsyncDisposable
    {
        /// <summary>
        /// Gets the name of this FastDB database.
        /// </summary>
        public string Name { get; private set; } = "FastDB";
        /// <summary>
        /// Specifies if this database is closed.
        /// </summary>
        public bool IsClosed { get; private set; }

        private readonly ConcurrentDictionary<string, IFastDBCollection> _Collections = new ConcurrentDictionary<string, IFastDBCollection>();
        public FastDBOptions Options { get; private set; }

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _CollectionLocks = new ConcurrentDictionary<string, SemaphoreSlim>();

        /// <summary>
        /// Creates a new instance of Stellar.FastDB using default options.
        /// </summary>
        public FastDB() : this(new FastDBOptions())
        {
        }

        /// <summary>
        /// Creates a new instance of Stellar.FastDB using default options and a database name.
        /// </summary>
        /// <param name="databaseName">The name of the database and directory with which to store all files.</param>
        public FastDB(string databaseName) : this(new FastDBOptions() { DatabaseName = databaseName })
        {
        }

        /// <summary>
        /// Creates a new instance of Stellar.FastDB using specified options and a database name.
        /// </summary>
        /// <param name="databaseName">The name of the datasbase and directory with which to store all files.</param>
        /// <param name="options">Options with which to initialize the database.</param>
        public FastDB(string databaseName, FastDBOptions options) : this(new FastDBOptions(options) { DatabaseName = databaseName })
        {
        }

        /// <summary>
        /// Creates a new instance of Stellar.FastDB using specified options.
        /// </summary>
        /// <param name="options"></param>
        public FastDB(FastDBOptions options)
        {
            Options = options;

            if (string.IsNullOrWhiteSpace(Options.DatabaseName))
                throw ThrowHelper.DatabaseNameMayNotBeNullOrEmpty();

            foreach (char c in Options.DatabaseName)
                if (!(char.IsLetterOrDigit(c) || c == ' ' || c == '_'))
                    throw ThrowHelper.DatabaseNameMayNotContainSpecialCharacters(Options.DatabaseName);

            if (Options.IsEncryptionEnabled)
            {
                if (string.IsNullOrEmpty(Options.EncryptionPassword))
                    throw ThrowHelper.EncryptionPasswordRequired();
            }
        }

        /// <summary>
        /// Retrieves a typed collection from the file system. If the collection does not exist, it is created.
        /// </summary>
        /// <typeparam name="TKey"></typeparam>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="collectionName"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        public IFastDBCollection<TKey, TValue> GetCollection<TKey, TValue>(string collectionName = null, FastDBOptions options = null) where TKey : struct
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);

            if (string.IsNullOrEmpty(collectionName))
            {
                collectionName = typeof(TValue).Name;
                if (Options.GeneratedFileNameCreationFunction != null)
                    collectionName = Options.GeneratedFileNameCreationFunction(collectionName);
            }

            options ??= Options;

            if (_Collections.TryGetValue(collectionName, out IFastDBCollection collection))
                return collection as IFastDBCollection<TKey, TValue>;
            else
            {
                var semaphore = _CollectionLocks.GetOrAdd(collectionName, a => new SemaphoreSlim(1, 1));
                semaphore.Wait();

                try
                {
                    if (_Collections.TryGetValue(collectionName, out collection))
                        return collection as IFastDBCollection<TKey, TValue>;

                    FastDBCollection<TKey, TValue> newCollection = new FastDBCollection<TKey, TValue>(collectionName, options);
                    newCollection.Load();
                    _Collections[collectionName] = newCollection;

                    return newCollection;
                }
                finally
                {
                    semaphore.Release();
                }
            }
        }

        /// <summary>
        /// Retrieves a typed collection from the file system. If the collection does not exist, it is created.
        /// </summary>
        /// <typeparam name="TKey"></typeparam>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="collectionName"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        public async Task<IFastDBCollection<TKey, TValue>> GetCollectionAsync<TKey, TValue>(string collectionName = null, FastDBOptions options = null) where TKey : struct
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);

            if (string.IsNullOrEmpty(collectionName))
            {
                collectionName = typeof(TValue).Name;
                if (Options.GeneratedFileNameCreationFunction != null)
                    collectionName = Options.GeneratedFileNameCreationFunction(collectionName);
            }

            options ??= Options;

            if (_Collections.TryGetValue(collectionName, out IFastDBCollection collection))
                return collection as IFastDBCollection<TKey, TValue>;
            else
            {
                var semaphore = _CollectionLocks.GetOrAdd(collectionName, a => new SemaphoreSlim(1, 1));
                await semaphore.WaitAsync();

                try
                {
                    if (_Collections.TryGetValue(collectionName, out collection))
                        return collection as IFastDBCollection<TKey, TValue>;

                    FastDBCollection<TKey, TValue> newCollection = new FastDBCollection<TKey, TValue>(collectionName, options);
                    await newCollection.LoadAsync();
                    _Collections[collectionName] = newCollection;
                    return newCollection;
                }
                finally
                {
                    semaphore.Release();
                }
            }
        }

        /// <summary>
        /// Returns a count of all records associated with all open database collections.
        /// </summary>
        /// <returns></returns>
        public int Count()
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);

            int count = 0;
            foreach (var collection in _Collections)
                count += collection.Value.Count;
            return count;
        }

        /// <summary>
        /// Clears records contained within all open database collections.
        /// </summary>
        public void Clear()
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.DatabaseReadOnly(Name);

            foreach (var collection in _Collections)
                collection.Value.Clear();
        }

        /// <summary>
        /// Performs a blocking flush operation to wait for file system writes to complete.
        /// </summary>
        public void Flush()
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);

            foreach (var collection in _Collections)
                collection.Value.Flush();
        }

        /// <summary>
        /// Performs an asynchronous blocking flush operation to wait for file system writes to complete.
        /// </summary>
        public async Task FlushAsync()
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);

            foreach (var collection in _Collections)
                await collection.Value.FlushAsync();
        }

        /// <summary>
        /// Performs a blocking flush and closes all open database collections.
        /// </summary>
        public void Close()
        {
            if (!IsClosed)
            {
                IsClosed = true;
                foreach (var collection in _Collections)
                    collection.Value.Close();
                _Collections.Clear();
                _CollectionLocks.Clear();
            }
        }

        /// <summary>
        /// Performs an asynchronous blocking flush and closes all open database collections.
        /// </summary>

        public async Task CloseAsync()
        {
            if (!IsClosed)
            {
                IsClosed = true;
                foreach (var collection in _Collections)
                    await collection.Value.CloseAsync();
                _Collections.Clear();
                _CollectionLocks.Clear();
            }
        }

        /// <summary>
        /// Deletes all open database collections.
        /// </summary>
        public void Delete()
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);

            IsClosed = true;
            foreach (var collection in _Collections)
                collection.Value.Delete();
        }

        /// <summary>
        /// Deletes all open database collections.
        /// </summary>
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

        /// <summary>
        /// Returns the file size in bytes of all open database collections.
        /// </summary>
        /// <returns></returns>
        public long GetFileSizeBytes()
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);

            long sizeBytes = 0;
            foreach (var collection in _Collections)
                sizeBytes += collection.Value.GetSizeBytes();
            return sizeBytes;
        }

        /// <summary>
        /// Performs a blocking flush and closes all open database collections.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (!IsClosed)
                await CloseAsync();
        }
    }
}
