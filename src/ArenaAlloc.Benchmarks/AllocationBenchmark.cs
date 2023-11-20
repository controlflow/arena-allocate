#nullable enable
using System;
using ArenaAlloc;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
// ReSharper disable UnusedAutoPropertyAccessor.Global

BenchmarkRunner.Run<AllocationBenchmark>();

[Config(typeof(NetCoreAndFrameworkConfig))]
[MemoryDiagnoser]
public class AllocationBenchmark
{
  private Arena<Token> myArena = null!;

  [Params(15000)]
  public int InstancesToAllocate { get; set; }

  [Params(1000)]
  public int RunsCount { get; set; }

  [Params(10000)]
  public int ArenaCapacity { get; set; }

  [GlobalSetup]
  public void ArenaSetup()
  {
    myArena = new Arena<Token>(capacity: ArenaCapacity, () => new Token());
  }

  [Benchmark]
  public void OrdinaryNew()
  {
    for (var run = 0; run < RunsCount; run++)
    {
      for (var instance = 0; instance < InstancesToAllocate; instance++)
      {
        GC.KeepAlive(new Token());
      }
    }
  }

  [Benchmark]
  public void ArenaAllocator()
  {
    for (var run = 0; run < RunsCount; run++)
    {
      for (var instance = 0; instance < InstancesToAllocate; instance++)
      {
        GC.KeepAlive(myArena.Alloc());
      }

      myArena.Reset();
    }
  }

  private class Token : IArenaParticipant
  {
#pragma warning disable CS0414
    private Token? myNext, myPrev, myParent;
#pragma warning restore CS0414

    public void ClearAllReferences()
    {
      myNext = null;
      myPrev = null;
      myParent = null;
    }
  }
}

internal class NetCoreAndFrameworkConfig : ManualConfig
{
  public NetCoreAndFrameworkConfig()
  {
    AddJob(
      Job.Default.WithId(".NET 8").WithRuntime(CoreRuntime.CreateForNewVersion("net8.0", ".NET 8")),
      Job.Default.WithId(".NET Framework").WithRuntime(ClrRuntime.Net48));
  }
}