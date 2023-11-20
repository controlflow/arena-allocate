// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable
using System;
using System.Text;
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

  [MustUseReturnValue]
  public string Add(string chars, int start, int length)
  {
    var source = (chars, start, length);
    return Add<(string chars, int start, int length), StringArrayPartInternSupport>(in source);
  }

  [MustUseReturnValue]
  public string Add(char[] chars, int start, int length)
  {
    var source = (chars, start, length);
    return Add<(char[] chars, int start, int length), CharArrayPartInternSupport>(in source);
  }

  [MustUseReturnValue]
  public string Add(string chars)
  {
    return Add<string, StringInternSupport>(chars);
  }

  [MustUseReturnValue]
  public string Add(char ch)
  {
    return Add<char, CharInternSupport>(ch);
  }

  [MustUseReturnValue]
  public string Add(StringBuilder builder)
  {
    return Add<StringBuilder, StringBuilderInternSupport>(builder);
  }

  [MustUseReturnValue]
  public unsafe string AddUtf8BytesOnlyInternAscii(byte* pointer, int bytesLengthWithoutNullTerminator)
  {
    var ptr = new PointerToUtf8String(pointer, bytesLengthWithoutNullTerminator);
    return Add<PointerToUtf8String, PointerToUtf8StringInternSupport>(in ptr);
  }

  [MustUseReturnValue]
  public unsafe string AddUtf8BytesOnlyInternAscii(byte[] utf8BytesWithoutNullTerminator)
  {
    fixed (byte* data = utf8BytesWithoutNullTerminator)
    {
      var ptr = new PointerToUtf8String(data, utf8BytesWithoutNullTerminator.Length);
      return Add<PointerToUtf8String, PointerToUtf8StringInternSupport>(in ptr);
    }
  }

  [MustUseReturnValue]
  public string Add<TSource, TStringInternSupport>(in TSource source, TStringInternSupport internSupport = default)
    where TStringInternSupport : struct, IStringInternSupport<TSource>
  {
    var hashCode = internSupport.GetFNVHashCode(in source);
    if (hashCode == 0)
      return internSupport.Materialize(in source);

    // capture array to avoid extra range checks
    var localTable = myLocalTable;
    var localIndex = LocalIndexFromHash(hashCode);

    var text = localTable[localIndex].Text;
    if (text != null && localTable[localIndex].HashCode == hashCode)
    {
      var local = localTable[localIndex].Text!;

      if (internSupport.TextEquals(local, in source))
        return local;
    }

    var shared = FindSharedEntry(source, hashCode, internSupport);
    if (shared != null)
    {
      // PERF: the following code does element-wise assignment of a struct
      //       because current JIT produces better code compared to
      //       localTable[localIndex] = new Entry(...)
      localTable[localIndex].HashCode = hashCode;
      localTable[localIndex].Text = shared;

      return shared;
    }

    text = internSupport.Materialize(source);
    AddCore(text, hashCode);
    return text;
  }

  private static string? FindSharedEntry<TSource, TStringInternSupport>(
    in TSource source,
    int hashCode,
    TStringInternSupport internSupport)
    where TStringInternSupport : struct, IStringInternSupport<TSource>
  {
    var sharedArray = ourSharedTable;
    var sharedIndex = SharedIndexFromHash(hashCode);

    string? entry = null;

    // we use quadratic probing here
    // bucket positions are (n^2 + n)/2 relative to the masked hashcode
    for (var index = 1; index < SharedBucketSize + 1; index++)
    {
      entry = sharedArray[sharedIndex].Text;
      var hash = sharedArray[sharedIndex].HashCode;

      if (entry != null)
      {
        if (hash == hashCode && internSupport.TextEquals(entry, in source))
          break;

        // this is not entry we are looking for
        entry = null;
      }
      else
      {
        // once we see unfilled entry, the rest of the bucket will be empty
        break;
      }

      sharedIndex = (sharedIndex + index) & SharedSizeMask;
    }

    return entry;
  }

  private void AddCore(string chars, int hashCode)
  {
    // add to the shared table first (in case someone looks for same item)
    AddSharedEntry(hashCode, chars);

    // add to the local table too
    var array = myLocalTable;
    var index = LocalIndexFromHash(hashCode);
    array[index].HashCode = hashCode;
    array[index].Text = chars;
  }

  private void AddSharedEntry(int hashCode, string text)
  {
    var array = ourSharedTable;
    var index = SharedIndexFromHash(hashCode);

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

  private static int LocalIndexFromHash(int hash)
  {
    return hash & LocalSizeMask;
  }

  private static int SharedIndexFromHash(int hash)
  {
    // we can afford to mix some more hash bits here
    return (hash ^ (hash >> LocalSizeBits)) & SharedSizeMask;
  }

  private int LocalNextRandom()
  {
    return myLocalRandom++;
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
  public static int GetFNVHashCode(string text)
  {
    var hashCode = FnvOffsetBias;

    foreach (var ch in text)
    {
      hashCode = unchecked((hashCode ^ ch) * FnvPrime);
    }

    return hashCode;
  }

  private struct StringBuilderInternSupport : IStringInternSupport<StringBuilder>
  {
    public int GetFNVHashCode(in StringBuilder source)
    {
      var hashCode = FnvOffsetBias;

      for (var index = 0; index < source.Length; index++)
      {
        // slow, no access to chunks
        hashCode = unchecked((hashCode ^ source[index]) * FnvPrime);
      }

      return hashCode;
    }

    public bool TextEquals(string candidate, in StringBuilder source)
    {
      if (candidate.Length != source.Length)
        return false;

      for (var index = 0; index < candidate.Length; index++)
      {
        if (source[index] != candidate[index])
          return false;
      }

      return true;
    }

    public string Materialize(in StringBuilder source)
    {
      return source.ToString();
    }
  }

  private struct StringInternSupport : IStringInternSupport<string>
  {
    int IStringInternSupport<string>.GetFNVHashCode(in string source) => StringTable.GetFNVHashCode(source);
    public bool TextEquals(string candidate, in string source) => candidate == source;
    public string Materialize(in string source) => source;
  }

  private struct CharInternSupport : IStringInternSupport<char>
  {
    int IStringInternSupport<char>.GetFNVHashCode(in char source)
    {
      return unchecked((FnvOffsetBias ^ source) * FnvPrime);
    }

    public bool TextEquals(string candidate, in char source)
    {
      return candidate.Length == 1 && candidate[0] == source;
    }

    public string Materialize(in char source) => source.ToString();
  }

  private struct CharArrayPartInternSupport : IStringInternSupport<(char[] chars, int start, int length)>
  {
    public int GetFNVHashCode(in (char[] chars, int start, int length) source)
    {
      var hashCode = FnvOffsetBias;
      var end = source.start + source.length;
      var chars = source.chars;

      for (var index = source.start; index < end; index++)
      {
        hashCode = unchecked((hashCode ^ chars[index]) * FnvPrime);
      }

      return hashCode;
    }

    public bool TextEquals(string candidate, in (char[] chars, int start, int length) source)
    {
      if (candidate.Length != source.length) return false;

      var chars = source.chars;
      var start = source.start;

      // use array.Length to eliminate the range check
      for (var index = 0; index < candidate.Length; index++)
      {
        if (candidate[index] != chars[start + index])
          return false;
      }

      return true;
    }

    public string Materialize(in (char[] chars, int start, int length) source)
    {
      return new string(source.chars, source.start, source.length);
    }
  }

  private struct StringArrayPartInternSupport : IStringInternSupport<(string chars, int start, int length)>
  {
    public int GetFNVHashCode(in (string chars, int start, int length) source)
    {
      var hashCode = FnvOffsetBias;
      var end = source.start + source.length;
      var chars = source.chars;

      for (var index = source.start; index < end; index++)
      {
        hashCode = unchecked((hashCode ^ chars[index]) * FnvPrime);
      }

      return hashCode;
    }

    public bool TextEquals(string candidate, in (string chars, int start, int length) source)
    {
      if (candidate.Length != source.length) return false;

      var chars = source.chars;
      var start = source.start;

      // use array.Length to eliminate the range check
      for (var index = 0; index < candidate.Length; index++)
      {
        if (candidate[index] != chars[start + index])
          return false;
      }

      return true;
    }

    public string Materialize(in (string chars, int start, int length) source)
    {
      return source.chars.Substring(startIndex: source.start, length: source.length);
    }
  }

  private readonly unsafe struct PointerToUtf8String(byte* pointer, int bytesLengthWithoutNullTerminatorInBytes)
  {
    public readonly byte* Pointer = pointer;
    public readonly int BytesLengthWithoutNullTerminator = bytesLengthWithoutNullTerminatorInBytes;
  }

  private struct PointerToUtf8StringInternSupport : IStringInternSupport<PointerToUtf8String>
  {
    public unsafe int GetFNVHashCode(in PointerToUtf8String source)
    {
      var hashCode = FnvOffsetBias;
      var length = source.BytesLengthWithoutNullTerminator;
      var data = source.Pointer;

      byte asciiMask = 0;

      for (var index = 0; index < length; index++)
      {
        var b = data[index];
        asciiMask |= b;
        hashCode = unchecked((hashCode ^ b) * FnvPrime);
      }

      var isAscii = (asciiMask & 0x80) == 0;
      return isAscii ? hashCode : 0;
    }

    public unsafe bool TextEquals(string candidate, in PointerToUtf8String source)
    {
      if (source.BytesLengthWithoutNullTerminator != candidate.Length)
        return false;

      var ptr = source.Pointer;
      for (var index = 0; index < candidate.Length; index++)
      {
        if (candidate[index] != ptr[index])
          return false;
      }

      return true;
    }

    public unsafe string Materialize(in PointerToUtf8String source)
    {
      return new string(
        value: (sbyte*) source.Pointer,
        startIndex: 0,
        length: source.BytesLengthWithoutNullTerminator,
        enc: Encoding.UTF8);
    }
  }
}

public interface IStringInternSupport<TSource>
{
  /// <summary>
  /// 0 return means "do not intern this string, only materialize"
  /// </summary>
  [Pure] int GetFNVHashCode(in TSource source);
  [Pure] bool TextEquals(string candidate, in TSource source);
  [Pure] string Materialize(in TSource source);
}

