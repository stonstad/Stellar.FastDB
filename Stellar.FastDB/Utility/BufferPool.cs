using System.Collections.Concurrent;
using System.Threading;

namespace Stellar.Collections
{
    internal sealed class BufferPool
    {
        private readonly ConcurrentDictionary<int, ConcurrentBag<byte[]>> _Pools = new ConcurrentDictionary<int, ConcurrentBag<byte[]>>();
        private int _Count;

        public byte[] Rent(int size)
        {
            if (_Pools.TryGetValue(size, out var pool) && pool.TryTake(out var instance))
            {
                Interlocked.Decrement(ref _Count);
                return instance;
            }
            else
                return new byte[size];
        }

        public void Return(byte[] item)
        {
            ConcurrentBag<byte[]> pool = _Pools.GetOrAdd(item.Length, _ => new ConcurrentBag<byte[]>());
            Interlocked.Increment(ref _Count);
            pool.Add(item);
        }

        public int Count => _Count;

        public void Clear()
        {
            _Pools.Clear();
        }
    }
}