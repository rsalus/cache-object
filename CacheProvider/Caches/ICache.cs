﻿using StackExchange.Redis;
using System.Collections.Concurrent;

namespace CacheProvider.Caches
{
    /// <summary>
    /// A cache interface for caching arbitrary objects.
    /// </summary>
    public interface ICache
    {
        /// <summary>
        /// Asynchronously retrieves an item from the cache using a key.
        /// </summary>
        /// <remarks>
        /// Returns the item if it exists in the cache, null otherwise.
        /// </remarks>
        /// <param name="key">The key of the item to retrieve.</param>
        Task<T?> GetItemAsync<T>(string key);

        /// <summary>
        /// Asynchronously adds an item to the cache with a specified key.
        /// </summary>
        /// <remarks>
        /// Returns true if the item was added to the cache, false otherwise.
        /// </remarks>
        /// <param name="key">The key to use for the item.</param>
        /// <param name="item">The item to add to the cache.</param>
        Task<bool> SetItemAsync<T>(string key, T item);

        /// <summary>
        /// Asynchronously removes an item from the cache using a key.
        /// </summary>
        /// <remarks>
        /// Returns true if the item was removed from the cache, false otherwise.
        /// </remarks>
        /// <param name="key">The key of the item to remove.</param>
        Task<bool> RemoveItemAsync(string key);

        /// <summary>
        /// Retrieves an object representation of the cache. The cache may be a local cache or a distributed cache.
        /// </summary>
        /// <remarks>
        /// In <see cref="LocalCache"/>, the cache object returned is a <see cref="ConcurrentDictionary{TKey, TValue}"/>.
        /// In <see cref="DistributedCache"/>, the cache object returned is a Redis <see cref="IDatabase"/>.
        /// </remarks>
        object GetCache();
    }
}
