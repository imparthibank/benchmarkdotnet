using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BenchmarkDotNet;
using BenchmarkDotNet.Attributes;

namespace Benchmarking.Benchmarkdotnet.Playground
{
    [MemoryDiagnoser]
    [ShortRunJob]
    public class BenchmarkHelper
    {
        List<int>? _list;
        [Params(1_000, 10_000,1_00_000,10_00_000)]        
        public int ListSize;
        [GlobalSetup]
        public void Setup()
        {
            _list = new List<int>();
            for (int i = 0; i < ListSize; i++)
            {
                _list.Add(i);
            }
        }
        [Benchmark]
        public void SortByNumbers()
        {

            _list!.Sort();
        }

    }
}
