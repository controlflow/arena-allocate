// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable
using System;
using System.Threading;
using JetBrains.Annotations;

namespace ArenaAlloc;

/// <summary>
/// This is basically a lossy cache of strings that is searchable by strings,
/// string sub ranges, character array ranges or string-builder.
/// </summary>
[PublicAPI]
public class StringTable
{
  // entry in the caches
  private struct Entry
  {
    /// <summary>Hash code of the entry</summary>
    public int HashCode;

    /// <summary>Full text of the item</summary>
    public string? Text;
  }

  // size of local cache
  private const int LocalSizeBits = 11;
  private const int LocalSize = (1 << LocalSizeBits); // 2048
  private const int LocalSizeMask = LocalSize - 1;

  // max size of shared cache
  private const int SharedSizeBits = 16;
  private const int SharedSize = (1 << SharedSizeBits); // 65534
  private const int SharedSizeMask = SharedSize - 1;

  // size of bucket in shared cache (local cache has bucket size 1)
  private const int SharedBucketBits = 4;
  private const int SharedBucketSize = (1 << SharedBucketBits); // 16
  private const int SharedBucketSizeMask = SharedBucketSize - 1;

  // local (L1) cache
  // simple fast and not thread-safe cache
  // with limited size and "last add wins" expiration policy

  // The main purpose of the local cache is to use in long lived
  // single threaded operations with lots of locality (like parsing).
  // Local cache is smaller (and thus faster) and is not affected
  // by cache misses on other threads.
  private readonly Entry[] myLocalTable = new Entry[LocalSize];

  // shared (L2) thread-safe cache
  // slightly slower than local cache
  // we read this cache when having a miss in local cache
  // writes to local cache will update shared cache as well.
  private static readonly Entry[] ourSharedTable = new Entry[SharedSize];

  // essentially a random number
  // the usage pattern will randomly use and increment this
  // the counter is not static to avoid interlocked operations and cross-thread traffic
  private int myLocalRandom = Environment.TickCount;

  public string Add(string chars, int start, int length)
  {
    var hashCode = GetFNVHashCode(chars, start, length);

    // capture array to avoid extra range checks
    var array = myLocalTable;
    var index = LocalIdxFromHash(hashCode);

    var text = array[index].Text;
    if (text != null && array[index].HashCode == hashCode)
    {
      var result = array[index].Text!;
      if (TextEquals(result, chars, start, length))
        return result;
    }

    var shared = FindSharedEntry(chars, start, length, hashCode);
    if (shared != null)
    {
      // PERF: the following code does element-wise assignment of a struct
      //       because current JIT produces better code compared to
      //       array[index] = new Entry(...)
      array[index].HashCode = hashCode;
      array[index].Text = shared;

      return shared;
    }

    return AddItem(chars, start, length, hashCode);
  }

  public string Add(char[] chars, int start, int length)
  {
    var hashCode = GetFNVHashCode(chars, start, length);

    // capture array to avoid extra range checks
    var array = myLocalTable;
    var index = LocalIdxFromHash(hashCode);

    var text = array[index].Text;
    if (text != null && array[index].HashCode == hashCode)
    {
      var result = array[index].Text!;
      if (TextEquals(result, chars, start, length))
        return result;
    }

    var shared = FindSharedEntry(chars, start, length, hashCode);
    if (shared != null)
    {
      // PERF: the following code does element-wise assignment of a struct
      //       because current JIT produces better code compared to
      //       array[index] = new Entry(...)
      array[index].HashCode = hashCode;
      array[index].Text = shared;

      return shared;
    }

    return AddItem(chars, start, length, hashCode);
  }

  public string Add(string chars)
  {
    var hashCode = GetFNVHashCode(chars);

    // capture array to avoid extra range checks
    var array = myLocalTable;
    var index = LocalIdxFromHash(hashCode);

    var text = array[index].Text;
    if (text != null && array[index].HashCode == hashCode)
    {
      var result = array[index].Text;
      if (result == chars)
        return result;
    }

    var shared = FindSharedEntry(chars, hashCode);
    if (shared != null)
    {
      // PERF: the following code does element-wise assignment of a struct
      //       because current JIT produces better code compared to
      //       array[index] = new Entry(...)
      array[index].HashCode = hashCode;
      array[index].Text = shared;

      return shared;
    }

    AddCore(chars, hashCode);
    return chars;
  }

  private static string? FindSharedEntry(string chars, int start, int length, int hashCode)
  {
    var array = ourSharedTable;
    var index = SharedIdxFromHash(hashCode);

    string? entry = null;
    // we use quadratic probing here
    // bucket positions are (n^2 + n)/2 relative to the masked hashcode
    for (var i = 1; i < SharedBucketSize + 1; i++)
    {
      entry = array[index].Text;
      var hash = array[index].HashCode;

      if (entry != null)
      {
        if (hash == hashCode && TextEquals(entry, chars, start, length))
          break;

        // this is not entry we are looking for
        entry = null;
      }
      else
      {
        // once we see unfilled entry, the rest of the bucket will be empty
        break;
      }

      index = (index + i) & SharedSizeMask;
    }

    return entry;
  }

  private static string? FindSharedEntry(char[] chars, int start, int length, int hashCode)
  {
    var array = ourSharedTable;
    var index = SharedIdxFromHash(hashCode);

    string? entry = null;
    // we use quadratic probing here
    // bucket positions are (n^2 + n)/2 relative to the masked hashcode
    for (var i = 1; i < SharedBucketSize + 1; i++)
    {
      entry = array[index].Text;
      var hash = array[index].HashCode;

      if (entry != null)
      {
        if (hash == hashCode && TextEquals(entry, chars, start, length))
          break;

        // this is not entry we are looking for
        entry = null;
      }
      else
      {
        // once we see unfilled entry, the rest of the bucket will be empty
        break;
      }

      index = (index + i) & SharedSizeMask;
    }

    return entry;
  }

  private static string? FindSharedEntry(string chars, int hashCode)
  {
    var array = ourSharedTable;
    var index = SharedIdxFromHash(hashCode);

    string? entry = null;

    // we use quadratic probing here
    // bucket positions are (n^2 + n)/2 relative to the masked hashcode
    for (var i = 1; i < SharedBucketSize + 1; i++)
    {
      entry = array[index].Text;
      var hash = array[index].HashCode;

      if (entry != null)
      {
        if (hash == hashCode && entry == chars)
          break;

        // this is not entry we are looking for
        entry = null;
      }
      else
      {
        // once we see unfilled entry, the rest of the bucket will be empty
        break;
      }

      index = (index + i) & SharedSizeMask;
    }

    return entry;
  }

  private string AddItem(string chars, int startIndex, int length, int hashCode)
  {
    var text = chars.Substring(startIndex, length); // allocation
    AddCore(text, hashCode);
    return text;
  }

  private string AddItem(char[] chars, int startIndex, int length, int hashCode)
  {
    var text = new string(chars, startIndex, length); // allocation
    AddCore(text, hashCode);
    return text;
  }

  private void AddCore(string chars, int hashCode)
  {
    // add to the shared table first (in case someone looks for same item)
    AddSharedEntry(hashCode, chars);

    // add to the local table too
    var array = myLocalTable;
    var index = LocalIdxFromHash(hashCode);
    array[index].HashCode = hashCode;
    array[index].Text = chars;
  }

  private void AddSharedEntry(int hashCode, string text)
  {
    var array = ourSharedTable;
    var index = SharedIdxFromHash(hashCode);

    // try finding an empty spot in the bucket
    // we use quadratic probing here
    // bucket positions are (n^2 + n)/2 relative to the masked hashcode
    var curIndex = index;
    for (var i = 1; i < SharedBucketSize + 1; i++)
    {
      if (array[curIndex].Text == null)
      {
        index = curIndex;
        goto foundIndex;
      }

      curIndex = (curIndex + i) & SharedSizeMask;
    }

    // or pick a random victim within the bucket range
    // and replace with new entry
    var i1 = LocalNextRandom() & SharedBucketSizeMask;
    index = (index + ((i1 * i1 + i1) / 2)) & SharedSizeMask;

    foundIndex:
    array[index].HashCode = hashCode;
    Volatile.Write(ref array[index].Text, text);
  }

  private static int LocalIdxFromHash(int hash)
  {
    return hash & LocalSizeMask;
  }

  private static int SharedIdxFromHash(int hash)
  {
    // we can afford to mix some more hash bits here
    return (hash ^ (hash >> LocalSizeBits)) & SharedSizeMask;
  }

  private int LocalNextRandom()
  {
    return myLocalRandom++;
  }

  [Pure]
  private static bool TextEquals(string array, string text, int start, int length)
  {
    if (array.Length != length) return false;

    // use array.Length to eliminate the range check
    for (var index = 0; index < array.Length; index++)
    {
      if (array[index] != text[start + index])
        return false;
    }

    return true;
  }

  [Pure]
  private static bool TextEquals(string array, char[] text, int start, int length)
  {
    if (array.Length != length) return false;

    // use array.Length to eliminate the range check
    for (var index = 0; index < array.Length; index++)
    {
      if (array[index] != text[start + index])
        return false;
    }

    return true;
  }

  /// <summary>
  /// The offset bias value used in the FNV-1a algorithm
  /// See http://en.wikipedia.org/wiki/Fowler%E2%80%93Noll%E2%80%93Vo_hash_function
  /// </summary>
  public const int FnvOffsetBias = unchecked((int)2166136261);

  /// <summary>
  /// The generative factor used in the FNV-1a algorithm
  /// See http://en.wikipedia.org/wiki/Fowler%E2%80%93Noll%E2%80%93Vo_hash_function
  /// </summary>
  public const int FnvPrime = 16777619;

  [Pure]
  public static int GetFNVHashCode(string text, int start, int length)
  {
    var hashCode = FnvOffsetBias;
    var end = start + length;

    for (var index = start; index < end; index++)
    {
      hashCode = unchecked((hashCode ^ text[index]) * FnvPrime);
    }

    return hashCode;
  }

  [Pure]
  public static int GetFNVHashCode(char[] text, int start, int length)
  {
    var hashCode = FnvOffsetBias;
    var end = start + length;

    for (var index = start; index < end; index++)
    {
      hashCode = unchecked((hashCode ^ text[index]) * FnvPrime);
    }

    return hashCode;
  }

  [Pure]
  public static int GetFNVHashCode(string text)
  {
    var hashCode = FnvOffsetBias;

    foreach (var ch in text)
    {
      hashCode = unchecked((hashCode ^ ch) * FnvPrime);
    }

    return hashCode;
  }
}