#nullable enable
namespace ArenaAlloc;

public sealed class Arena<T>
  where T : class, IArenaParticipant
{
  private readonly Func<T> myFactory;
  private readonly T[] myArray;
  private int myFreeIndex;
}

public interface IArenaParticipant
{
  void ClearAllReferences();
}