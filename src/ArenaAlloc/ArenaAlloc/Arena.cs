#nullable enable
using System;
using System.Threading;
using JetBrains.Annotations;

namespace ArenaAlloc;

public abstract class ArenaBase<T> where T : IArenaParticipant
{
  protected readonly T?[] Array;
  protected int FreeIndex;

  protected ArenaBase(int capacity)
  {
    if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));

    Array = new T[capacity];
    FreeIndex = -1;
  }

  public int Capacity => Array.Length;
  public float Utilization => (float) Math.Min(FreeIndex + 1, Array.Length) / Capacity;

  public void Reset()
  {
    var array = Array;
    var lastIndex = FreeIndex;

    for (var index = 0; index < array.Length; index++)
    {
      if (index > lastIndex)
        break; // only clean what is necessary, do not waste cycles

      array[index]!.ClearAllReferences();
    }

    FreeIndex = -1;
  }
}

public class Arena<T>(int capacity, [RequireStaticDelegate] Func<T> factory)
  : ArenaBase<T>(capacity)
  where T : class, IArenaParticipant
{
  [MustUseReturnValue]
  public T Alloc()
  {
    var array = Array;

    if (FreeIndex < array.Length)
    {
      var newFreeIndex = Interlocked.Increment(ref FreeIndex);
      if (newFreeIndex < array.Length)
      {
        ref var slot = ref array[newFreeIndex];
        return slot ??= factory();
      }
    }

    return factory();
  }
}

public class Arena<T, TContext>(int capacity, [RequireStaticDelegate] Func<TContext, T> factory)
  : ArenaBase<T>(capacity)
  where T : class, IArenaParticipant
{
  [MustUseReturnValue]
  public T Alloc(TContext context)
  {
    var array = Array;

    if (FreeIndex < array.Length)
    {
      var newFreeIndex = Interlocked.Increment(ref FreeIndex);
      if (newFreeIndex < array.Length)
      {
        ref var slot = ref array[newFreeIndex];
        return slot ??= factory(context);
      }
    }

    return factory(context);
  }
}