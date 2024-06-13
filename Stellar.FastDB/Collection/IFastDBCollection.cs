using System.Collections.Generic;
using System.Threading.Tasks;

namespace Stellar.Collections
{
    /// <summary>Represents a thread-safe collection of keys and values persisted to the file system.</summary>
    /// <typeparam name="TKey">The type of the keys in the collection.</typeparam>
    /// <typeparam name="TValue">The type of the values in the collection.</typeparam>
    /// <remarks>
    /// All public and protected members of <see cref="IFastDBCollection"/> are thread-safe and may be used
    /// concurrently from multiple threads.
    /// </remarks>    
    public interface IFastDBCollection
    {
        /// <summary>
        /// Loads all elements in the database collection.
        /// </summary>
        
        void Load();
        
        /// <summary>
        /// Loads all elements in the database collection.
        /// </summary>
        Task LoadAsync();
        
        /// <summary>
        /// Removes all elements from the database collection.
        /// </summary>
        /// <summary>
        void Clear();
        
        /// <summary>
        /// Performs a blocking flush operation to wait for file system writes to complete.
        /// </summary>
        void Flush();
        
        /// <summary>
        /// Performs an asynchronous blocking flush operation to wait for file system writes to complete.
        /// </summary>
        Task FlushAsync();
        
        /// <summary>
        /// Performs a blocking flush and closes all open database collections.
        /// </summary>
        void Close();
        
        /// <summary>
        /// Performs an asynchronous blocking flush and closes all open database collections.
        /// </summary>
        Task CloseAsync();
        
        /// <summary>
        /// Deletes the database collection.
        /// </summary>
        void Delete();

        /// <summary>
        /// Deletes the database collection.
        /// </summary>
        Task DeleteAsync();

        Task DefragmentMemoryAsync();

        /// <summary>
        /// Returns the file system size in bytes of the database collection.
        /// </summary>
        /// <returns></returns>
        long GetSizeBytes();

        /// <summary>
        /// Returns the number of records existing in the database collection.
        /// </summary>
        int Count { get; }
    }

    /// <summary>Represents a thread-safe collection of keys and values persisted to the file system.</summary>
    /// <typeparam name="TKey">The type of the keys in the collection.</typeparam>
    /// <typeparam name="TValue">The type of the values in the collection.</typeparam>
    /// <remarks>
    /// All public and protected members of <see cref="IFastDBCollection{TKey,TValue}"/> are thread-safe and may be used
    /// concurrently from multiple threads.
    /// </remarks>    
    public interface IFastDBCollection<TKey, TValue> : IFastDBCollection, IEnumerable<TValue> where TKey : struct
    {
        /// <summary>
        /// Adds an element to the collection.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        bool Add(TKey key, TValue value);

        /// <summary>
        /// Adds an element to the collection
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        Task<bool> AddAsync(TKey key, TValue value);
        
        /// <summary>
        /// Adds one or more elements to the collection.
        /// </summary>
        /// <param name="dictionary"></param>
        /// <returns></returns>
        Task<bool> AddBulkAsync(IDictionary<TKey, TValue> dictionary);
        
        /// <summary>
        /// Updates an existing element in the collection.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        bool Update(TKey key, TValue value);
        
        /// <summary>
        /// Updates an existing element in the collection.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        Task<bool> UpdateAsync(TKey key, TValue value);
        
        /// <summary>
        /// Adds an element to the collection or updates an existing element, if present.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        bool AddOrUpdate(TKey key, TValue value);
        
        /// <summary>
        /// Adds an element to the collection or updates an existing element, if present.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        Task<bool> AddOrUpdateAsync(TKey key, TValue value);
        
        /// <summary>
        /// Removes an element from the collection.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        bool Remove(TKey key, out TValue value);
        
        /// <summary>
        /// Removes one or more elements from the collection.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        Task RemoveBulkAsync(IEnumerable<TKey> keys);

        /// <summary>
        /// Removes one or more elements from the collection.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        void RemoveBulk(IEnumerable<TKey> keys);

        /// <summary>
        /// Removes an element from the collection.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        Task<bool> RemoveAsync(TKey key);

        /// <summary>
        /// Attempts to retrieve an element from the collection. If not present, false is returned.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        bool TryGet(TKey key, out TValue value);
        
        /// <summary>
        /// Retrieve an element from the collection using the specified id. If not present, an exception is thrown.
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        TValue this[TKey key] { get; set; }
        
        /// <summary>
        /// Returns whether a element exists in the collection using the specified key.
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        bool ContainsKey(TKey key);

        /// <summary>
        /// Returns a KeyValuePair<TKey, TValue> enumerator.
        /// </summary>
        IEnumerator<KeyValuePair<TKey, TValue>> GetDictionaryEnumerator();
    }
}
