using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Stellar.Collections
{
    internal sealed partial class FastDBStream<TKey, TValue> where TKey : struct
    {
        private CancellationTokenSource _CancellationTokenSource;

        // limit parallelization
        private SemaphoreSlim _LimitParallelizationSemaphore;
        private int _SequentialId = 0;

        // serialization channel
        private Task _SerializationChannelTask = null;
        private Channel<QueueEntry> _SerializationChannel;
        private ManualResetEventSlim _SerializationChannelEmpty = new ManualResetEventSlim(true);

        private void StartSerializationChannel()
        {
            ChannelReader<QueueEntry> serializationChannelReader = _SerializationChannel.Reader;
            _SerializationChannelTask = Task.Run(async () =>
            {
                while (!_CancellationTokenSource.IsCancellationRequested)
                    while (await serializationChannelReader.WaitToReadAsync(_CancellationTokenSource.Token))
                    {
                        _SerializationChannelEmpty.Reset();
                        while (serializationChannelReader.TryRead(out QueueEntry entry))
                        {
                            await _LimitParallelizationSemaphore.WaitAsync(_CancellationTokenSource.Token); // limit this path to N parallel tasks
                            int squentialId = Interlocked.Increment(ref _SequentialId);
                            _ = Task.Run(() =>
                            {
                                if (entry.Type == BufferOperationType.Add || entry.Type == BufferOperationType.Update)
                                    entry.Bytes = Serialize(entry.Key, entry.Value);

                                if (_SequentialProcessingChannel.TryAdd(squentialId, entry))
                                    _SequentialSemaphore.Release();
                                else
                                    throw new Exception();
                                _LimitParallelizationSemaphore.Release();
                            });
                        }
                        _SerializationChannelEmpty.Set();
                    }
            }, _CancellationTokenSource.Token);
        }

        // sequential processing channel
        private Task _SequentialProcessingTask;
        private ConcurrentDictionary<int, QueueEntry> _SequentialProcessingChannel;
        private SemaphoreSlim _SequentialSemaphore = new SemaphoreSlim(1);
        private int _CurrentSequentialId = 1;
        private ManualResetEventSlim _SequentialProcessingChannelEmpty = new ManualResetEventSlim(true);
        private void StartSequentialProcessingChannel()
        {
            ChannelWriter<QueueEntry> fileSystemChannelWriter = _FileSystemChannel.Writer;

            _SequentialProcessingTask = Task.Run(async () =>
            {
                while (!_CancellationTokenSource.IsCancellationRequested)
                {
                    await _SequentialSemaphore.WaitAsync(_CancellationTokenSource.Token);
                    if (!_SequentialProcessingChannel.IsEmpty)
                    {
                        _SequentialProcessingChannelEmpty.Reset();
                        while (!_CancellationTokenSource.IsCancellationRequested && _SequentialProcessingChannel.TryRemove(_CurrentSequentialId, out QueueEntry entry))
                        {
                            if (fileSystemChannelWriter.TryWrite(entry))
                                Interlocked.Increment(ref _CurrentSequentialId);
                            else
                                throw new Exception();
                        }
                    }
                    if (_SequentialProcessingChannel.IsEmpty)
                        _SequentialProcessingChannelEmpty.Set();
                }
            }, _CancellationTokenSource.Token);
        }

        // file system channel
        private Task _FileSystemChannelTask = null;
        private Channel<QueueEntry> _FileSystemChannel;
        private ManualResetEventSlim _FileSystemChannelEmpty = new ManualResetEventSlim(true);

        private void StartFileSystemChannel()
        {
            ChannelReader<QueueEntry> fileSystemChannelReader = _FileSystemChannel.Reader; // single reader

            _FileSystemChannelTask = Task.Run(async () =>
            {
                while (!_CancellationTokenSource.IsCancellationRequested)
                    while (await fileSystemChannelReader.WaitToReadAsync(_CancellationTokenSource.Token))
                    {
                        _FileSystemChannelEmpty.Reset();
                        while (fileSystemChannelReader.TryRead(out QueueEntry entry))
                        {
                            if (entry.Type == BufferOperationType.Add)
                                AddInternal(entry.Key, entry.Value, entry.Bytes);
                            else if (entry.Type == BufferOperationType.Remove)
                                RemoveInternal(entry.Key);
                            else if (entry.Type == BufferOperationType.Update)
                                UpdateInternal(entry.Key, entry.Value, entry.Bytes);

                            entry.Clear();
                            _QueueEntryPool.Return(entry);
                        }
                        _FileSystemChannelEmpty.Set();
                    }
            }, _CancellationTokenSource.Token);
        }

        private void ResetSemaphores() // enables locking
        {
            if (Options.BufferMode == BufferModeType.WriteEnabled)
            {
                _FileSystemChannelEmpty.Reset();
            }
            else if (Options.BufferMode == BufferModeType.WriteParallelEnabled)
            {
                _SerializationChannelEmpty.Reset();
                _SequentialProcessingChannelEmpty.Reset();
                _FileSystemChannelEmpty.Reset();
            }
        }

        private void StartTasks()
        {
            if (Options.BufferMode != BufferModeType.Disabled)
            {
                _FileSystemChannel = Channel.CreateUnbounded<QueueEntry>(new UnboundedChannelOptions() { SingleReader = true, SingleWriter = false });
                _FileSystemChannelEmpty.Set();
                StartFileSystemChannel();

                if (Options.BufferMode == BufferModeType.WriteParallelEnabled)
                {
                    _SequentialId = 0;
                    _CurrentSequentialId = 1;

                    _LimitParallelizationSemaphore = new SemaphoreSlim(Options.MaxDegreeOfParallelism, Options.MaxDegreeOfParallelism);

                    _SerializationChannel = Channel.CreateUnbounded<QueueEntry>(new UnboundedChannelOptions() { SingleReader = true, SingleWriter = false });
                    _SerializationChannelEmpty.Set();
                    
                    _SequentialProcessingChannel = new ConcurrentDictionary<int, QueueEntry>();
                    _SequentialProcessingChannelEmpty.Set();

                    StartSerializationChannel();
                    StartSequentialProcessingChannel();
                }
            }
        }

        private void CancelTasks()
        {
            if (_CancellationTokenSource != null)
                _CancellationTokenSource.Cancel(false);
        }
    }
}