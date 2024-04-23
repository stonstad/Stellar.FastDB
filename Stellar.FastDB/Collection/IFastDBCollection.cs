using System.Collections.Generic;
using System.Threading.Tasks;

namespace Stellar.Collections
{
    public interface IFastDBCollection
    {
        void Load();
        Task LoadAsync();
        void Clear();
        void Flush();
        Task FlushAsync();
        void Close();
        Task CloseAsync();
        void Delete();
        Task DeleteAsync();
        Task DefragmentMemoryAsync();
        long GetSizeBytes();
        int Count { get; }
    }

    public interface IFastDBCollection<TKey, TValue> : IFastDBCollection, IEnumerable<TValue> where TKey : struct
    {
        bool Add(TKey key, TValue value);
        Task<bool> AddAsync(TKey key, TValue value);
        Task<bool> AddBulkAsync(IDictionary<TKey, TValue> dictionary);
        bool Update(TKey key, TValue value);
        Task<bool> UpdateAsync(TKey key, TValue value);
        bool AddOrUpdate(TKey key, TValue value);
        Task<bool> AddOrUpdateAsync(TKey key, TValue value);
        bool Remove(TKey key, out TValue value);
        Task<bool> RemoveAsync(TKey key);
        bool TryGet(TKey key, out TValue value);
        TValue this[TKey key] { get; set; }
        bool ContainsKey(TKey key);
    }
}
