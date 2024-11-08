﻿using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using lvlup.DataFerry.Collections;

namespace lvlup.DataFerry.Tests
{
    [ShortRunJob]
    [MemoryDiagnoser]
    public class LinkedListBenchmarks
    {
        private readonly ConcurrentLinkedList<int> _concurrentList;
        private readonly LinkedList<int> _linkedList;
        private readonly int[] _items;

        public LinkedListBenchmarks()
        {
            _concurrentList = new();
            _linkedList = new();
            _items = Enumerable.Range(0, 100).ToArray();
        }

        /*
        [Config(typeof(Config))]
        public class Config : ManualConfig
        {
            public Config()
            {
                AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
            }
        }
        */

        public static void Main(string[] args)
        {
            // Uncomment to debug
            //_ = BenchmarkSwitcher.FromAssembly(typeof(MemCacheBenchmark).Assembly).Run(args, new DebugInProcessConfig());
            _ = BenchmarkRunner.Run<LinkedListBenchmarks>();
        }

        [Benchmark]
        public void LinkedListInsert()
        {
            for (int i = 0; i < 10; i++)
            {
                _linkedList.AddFirst(i);
            }
        }

        [Benchmark]
        public void ConcurrentInsert()
        {
            for (int i = 0; i < 10; i++)
            {
                _concurrentList.TryInsert(i);
            }
        }

        [Benchmark]
        public void ConcurrentRemove()
        {
            // Pre-populate the lists
            foreach (var item in _items)
            {
                _concurrentList.TryInsert(item);
            }

            for (int i = 0; i < 10; i++)
            {
                _concurrentList.TryRemove(i);
            }
        }

        [Benchmark]
        public void LinkedListRemove()
        {
            // Pre-populate the lists
            foreach (var item in _items)
            {
                _linkedList.AddFirst(item);
            }

            for (int i = 0; i < 10; i++)
            {
                _linkedList.Remove(i);
            }
        }
    }
}
