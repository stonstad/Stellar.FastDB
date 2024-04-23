using System.Collections.Concurrent;
using System.Threading;

namespace Stellar.Collections
{
    internal class Pool<T> where T : new()
    {
        private readonly ConcurrentBag<T> _Instances = new ConcurrentBag<T>();
        private int _Count;

        public T Rent()
        {
            if (_Instances.TryTake(out T instance))
            {
                Interlocked.Decrement(ref _Count);
                return instance;
            }
            else
                return new T();
        }

        public void Return(T item)
        {
            Interlocked.Increment(ref _Count);
            _Instances.Add(item);
        }

        public int Count => _Count;

        public void Clear()
        {
            _Instances.Clear();
        }
    }
}