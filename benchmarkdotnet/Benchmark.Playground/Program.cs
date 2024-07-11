// See https://aka.ms/new-console-template for more information
using BenchmarkDotNet.Running;
using System.Reflection;
using Benchmark.Playground;
using Benchmark.Playground.Helpers;
using Benchmark.Playground.Models;
using Bogus;

//BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
//BenchmarkHelper benchmarkHelper = new();
//benchmarkHelper.SortByNumbers();
//var summary = BenchmarkRunner.Run<SerializationBenchmarks>();

RandomDataGeneratorHelper generatorHelper = new RandomDataGeneratorHelper();
var persons = generatorHelper.GenerateRandomPersons(10);
foreach (var person in persons)
{
    Console.WriteLine($"FullName: {person.FullName}, UserName: {person.UserName}, Email: {person.Email}, DateOfBirth: {person.DateOfBirth.ToShortDateString()}, Phone: {person.Phone}");
}
Console.ReadLine();

