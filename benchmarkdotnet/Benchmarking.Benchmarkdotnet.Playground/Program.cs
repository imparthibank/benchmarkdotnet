// See https://aka.ms/new-console-template for more information
using BenchmarkDotNet.Running;
using Benchmarking.Benchmarkdotnet.Playground;
using System.Reflection;

BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
BenchmarkHelper benchmarkHelper = new();
benchmarkHelper.SortByNumbers();
