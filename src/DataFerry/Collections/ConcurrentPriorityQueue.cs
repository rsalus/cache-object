﻿using System.Collections.Concurrent;
using System.Collections;
using System.Threading.Channels;
using System.Diagnostics;

namespace lvlup.DataFerry.Collections
{
    public class ConcurrentPriorityQueue<TElement, TPriority> : IProducerConsumerCollection<TElement>
    {
        // Queues
        private readonly PriorityQueue<TElement, TPriority> _queue;
        private readonly ConcurrentQueue<int> _queueLengthHistory;

        // Channels/Timers
        private readonly Channel<bool> _contentionChannel;
        private readonly Timer _batchTimer;

        // Locks/Semaphores
        private Lock[] @stripes;
        private readonly Lock @syncLock = new();
        private readonly SemaphoreSlim _batchSemaphore = new(1, 1);
        
        // Thread-local buffer for batch processing
        private static readonly ThreadLocal<ConcurrentBag<ItemWithPriority>> _threadLocalBuffer = new(() => []);

        // Constants
        private const double ContentionWaitTimeThreshold = 10.0;
        private const int ContentionSizeThreshold = 1000;
        private const int ContentionWindowSize = 10;
        private const int ContentionWindowTime = 50;
        private const int ContentionIndicatorThreshold = 10;

        // Settings
        private readonly int _queueCapacity;
        private long _prevContentionTimestamp;
        private int _contentionIndicators;
        private int _stripeCount = 10;

        public ConcurrentPriorityQueue(
            IComparer<TPriority> comparer, 
            int capacity = 1000,
            int batchInterval = 100)
        {
            _queue = new(comparer);
            _queueLengthHistory = new();
            _queueCapacity = capacity;
            _contentionChannel = Channel.CreateUnbounded<bool>();
            @stripes = new Lock[_stripeCount];
            @stripes = Enumerable.Range(0, _stripeCount)
                .Select(_ => new Lock())
                .ToArray();
            _batchTimer = new Timer(
                async s => await BatchProcessItemsJob(),
                default,
                TimeSpan.FromMilliseconds(batchInterval),
                TimeSpan.FromMilliseconds(batchInterval));

            _ = Task.Run(MonitorContentionAsync);
        }

        private async Task BatchProcessItemsJob()
        {
            // To avoid resource wastage from concurrent jobs in multiple instances,
            // we use a Semaphore to serialize cleanup execution. This mitigates
            // CPU-intensive operations. User-initiated eviction is still allowed.

            await _batchSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                await BatchProcessItems().ConfigureAwait(false);
            }
            finally
            {
                _batchSemaphore.Release();
            }
        }

        private async ValueTask BatchProcessItems()
        {
            if (!Monitor.TryEnter(_batchTimer)) return;

            try
            {
                // Gather updates from all thread-local buffers
                var allUpdates = _threadLocalBuffer.Values
                    .SelectMany(buffer => buffer.ToArray())
                    .ToArray();

                if (allUpdates is null || !allUpdates.Any()) return;

                // Clear all buffers
                foreach (var buffer in _threadLocalBuffer.Values)
                {
                    buffer.Clear();
                }

                // Process the batch
                var blockingCollection = new BlockingCollection<ItemWithPriority>();
                allUpdates.ToList().ForEach(blockingCollection.Add);
                blockingCollection.CompleteAdding();

                // Create tasks according to available resources
                var tasks = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => Task.Run(() =>
                {
                    while (blockingCollection.TryTake(out var item))
                    {
                        InternalTryAdd(item);
                    }
                })).ToArray();

                // Task.WaitAll(tasks);
                await Task.WhenAll(tasks).ConfigureAwait(false);

                // Contention detection
                if (_contentionIndicators > ContentionIndicatorThreshold)
                {
                    _contentionChannel.Writer.TryWrite(true);
                }
            }
            finally
            {
                Monitor.Exit(_batchTimer);
            }
        }

        private async Task MonitorContentionAsync()
        {
            while (await _contentionChannel.Reader.WaitToReadAsync())
            {
                if (_contentionChannel.Reader.TryRead(out _))
                {
                    if (IsContentionDetected())
                    {
                        ResizeStripeBuffer();
                    }
                }
            }
        }

        private bool IsContentionDetected()
        {
            // Lock wait time (average over multiple attempts across stripes)
            double averageLockWaitTime = Enumerable.Range(0, 3)
                .Select(_ =>
                {
                    int stripeIndex = GetStripeIndexForCurrentThread();
                    var stopwatch = Stopwatch.StartNew();
                    lock (@stripes[stripeIndex]) { }
                    return stopwatch.ElapsedMilliseconds;
                })
                .Average();

            // Queue length (using a simple moving average)
            int queueLength = _queue.Count;
            _queueLengthHistory.Enqueue(queueLength);

            if (_queueLengthHistory.Count > ContentionWindowSize)
            {
                _queueLengthHistory.TryDequeue(out _);
            }

            double averageQueueLength = _queueLengthHistory.Average();

            // Is there contention?
            return averageLockWaitTime > ContentionWaitTimeThreshold 
                || averageQueueLength > ContentionSizeThreshold;
        }

        private int GetStripeIndexForCurrentThread()
        {
            int threadId = Environment.CurrentManagedThreadId;
            return threadId % _stripeCount;
        }

        private void ResizeStripeBuffer()
        {
            // Determine the new number of stripes
            int newStripeCount = _stripeCount * 2;

            // Create a new array of stripes
            var newStripes = Enumerable.Range(0, newStripeCount)
                .Select(_ => new Lock())
                .ToArray();

            // Update the stripe count and stripes array
            _stripeCount = newStripeCount;
            @stripes = newStripes;
        }

        public int Count => _queue.UnorderedItems.Skip(0).Count();

        public bool IsSynchronized => true;

#pragma warning disable CS9216
        public object SyncRoot => @syncLock;
#pragma warning restore CS9216

        public bool TryAdd(TElement item, TPriority priority)
        {
            _threadLocalBuffer.Value ??= [];
            _threadLocalBuffer.Value.Add(new(item, priority));
            return true;
        }

        public bool TryTake(out TElement item)
        {
            // Potential contention; increment counter
            if (Environment.TickCount64 - _prevContentionTimestamp < ContentionWindowTime)
            {
                Interlocked.Increment(ref _contentionIndicators);
            }

            // Access the thread-specific lock
            int stripeIndex = GetStripeIndexForCurrentThread();
            @stripes[stripeIndex].Enter();
            try
            {
                // Check for contention
                if (Environment.TickCount64 - _prevContentionTimestamp > ContentionWaitTimeThreshold)
                {
                    _prevContentionTimestamp = Environment.TickCount64;
                }
                else
                {
                    Interlocked.Decrement(ref _contentionIndicators);
                }

                // Check our local buffer and/or main queue
                if (TryLocalBufferOrMainQueue(out item)) return true;
            }
            finally
            {
                @stripes[stripeIndex].Exit();
            }

            return false;
        }

        public void CopyTo(TElement[] array, int index)
        {
            // Acquire all locks
            Enumerable.Range(0, _stripeCount)
                .ToList()
                .ForEach(i => @stripes[i].Enter());

            try
            {
                _queue.UnorderedItems
                    .OrderBy(tuple => tuple.Priority, _queue.Comparer)
                    .Select(tuple => tuple.Element)
                    .ToArray()
                    .CopyTo(array, index);
            }
            finally
            {
                // Release all locks
                Enumerable.Range(0, _stripeCount)
                    .ToList()
                    .ForEach(i => @stripes[i].Exit());
            }
        }

        public void CopyTo(Array array, int index)
        {
            // Acquire all locks
            Enumerable.Range(0, _stripeCount)
                .ToList()
                .ForEach(i => @stripes[i].Enter());

            try
            {
                _queue.UnorderedItems
                    .OrderBy(tuple => tuple.Priority, _queue.Comparer)
                    .Select(tuple => tuple.Element)
                    .ToArray()
                    .CopyTo(array, index);
            }
            finally
            {
                // Release all locks
                Enumerable.Range(0, _stripeCount)
                    .ToList()
                    .ForEach(i => @stripes[i].Exit());
            }
        }

        public TElement[] ToArray()
        {
            // Acquire all locks
            Enumerable.Range(0, _stripeCount)
                .ToList()
                .ForEach(i => @stripes[i].Enter());

            try
            {
                return _queue.UnorderedItems
                    .Select(tuple => tuple.Element)
                    .ToArray();
            }
            finally
            {
                // Release all locks
                Enumerable.Range(0, _stripeCount)
                    .ToList()
                    .ForEach(i => @stripes[i].Exit());
            }
        }

        public IEnumerator<TElement> GetEnumerator()
        {
            // Acquire all locks
            Enumerable.Range(0, _stripeCount)
                .ToList()
                .ForEach(i => @stripes[i].Enter());

            try
            {
                // Return an enumerator that iterates over the elements in priority order
                return _queue.UnorderedItems
                    .OrderBy(tuple => tuple.Priority, _queue.Comparer)
                    .Select(tuple => tuple.Element)
                    .GetEnumerator();
            }
            finally
            {
                // Release all locks
                Enumerable.Range(0, _stripeCount)
                    .ToList()
                    .ForEach(i => @stripes[i].Exit());
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public bool TryAdd(TElement item) => 
            throw new NotImplementedException("Use `TryAdd(TElement item, TPriority priority)` instead.");

        private void InternalTryAdd(ItemWithPriority item)
        {
            // Don't add new items if we are at capacity
            if (Count > _queueCapacity) return;

            // Potential contention; increment counter
            if (Environment.TickCount64 - _prevContentionTimestamp < ContentionWindowTime)
            {
                Interlocked.Increment(ref _contentionIndicators);
            }

            // Access the thread-specific lock
            int stripeIndex = GetStripeIndexForCurrentThread();
            @stripes[stripeIndex].Enter();
            try
            {
                // Enqueue item
                _queue.Enqueue(item.Item, item.Priority);

                // Check for contention
                if (Environment.TickCount64 - _prevContentionTimestamp > ContentionWaitTimeThreshold)
                {
                    _prevContentionTimestamp = Environment.TickCount64;
                }
                else
                {
                    Interlocked.Decrement(ref _contentionIndicators);
                }
            }
            finally
            {
                @stripes[stripeIndex].Exit();
            }
        }

        // We need to check the local buffer for high priority items
        // before TryTake ops since we batch the Enqueue operations.
        private bool TryLocalBufferOrMainQueue(out TElement item)
        {
            item = default!;

            // Check if ANY local buffer has items
            if (!_threadLocalBuffer.Values.Any(buffer => !buffer.IsEmpty))
            {
                return false;
            }

            // Find the candidates to compare against
            ConcurrentBag<ItemWithPriority>? candidateBuffer = null;
            ItemWithPriority? candidateItem = default;

            foreach (var buffer in _threadLocalBuffer.Values)
            {
                var curr = buffer.MaxBy(item => item.Priority, _queue.Comparer);

                if (!curr.Equals(default) && (candidateItem.Equals(default) 
                    || _queue.Comparer.Compare(curr.Priority, candidateItem.Value.Priority) > 0))
                {
                    candidateBuffer = buffer;
                    candidateItem = curr;
                }
            }

            if (candidateBuffer is null || candidateItem is null) return false;

            // Check if the queue has an item with lower priority
            if (_queue.TryPeek(out var existingItem, out var existingPriority))
            {
                switch (_queue.Comparer.Compare(candidateItem.Value.Priority, existingPriority))
                {
                    case < 0: // Existing item has higher priority
                        item = candidateItem.Value.Item;
                        candidateBuffer = new(candidateBuffer.Where(item => !item.Equals(candidateItem.Value)));
                        break;
                    default: // Candidate has higher priority or priorities are equal
                        _queue.TryDequeue(out existingItem, out _);
                        item = existingItem ?? default!;
                        break;
                }
                return true;
            }

            return false;
        }

        public readonly struct ItemWithPriority(TElement item, TPriority priority)
        {
            public TElement Item { get; } = item;
            public TPriority Priority { get; } = priority;
        }
    }
}
