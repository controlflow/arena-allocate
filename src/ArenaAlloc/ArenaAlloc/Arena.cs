#nullable enable
using System;
using System.Threading;
using JetBrains.Annotations;

namespace ArenaAlloc;

public sealed class Arena<T>
  where T : class, IArenaParticipant
{
  public Arena(int capacity, Func<T> factory)
  {
    if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));

    myArray = new T[capacity];
    myFreeIndex = -1;
    myFactory = factory;
  }

  private readonly Func<T> myFactory;
  private readonly T?[] myArray;
  private int myFreeIndex;

  public int Capacity => myArray.Length;
  public float Utilization => (float) Math.Min(myFreeIndex + 1, myArray.Length) / Capacity;

  [MustUseReturnValue]
  public T Alloc()
  {
    var array = myArray;

    if (myFreeIndex < array.Length)
    {
      var newFreeIndex = Interlocked.Increment(ref myFreeIndex);
      if (newFreeIndex < array.Length)
      {
        ref var slot = ref array[newFreeIndex];
        return slot ??= myFactory();
      }
    }

    return myFactory();
  }

  public void Reset()
  {
    var array = myArray;
    var lastIndex = myFreeIndex;

    for (var index = 0; index < array.Length; index++)
    {
      if (index > lastIndex)
        break; // only clean what is necessary, do not waste cycles

      array[index]!.ClearAllReferences();
    }

    myFreeIndex = -1;
  }
}