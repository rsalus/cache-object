﻿using lvlup.DataFerry.Collections.Abstractions;
using lvlup.DataFerry.Orchestrators.Abstractions;
using Microsoft.Extensions.Logging;

namespace lvlup.DataFerry.Collections
{
    /// <summary>
    /// A concurrent priority queue implemented as a lock-based SkipList.
    /// </summary>
    /// <typeparam name="TPriority">The key.</typeparam>
    /// <typeparam name="TElement">The value.</typeparam>
    /// <remarks>
    /// <para>
    /// A SkipList is a probabilistic data structure that provides efficient search, insertion, and deletion operations with an expected logarithmic time complexity. 
    /// Unlike balanced search trees (e.g., AVL trees, Red-Black trees), SkipLists achieve efficiency through probabilistic balancing, making them well-suited for concurrent implementations.
    /// </para>
    /// <para>
    /// This concurrent SkipList implementation offers:
    /// </para>
    /// <list type="bullet">
    /// <item>Expected O(log n) time complexity for `ContainsKey`, `TryGetValue`, `TryAdd`, `Update`, and `TryRemove` operations.</item> 
    /// <item>Lock-free and wait-free `ContainsKey` and `TryGetValue` operations.</item>
    /// <item>Lock-free key enumerations.</item>
    /// </list>
    /// <para>
    /// <b>Implementation Details:</b>
    /// </para>
    /// <para>
    /// This implementation employs logical deletion and insertion to optimize performance and ensure thread safety. 
    /// Nodes are marked as deleted logically before being physically removed, and they are inserted level by level to maintain consistency.
    /// </para>
    /// <para>
    /// <b>Invariants:</b>
    /// </para>
    /// <para>
    /// The list at a lower level is always a subset of the list at a higher level. This invariant ensures that nodes are added from the bottom up and removed from the top down.
    /// </para>
    /// <para>
    /// <b>Locking:</b>
    /// </para>
    /// <para>
    /// Locks are acquired in a bottom-up manner to prevent deadlocks. The order of lock release is not critical.
    /// </para>
    /// </remarks>
    public class ConcurrentPriorityQueue<TKey, TPriority> : IConcurrentPriorityQueue<TKey, TPriority>
    {
        /// <summary>
        /// Invalid level.
        /// </summary>
        internal const int InvalidLevel = -1;

        /// <summary>
        /// Bottom level.
        /// </summary>
        internal const int BottomLevel = 0;

        /// <summary>
        /// Default size limit
        /// </summary>
        internal const int DefaultMaxSize = 10000;

        /// <summary>
        /// Default number of levels
        /// </summary>
        internal const int DefaultNumberOfLevels = 32;

        /// <summary>
        /// Default promotion chance for each level. [0, 1).
        /// </summary>
        private const double DefaultPromotionProbability = 0.5;

        /// <summary>
        /// The maximum number of elements allowed in the queue.
        /// </summary>
        private readonly int _maxSize;

        /// <summary>
        /// Number of levels in the skip list.
        /// </summary>
        private readonly int _numberOfLevels;

        /// <summary>
        /// Heighest level allowed in the skip list,
        /// </summary>
        private readonly int _topLevel;

        /// <summary>
        /// The promotion chance for each level. [0, 1).
        /// </summary>
        private readonly double _promotionProbability;

        /// <summary>
        /// The number of nodes in the SkipList.
        /// </summary>
        private int _count;

        /// <summary>
        /// Head of the skip list.
        /// </summary>
        private readonly Node _head;

        /// <summary>
        /// Key comparer used to order the keys.
        /// </summary>
        private readonly IComparer<TPriority> _comparer;

        /// <summary>
        /// Background task processor which handles node removal.
        /// </summary>
        private readonly ITaskOrchestrator _taskOrchestrator;

        /// <summary>
        /// Random number generator.
        /// </summary>
        private static readonly Random RandomGenerator = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="ConcurrentSkipList{TPriority, TElement}"/> class.
        /// </summary>
        /// <param name="keyComparer">The comparer used to compare keys.</param>
        /// <param name="numberOfLevels">The maximum number of levels in the SkipList.</param>
        /// <param name="promotionProbability">The probability of promoting a node to a higher level.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="keyComparer"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="numberOfLevels"/> is less than or equal to 0.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="promotionProbability"/> is less than 0 or greater than 1.</exception>
        public ConcurrentPriorityQueue(ITaskOrchestrator taskOrchestrator, IComparer<TPriority> comparer, int maxSize = 10000, int numberOfLevels = 32, double promotionProbability = 0.5)
        {
            ArgumentNullException.ThrowIfNull(comparer, nameof(comparer));
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(numberOfLevels, 0, nameof(numberOfLevels));
            ArgumentOutOfRangeException.ThrowIfLessThan(promotionProbability, 0, nameof(promotionProbability));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(promotionProbability, 1, nameof(promotionProbability));

            _taskOrchestrator = taskOrchestrator;
            _comparer = comparer;
            _numberOfLevels = numberOfLevels;
            _promotionProbability = promotionProbability;
            _maxSize = maxSize;
            _topLevel = numberOfLevels - 1;

            _head = new Node(Node.NodeType.Head, _topLevel);
            var tail = new Node(Node.NodeType.Tail, _topLevel);

            // Link head to tail at all levels
            for (int level = 0; level <= _topLevel; level++)
            {
                _head.SetNextNode(level, tail);
                Interlocked.Increment(ref _count);
            }

            _head.IsInserted = true;
            tail.IsInserted = true;
        }

        /// <summary>
        /// Schedule the node to be physically deleted.
        /// </summary>
        /// <remarks>Node must already be logically deleted.</remarks>
        /// <param name="node"></param>
        /// <param name="topLevel"></param>
        private void ScheduleNodeRemoval(Node node, int? topLevel = null)
        {
            if (!node.IsDeleted) return;

            _taskOrchestrator.Run(() =>
            {
                // To preserve the invariant that lower levels are super-sets
                // of higher levels, always unlink top to bottom.
                int startingLevel = topLevel ?? node.TopLevel;

                for (int level = startingLevel; level >= 0; level--)
                {
                    Node predecessor = _head;
                    while (predecessor.GetNextNode(level) != node)
                    {
                        predecessor = predecessor.GetNextNode(level);
                    }
                    predecessor.SetNextNode(level, node.GetNextNode(level));
                }
            });
        }

        /// <inheritdoc/>
        public IEnumerator<TKey> GetEnumerator()
        {
            Node current = _head;
            while (true)
            {
                current = current.GetNextNode(BottomLevel);

                // If current is tail, this must be the end of the list.
                if (current.Type == Node.NodeType.Tail) yield break;

                // Takes advantage of the fact that next is set before 
                // the node is physically linked.
                if (!current.IsInserted || current.IsDeleted) continue;

                yield return current.Key;
            }
        }

        /// <inheritdoc/>
        public bool Contains(TKey key)
        {
            ArgumentNullException.ThrowIfNull(key, nameof(key));

            var searchResult = WeakSearch(key);

            // If node is not found, not logically inserted or logically removed, return false.
            return searchResult.IsFound
                && searchResult.GetNodeFound().IsInserted
                && !searchResult.GetNodeFound().IsDeleted;
        }

        /// <inheritdoc/>
        public bool TryGetValue(TKey key, out TPriority value)
        {
            ArgumentNullException.ThrowIfNull(key, nameof(key));

            var searchResult = WeakSearch(key);

            if (searchResult.IsFound 
                && searchResult.GetNodeFound().IsInserted 
                && !searchResult.GetNodeFound().IsDeleted)
            {
                value = searchResult.GetNodeFound().Value;
                return true;
            }

            value = default!;
            return false;
        }

        /// <inheritdoc/>
        public bool TryAdd(TKey key, TPriority value)
        {
            ArgumentNullException.ThrowIfNull(key, nameof(key));

            int insertLevel = GenerateLevel();

            while (true)
            {
                var searchResult = WeakSearch(key);
                if (searchResult.IsFound)
                {
                    if (searchResult.GetNodeFound().IsDeleted)
                    {
                        continue;
                    }

                    // Spin until the duplicate key is logically inserted.
                    WaitUntilIsInserted(searchResult.GetNodeFound());
                    return false;
                }

                int highestLevelLocked = InvalidLevel;
                try
                {
                    bool isValid = true;
                    for (int level = 0; isValid && level <= insertLevel; level++)
                    {
                        var predecessor = searchResult.GetPredecessor(level);
                        var successor = searchResult.GetSuccessor(level);

                        predecessor.Lock();
                        highestLevelLocked = level;

                        // If predecessor is locked and the predecessor is still pointing at the successor, successor cannot be deleted.
                        isValid = IsValidLevel(predecessor, successor, level);
                    }

                    if (isValid == false)
                    {
                        continue;
                    }

                    // Create the new node and initialize all the next pointers.
                    var newNode = new Node(key, value, insertLevel);
                    for (int level = 0; level <= insertLevel; level++)
                    {
                        newNode.SetNextNode(level, searchResult.GetSuccessor(level));
                    }

                    // Ensure that the node is fully initialized before physical linking starts.
                    Thread.MemoryBarrier();

                    for (int level = 0; level <= insertLevel; level++)
                    {
                        // Note that this is required for correctness.
                        // Remove takes a dependency of the fact that if found at expected level, all the predecessors have already been correctly linked.
                        // Hence we only need to use a MemoryBarrier before linking in the top level. 
                        if (level == insertLevel)
                        {
                            Thread.MemoryBarrier();
                        }

                        searchResult.GetPredecessor(level).SetNextNode(level, newNode);
                    }

                    // Linearization point: MemoryBarrier not required since IsInserted is a volatile member (hence implicitly uses MemoryBarrier). 
                    newNode.IsInserted = true;
                    if (Interlocked.Increment(ref _count) > _maxSize) TryRemoveMin(out _);
                    return true;
                }
                finally
                {
                    // Unlock order is not important.
                    for (int level = highestLevelLocked; level >= 0; level--)
                    {
                        searchResult.GetPredecessor(level).Unlock();
                    }
                }
            }
        }

        /// <inheritdoc/>
        public void Update(TKey key, TPriority value)
        {
            ArgumentNullException.ThrowIfNull(key, nameof(key));

            var searchResult = WeakSearch(key);

            if (!searchResult.IsFound 
                || !searchResult.GetNodeFound().IsInserted 
                || searchResult.GetNodeFound().IsDeleted)
            {
                throw new ArgumentException("The key does not exist or is being deleted.", nameof(key));
            }

            Node nodeToBeUpdated = searchResult.GetNodeFound();
            nodeToBeUpdated.Lock();
            try
            {
                if (nodeToBeUpdated.IsDeleted)
                {
                    throw new ArgumentException("The key does not exist or is being deleted.", nameof(key));
                }

                nodeToBeUpdated.Value = value;
            }
            finally
            {
                nodeToBeUpdated.Unlock();
            }
        }

        /// <inheritdoc/>
        public void Update(TKey key, Func<TKey, TPriority, TPriority> updateFunction)
        {
            ArgumentNullException.ThrowIfNull(key, nameof(key));
            ArgumentNullException.ThrowIfNull(updateFunction, nameof(updateFunction));

            var searchResult = WeakSearch(key);

            if (!searchResult.IsFound 
                || !searchResult.GetNodeFound().IsInserted 
                || searchResult.GetNodeFound().IsDeleted)
            {
                throw new ArgumentException("The key does not exist or is being deleted.", nameof(key));
            }

            Node nodeToBeUpdated = searchResult.GetNodeFound();
            nodeToBeUpdated.Lock();
            try
            {
                if (nodeToBeUpdated.IsDeleted)
                {
                    throw new ArgumentException("The key does not exist or is being deleted.", nameof(key));
                }

                nodeToBeUpdated.Value = updateFunction(key, nodeToBeUpdated.Value);
            }
            finally
            {
                nodeToBeUpdated.Unlock();
            }
        }

        /// <inheritdoc/>
        public bool TryRemoveMin(out TKey item)
        {
            while (true)
            {
                Node? nodeToBeDeleted = _head.GetNextNode(0);

                // If the first node is the tail, the list is empty
                if (nodeToBeDeleted.Type == Node.NodeType.Tail)
                {
                    item = default!;
                    return false;
                }

                // Try to delete the node
                nodeToBeDeleted.Lock();
                try
                {
                    if (nodeToBeDeleted.IsDeleted || !nodeToBeDeleted.IsInserted)
                    {
                        // Node is already deleted or not fully linked, retry
                        continue;
                    }

                    // Logically delete and schedule physical deletion
                    nodeToBeDeleted.IsDeleted = true;
                    ScheduleNodeRemoval(nodeToBeDeleted);

                    item = nodeToBeDeleted.Key;
                    Interlocked.Decrement(ref _count);
                    return true;
                }
                finally
                {
                    nodeToBeDeleted.Unlock();
                }
            }
        }

        /// <inheritdoc/>
        public bool TryRemove(TKey key)
        {
            ArgumentNullException.ThrowIfNull(key, nameof(key));

            Node? nodeToBeDeleted = null;
            bool isLogicallyDeleted = false;

            // Level at which the to be deleted node was found.
            int topLevel = InvalidLevel;

            while (true)
            {
                var searchResult = WeakSearch(key);
                nodeToBeDeleted ??= searchResult.GetNodeFound();

                // Ensure node is fully linked and not already deleted
                if (!isLogicallyDeleted
                    && (!nodeToBeDeleted.IsInserted
                        || nodeToBeDeleted.TopLevel != searchResult.LevelFound
                        || nodeToBeDeleted.IsDeleted))
                {
                    return false; // Node not fully linked or already deleted
                }

                // Logically delete the node if not already done
                if (!isLogicallyDeleted)
                {
                    topLevel = searchResult.LevelFound;
                    nodeToBeDeleted.Lock();
                    if (nodeToBeDeleted.IsDeleted)
                    {
                        nodeToBeDeleted.Unlock();
                        return false;
                    }

                    // Linearization point: IsDeleted is volatile.
                    nodeToBeDeleted.IsDeleted = true;
                    isLogicallyDeleted = true;
                }

                int highestLevelLocked = InvalidLevel;
                try
                {
                    bool isValid = true;
                    for (int level = 0; isValid && level <= topLevel; level++)
                    {
                        var predecessor = searchResult.GetPredecessor(level);
                        predecessor.Lock();
                        highestLevelLocked = level;
                        isValid = predecessor.IsDeleted == false && predecessor.GetNextNode(level) == nodeToBeDeleted;
                    }

                    if (isValid is false) continue;

                    ScheduleNodeRemoval(nodeToBeDeleted, topLevel);

                    /* Original implementation; physically delete immediately
                    for (int level = topLevel; level >= 0; level--)
                    {
                        var predecessor = searchResult.GetPredecessor(level);
                        var newLink = nodeToBeDeleted.GetNextNode(level);

                        predecessor.SetNextNode(level, newLink);
                    }
                    */

                    nodeToBeDeleted.Unlock();
                    Interlocked.Decrement(ref _count);
                    return true;
                }
                finally
                {
                    for (int level = highestLevelLocked; level >= 0; level--)
                    {
                        searchResult.GetPredecessor(level).Unlock();
                    }
                }
            }
        }

        /// <inheritdoc/>
        public int GetCount() => _count;

        /// <summary>
        /// Waits (spins) until the specified node is marked as logically inserted, 
        /// meaning it has been physically inserted at every level.
        /// </summary>
        /// <param name="node">The node to wait for.</param>
        private static void WaitUntilIsInserted(Node node)
        {
            SpinWait.SpinUntil(() => node.IsInserted);
        }

        /// <summary>
        /// Generates a random level for a new node based on the promotion probability.
        /// The probability of picking a given level is P(L) = p ^ -(L + 1) where p = PromotionChance.
        /// </summary>
        /// <returns>The generated level.</returns>
        private int GenerateLevel()
        {
            int level = 0;

            while (level < _topLevel && RandomGenerator.NextDouble() <= _promotionProbability)
            {
                level++;
            }

            return level;
        }

        /// <summary>
        /// Performs a lock-free search for the node with the specified key.
        /// </summary>
        /// <param name="key">The key to search for.</param>
        /// <returns>A <see cref="SearchResult"/> instance containing the results of the search.</returns>
        /// <remarks>
        /// <para>
        /// If the key is found, the <see cref="SearchResult.IsFound"/> property will be true, 
        /// and the <see cref="SearchResult.PredecessorArray"/> and <see cref="SearchResult.SuccessorArray"/> 
        /// will contain the predecessor and successor nodes at each level.
        /// </para>
        /// <para>
        /// If the key is not found, the <see cref="SearchResult.IsFound"/> property will be false, 
        /// and the <see cref="SearchResult.PredecessorArray"/> and <see cref="SearchResult.SuccessorArray"/> 
        /// will contain the nodes that would have been the predecessor and successor of the node with the 
        /// specified key if it existed.
        /// </para>
        /// </remarks>
        private SearchResult WeakSearch(TKey key)
        {
            int levelFound = InvalidLevel;
            Node[] predecessorArray = new Node[_numberOfLevels];
            Node[] successorArray = new Node[_numberOfLevels];

            Node predecessor = _head;
            for (int level = _topLevel; level >= 0; level--)
            {
                Node current = predecessor.GetNextNode(level);
                var priority = current.Value;

                while (Compare(current, priority) < 0)
                {
                    predecessor = current;
                    current = predecessor.GetNextNode(level);
                    priority = current.Value;
                }

                /*
                // Compare the priority of the current node with the priority of the node we're searching for
                while (current.Type != Node.NodeType.Tail
                       && Equals(current.Key, key)
                       && _comparer.Compare(priority, current.Value) < 0)
                {
                    predecessor = current;
                    current = predecessor.GetNextNode(level);
                    priority = current.Value; // Update the priority for the next comparison
                }
                */

                // At this point, current is >= searchKey
                if (levelFound == InvalidLevel && Compare(current, priority) == 0)
                {
                    levelFound = level;
                }

                predecessorArray[level] = predecessor;
                successorArray[level] = current;
            }

            return new SearchResult(levelFound, predecessorArray, successorArray);
        }

        private int Compare(Node node, TPriority value)
        {
            return node.Type switch
            {
                Node.NodeType.Head => -1,
                Node.NodeType.Tail => 1,
                _ => _comparer.Compare(node.Value, value)
            };
        }

        private static bool IsValidLevel(Node predecessor, Node successor, int level)
        {
            ArgumentNullException.ThrowIfNull(predecessor, nameof(predecessor));
            ArgumentNullException.ThrowIfNull(successor, nameof(successor));

            return !predecessor.IsDeleted
                   && !successor.IsDeleted
                   && predecessor.GetNextNode(level) == successor;
        }

        /// <summary>
        /// Represents a node in the SkipList.
        /// </summary>
        public class Node
        {
            private readonly Lock nodeLock = new();
            private readonly Node[] nextNodeArray;
            private volatile bool isInserted;
            private volatile bool isDeleted;

            /// <summary>
            /// Initializes a new instance of the <see cref="Node"/> class.
            /// </summary>
            /// <param name="nodeType">The type of the node.</param>
            /// <param name="height">The height (level) of the node.</param>
            public Node(NodeType nodeType, int height)
            {
                Key = default!;
                Value = default!;
                Type = nodeType;
                nextNodeArray = new Node[height + 1];
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="Node"/> class.
            /// </summary>
            /// <param name="key">The key associated with the node.</param>
            /// <param name="value">The value associated with the node.</param>
            /// <param name="height">The height (level) of the node.</param>
            public Node(TKey key, TPriority value, int height)
            {
                Key = key;
                Value = value;
                Type = NodeType.Data;
                nextNodeArray = new Node[height + 1];
            }

            /// <summary>
            /// Defines the types of nodes in the SkipList.
            /// </summary>
            public enum NodeType : byte
            {
                /// <summary>
                /// Represents the head node of the SkipList.
                /// </summary>
                Head = 0,

                /// <summary>
                /// Represents the tail node of the SkipList.
                /// </summary>
                Tail = 1,

                /// <summary>
                /// Represents a regular data node in the SkipList.
                /// </summary>
                Data = 2
            }

            /// <summary>
            /// Gets the key associated with the node.
            /// </summary>
            public TKey Key { get; }

            /// <summary>
            /// Gets or sets the value associated with the node.
            /// </summary>
            public TPriority Value { get; set; }

            /// <summary>
            /// Gets or sets the type of the node.
            /// </summary>
            public NodeType Type { get; set; }

            /// <summary>
            /// Gets or sets a value indicating whether the node has been logically inserted.
            /// </summary>
            public bool IsInserted { get => isInserted; set => isInserted = value; }

            /// <summary>
            /// Gets or sets a value indicating whether the node has been logically deleted.
            /// </summary>
            public bool IsDeleted { get => isDeleted; set => isDeleted = value; }

            /// <summary>
            /// Gets the top level (highest index) of the node in the SkipList.
            /// </summary>
            public int TopLevel => nextNodeArray.Length - 1;

            /// <summary>
            /// Gets the next node at the specified height (level).
            /// </summary>
            /// <param name="height">The height (level) at which to get the next node.</param>
            /// <returns>The next node at the specified height.</returns>
            public Node GetNextNode(int height) => nextNodeArray[height];

            /// <summary>
            /// Sets the next node at the specified height (level).
            /// </summary>
            /// <param name="height">The height (level) at which to set the next node.</param>
            /// <param name="next">The next node to set.</param>
            public void SetNextNode(int height, Node next) => nextNodeArray[height] = next;

            /// <summary>
            /// Acquires the lock associated with the node.
            /// </summary>
            public void Lock() => nodeLock.Enter();

            /// <summary>
            /// Releases the lock associated with the node.
            /// </summary>
            public void Unlock() => nodeLock.Exit();
        }

        /// <summary>
        /// Represents the result of a search operation in the SkipList.
        /// </summary>
        /// <param name="LevelFound">The level at which the key was found (or <see cref="NotFoundLevel"/> if not found).</param>
        /// <param name="PredecessorArray">An array of predecessor nodes at each level.</param>
        /// <param name="SuccessorArray">An array of successor nodes at each level.</param>
        private record SearchResult(int LevelFound, Node[] PredecessorArray, Node[] SuccessorArray)
        {
            /// <summary>
            /// Represents the level value when a key is not found in the SkipList.
            /// </summary>
            public const int NotFoundLevel = -1;

            /// <summary>
            /// Gets a value indicating whether the key was found in the SkipList.
            /// </summary>
            public bool IsFound => LevelFound != NotFoundLevel;

            /// <summary>
            /// Gets the predecessor node at the specified level.
            /// </summary>
            /// <param name="level">The level at which to get the predecessor node.</param>
            /// <returns>The predecessor node at the specified level.</returns>
            /// <exception cref="ArgumentNullException">Thrown if <see cref="PredecessorArray"/> is null.</exception>
            public Node GetPredecessor(int level)
            {
                ArgumentNullException.ThrowIfNull(PredecessorArray, nameof(PredecessorArray));
                return PredecessorArray[level];
            }

            /// <summary>
            /// Gets the successor node at the specified level.
            /// </summary>
            /// <param name="level">The level at which to get the successor node.</param>
            /// <returns>The successor node at the specified level.</returns>
            /// <exception cref="ArgumentNullException">Thrown if <see cref="SuccessorArray"/> is null.</exception>
            public Node GetSuccessor(int level)
            {
                ArgumentNullException.ThrowIfNull(SuccessorArray, nameof(SuccessorArray));
                return SuccessorArray[level];
            }

            /// <summary>
            /// Gets the node that was found during the search.
            /// </summary>
            /// <returns>The node that was found.</returns>
            /// <exception cref="InvalidOperationException">Thrown if the key was not found (<see cref="IsFound"/> is false).</exception>
            public Node GetNodeFound()
            {
                if (!IsFound) throw new InvalidOperationException("Cannot get node found when the key was not found.");

                return SuccessorArray[LevelFound];
            }
        }
    }
}