#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace ArenaAlloc.Tests;

[TestFixture]
public class BasicTests
{
  private readonly Random myRandom = new();

  [Test]
  [TestCase(1, 0)]
  [TestCase(1, 1)]
  [TestCase(1, 2)]
  [TestCase(1, 10)]
  [TestCase(2, 1)]
  [TestCase(2, 10)]
  [TestCase(10, 5)]
  [TestCase(10, 10)]
  [TestCase(1000, 100)]
  [TestCase(1000, 2000)]
  public void Intern(int capacity, int instancesToCreate)
  {
    Token.InstancesCount = 0;

    var arena = new Arena<Token>(capacity, factory: static () => new Token());
    var instances1 = new HashSet<Token>(ReferenceEqualityComparer<Token>.Instance);
    Assert.AreEqual(arena.Capacity, capacity);

    for (var index = 0; index < instancesToCreate; index++)
    {
      instances1.Add(arena.Alloc());
    }

    Assert.AreEqual(instances1.Count, instancesToCreate);
    Assert.AreEqual(Token.InstancesCount, instancesToCreate);

    arena.Reset();

    var instances2 = new HashSet<Token>(ReferenceEqualityComparer<Token>.Instance);

    for (var index = 0; index < instancesToCreate; index++)
    {
      instances2.Add(arena.Alloc());
    }

    Assert.AreEqual(instances2.Count, instancesToCreate);

    var arenaInstances = new HashSet<Token>(ReferenceEqualityComparer<Token>.Instance);
    arenaInstances.UnionWith(instances1);
    arenaInstances.IntersectWith(instances2);

    Assert.AreEqual(arenaInstances.Count, Math.Min(capacity, instancesToCreate));

    Assert.That(instances1.Where(x => !arenaInstances.Contains(x)).All(x => x.CleanReferencesCalled == 0));
    Assert.That(instances2.Where(x => !arenaInstances.Contains(x)).All(x => x.CleanReferencesCalled == 0));
    Assert.That(arenaInstances.All(x => x.CleanReferencesCalled == 1));

    arena.Reset();

    Assert.That(arenaInstances.All(x => x.CleanReferencesCalled == 2));

    Assert.AreEqual(Token.InstancesCount, instancesToCreate * 2 - arenaInstances.Count);
  }

  [Test]
  [Repeat(1000)]
  public void MultiCoreTest()
  {
    var capacity = myRandom.Next(100_000, 10_000_000);
    var instancesToAllocate = myRandom.Next(10_000, 1_000_000_000);

    var arena = new Arena<Dummy>(capacity, static () => new Dummy());
    var bag = new ConcurrentBag<Dummy>();

    var instancesLeft = instancesToAllocate;

    Parallel.For(fromInclusive: 0, toExclusive: Environment.ProcessorCount, body: _ =>
    {
      var decrement = Interlocked.Decrement(ref instancesLeft);
      if (decrement > 0)
      {
        bag.Add(arena.Alloc());
      }
    });

    var set = new HashSet<Dummy>(bag, ReferenceEqualityComparer<Dummy>.Instance);
    Assert.AreEqual(set.Count, bag.Count);
  }

  [Test]
  public void WithContext()
  {
    const int capacity = 100;

    var arena1 = new Arena<Dummy>(capacity: capacity, static () => new Dummy());
    var arena2 = new Arena<Dummy, Arena<Dummy>>(capacity: capacity, static arena1 => new Dummy { Inner = arena1.Alloc() });

    for (var index = 0; index < capacity * 2; index++)
    {
      var dummy = arena2.Alloc(arena1);
      Assert.IsNotNull(dummy.Inner);
    }
  }

  private class Token : IArenaParticipant
  {
    public static int InstancesCount;

    public Token()
    {
      InstancesCount++;
    }

    public int CleanReferencesCalled;

    public void ClearAllReferences()
    {
      CleanReferencesCalled++;
    }
  }

  private class Dummy : IArenaParticipant
  {
    [SuppressMessage("ReSharper", "PropertyCanBeMadeInitOnly.Local")]
    public Dummy? Inner { get; set; }

    public Dummy()
    {
      Thread.SpinWait(10);
    }

    public void ClearAllReferences() { }
  }

  private class ReferenceEqualityComparer<T> : IEqualityComparer<T>
    where T : class
  {
    private ReferenceEqualityComparer() { }
    public static IEqualityComparer<T> Instance { get; } = new ReferenceEqualityComparer<T>();

    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
    public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
  }
}