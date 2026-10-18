using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
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

        // the open collections: each forgets itself as it closes or is deleted
        private readonly ConcurrentDictionary<string, IFastDBCollection> _Collections = new ConcurrentDictionary<string, IFastDBCollection>();
        public FastDBOptions Options { get; private set; }

        // a name's collection is opened, closed and deleted under its lock, so its file is never open twice. A lock lives
        // only while it's held or awaited, so names used once don't pile up.
        private sealed class NameLock
        {
            public readonly SemaphoreSlim Semaphore = new SemaphoreSlim(1, 1);
            public int Users;                   // holders and waiters, under _NameLocks
        }

        private readonly Dictionary<string, NameLock> _NameLocks = new Dictionary<string, NameLock>();

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
                if (!(char.IsLetterOrDigit(c) || c == ' ' || c == '.' || c == '_' || c == '\'' || c == '!'))
                    throw ThrowHelper.DatabaseNameMayNotContainSpecialCharacters(Options.DatabaseName);

            if (Options.IsEncryptionEnabled)
            {
                if (string.IsNullOrEmpty(Options.EncryptionPassword))
                    throw ThrowHelper.EncryptionPasswordRequired();
            }
        }

        /// <summary>
        /// Reopens a FastDB instance that was previously closed. 
        /// This method should only be used if the database has been explicitly closed earlier.
        /// </summary>
        public void Load()
        {
            IsClosed = false;
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

            collectionName = CollectionName<TValue>(collectionName);
            IFastDBCollection<TKey, TValue> collection = OpenCollection<TKey, TValue>(collectionName);
            if (collection != null)
                return collection;

            FastDBCollection<TKey, TValue> newCollection;
            EnterName(collectionName);
            try
            {
                collection = OpenCollection<TKey, TValue>(collectionName);
                if (collection != null)
                    return collection;

                newCollection = new FastDBCollection<TKey, TValue>(this, collectionName, options ?? Options);
                newCollection.Load();
                _Collections[collectionName] = newCollection;
            }
            finally
            {
                ExitName(collectionName);
            }

            if (ClosedWhileOpening())
            {
                newCollection.Close(false);
                throw ThrowHelper.DatabaseClosed(Name);
            }
            return newCollection;
        }

        /// <summary>
        /// Retrieves a typed collection from the file system. If the collection does not exist, it is created.
        /// </summary>
        /// <typeparam name="TKey"></typeparam>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="collectionName"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        public async Task<IFastDBCollection<TKey, TValue>> GetCollectionAsync<TKey, TValue>(FastDBOptions options = null) where TKey : struct
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);

            return await GetCollectionAsync<TKey, TValue>(CollectionName<TValue>(null), options);
        }

        /// <summary>
        /// Retrieves a typed collection from the file system. If the collection does not exist, it is created.
        /// </summary>
        /// <typeparam name="TKey"></typeparam>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="collectionName"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        public async Task<IFastDBCollection<TKey, TValue>> GetCollectionAsync<TKey, TValue>(string collectionName, FastDBOptions options = null) where TKey : struct
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);

            if (string.IsNullOrEmpty(collectionName))
                throw new ArgumentNullException(nameof(collectionName));

            IFastDBCollection<TKey, TValue> collection = OpenCollection<TKey, TValue>(collectionName);
            if (collection != null)
                return collection;

            // a name lock is held across these awaits, so they don't resume on the caller's context: a thread that waits
            // for the same lock there would never let them finish
            FastDBCollection<TKey, TValue> newCollection;
            await EnterNameAsync(collectionName).ConfigureAwait(false);
            try
            {
                collection = OpenCollection<TKey, TValue>(collectionName);
                if (collection != null)
                    return collection;

                newCollection = new FastDBCollection<TKey, TValue>(this, collectionName, options ?? Options);
                await newCollection.LoadAsync().ConfigureAwait(false);
                _Collections[collectionName] = newCollection;
            }
            finally
            {
                ExitName(collectionName);
            }

            if (ClosedWhileOpening())
            {
                await newCollection.CloseAsync(false).ConfigureAwait(false);
                throw ThrowHelper.DatabaseClosed(Name);
            }
            return newCollection;
        }

        // the collection's name, from its value type when none is given
        private string CollectionName<TValue>(string collectionName)
        {
            if (!string.IsNullOrEmpty(collectionName))
                return collectionName;

            collectionName = typeof(TValue).Name;
            if (Options.GeneratedFileNameCreationFunction != null)
                collectionName = Options.GeneratedFileNameCreationFunction(collectionName);
            return collectionName;
        }

        // the open collection with this name, or null if none is open
        private IFastDBCollection<TKey, TValue> OpenCollection<TKey, TValue>(string collectionName) where TKey : struct
        {
            if (!_Collections.TryGetValue(collectionName, out IFastDBCollection collection))
                return null;
            if (collection is IFastDBCollection<TKey, TValue> typed)
                return typed;
            throw ThrowHelper.CollectionTypeMismatch(collectionName, typeof(TKey), typeof(TValue));
        }

        // after registering a new collection: a Close() or Delete() that started meanwhile may have missed it. Either this
        // sees IsClosed or they see the collection, as each side fences between its write and its read.
        private bool ClosedWhileOpening()
        {
            Interlocked.MemoryBarrier();
            return IsClosed;
        }

        // before Close() or Delete() reads the collections; see ClosedWhileOpening()
        private void MarkClosed()
        {
            IsClosed = true;
            Interlocked.MemoryBarrier();
        }

        internal void EnterName(string collectionName)
        {
            AddNameUser(collectionName).Semaphore.Wait();
        }

        internal Task EnterNameAsync(string collectionName)
        {
            return AddNameUser(collectionName).Semaphore.WaitAsync();
        }

        internal void ExitName(string collectionName)
        {
            NameLock nameLock;
            lock (_NameLocks)
                nameLock = _NameLocks[collectionName];
            nameLock.Semaphore.Release();
            RemoveNameUser(collectionName);
        }

        private NameLock AddNameUser(string collectionName)
        {
            lock (_NameLocks)
            {
                if (!_NameLocks.TryGetValue(collectionName, out NameLock nameLock))
                {
                    nameLock = new NameLock();
                    _NameLocks.Add(collectionName, nameLock);
                }
                nameLock.Users++;
                return nameLock;
            }
        }

        private void RemoveNameUser(string collectionName)
        {
            lock (_NameLocks)
            {
                if (--_NameLocks[collectionName].Users == 0)
                    _NameLocks.Remove(collectionName);
            }
        }

        // a collection closing or being deleted, under its name's lock: GetCollection() opens the name afresh from now on
        internal void Forget(string collectionName, IFastDBCollection collection)
        {
            _Collections.TryRemove(new KeyValuePair<string, IFastDBCollection>(collectionName, collection));
        }

        // runs a database-wide call on each open collection, skipping one that closes on its own meanwhile rather than
        // fail on it; any other failure is thrown
        private void ForEachOpen(Action<IFastDBCollection> action)
        {
            foreach (IFastDBCollection collection in _Collections.Values)
            {
                try
                {
                    action(collection);
                }
                catch (InvalidOperationException) when (collection.IsClosed)
                {
                }
            }
        }

        private async Task ForEachOpenAsync(Func<IFastDBCollection, Task> action)
        {
            foreach (IFastDBCollection collection in _Collections.Values)
            {
                try
                {
                    await action(collection);
                }
                catch (InvalidOperationException) when (collection.IsClosed)
                {
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
            ForEachOpen(collection => count += collection.Count);
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

            ForEachOpen(collection => collection.Clear());
        }

        /// <summary>
        /// Performs a blocking flush operation to wait for file system writes of all open database collections to
        /// complete.
        /// </summary>
        public void Flush()
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);

            ForEachOpen(collection => collection.Flush());
        }

        /// <summary>
        /// Performs an asynchronous blocking flush operation to wait for file system writes of all open database
        /// collections to complete.
        /// </summary>
        public async Task FlushAsync()
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);

            await ForEachOpenAsync(collection => collection.FlushAsync());
        }

        /// <summary>
        /// Performs a blocking flush and closes all open database collections, including any a failed Delete() left
        /// open.
        /// </summary>
        public void Close()
        {
            MarkClosed();
            ReleaseCollections(collection => collection.Close(false));
        }

        /// <summary>
        /// Performs an asynchronous blocking flush and closes all open database collections, including any a failed
        /// Delete() left open.
        /// </summary>
        public async Task CloseAsync()
        {
            MarkClosed();
            await ReleaseCollectionsAsync(collection => collection.CloseAsync(false));
        }

        /// <summary>
        /// Deletes all open database collections. A collection that can't be deleted, such as one opened read-only,
        /// stays open until Close().
        /// </summary>
        public void Delete()
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.DatabaseReadOnly(Name);

            MarkClosed();
            ReleaseCollections(collection => collection.Delete(false));
        }

        /// <summary>
        /// Deletes all open database collections. A collection that can't be deleted, such as one opened read-only,
        /// stays open until Close().
        /// </summary>
        public async Task DeleteAsync()
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.DatabaseReadOnly(Name);

            MarkClosed();
            await ReleaseCollectionsAsync(collection => collection.DeleteAsync(false));
        }

        // closes or deletes every open collection even if one fails, then rethrows the first failure. Each collection
        // forgets itself once it's closed, so one that refused stays open.
        private void ReleaseCollections(Action<IFastDBCollection> release)
        {
            ExceptionDispatchInfo error = null;
            foreach (IFastDBCollection collection in _Collections.Values)
            {
                try
                {
                    release(collection);
                }
                catch (Exception ex)
                {
                    error ??= ExceptionDispatchInfo.Capture(ex);
                }
            }

            error?.Throw();
        }

        private async Task ReleaseCollectionsAsync(Func<IFastDBCollection, Task> release)
        {
            ExceptionDispatchInfo error = null;
            foreach (IFastDBCollection collection in _Collections.Values)
            {
                try
                {
                    await release(collection);
                }
                catch (Exception ex)
                {
                    error ??= ExceptionDispatchInfo.Capture(ex);
                }
            }

            error?.Throw();
        }

        /// <summary>
        /// Compacts all open database collections, reclaiming the space held by overwritten and removed records. A
        /// collection holding records that couldn't be read when it was opened can't be compacted, and is skipped.
        /// </summary>
        public void Compact()
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.DatabaseReadOnly(Name);

            ForEachOpen(static collection =>
            {
                try
                {
                    collection.Compact();
                }
                catch (CompactionBlockedException)
                {
                }
            });
        }

        /// <summary>
        /// Compacts all open database collections, reclaiming the space held by overwritten and removed records. A
        /// collection holding records that couldn't be read when it was opened can't be compacted, and is skipped.
        /// </summary>
        public async Task CompactAsync()
        {
            if (IsClosed)
                throw ThrowHelper.DatabaseClosed(Name);
            if (Options.IsReadOnlyEnabled)
                throw ThrowHelper.DatabaseReadOnly(Name);

            await ForEachOpenAsync(static async collection =>
            {
                try
                {
                    await collection.CompactAsync();
                }
                catch (CompactionBlockedException)
                {
                }
            });
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
            ForEachOpen(collection => sizeBytes += collection.GetSizeBytes());
            return sizeBytes;
        }

        /// <summary>
        /// Performs a blocking flush and closes all open database collections.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await CloseAsync();
        }
    }
}
