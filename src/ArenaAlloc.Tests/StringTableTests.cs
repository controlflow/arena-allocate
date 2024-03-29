// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Text;
using NUnit.Framework;
// ReSharper disable StringLiteralTypo

namespace ArenaAlloc.Tests;

[TestFixture]
public sealed class StringTableTests
{
  [Test]
  public void TestAddSameWithStringBuilderProducesSameStringInstance()
  {
    var st = new StringTable();
    var sb1 = new StringBuilder("goo");
    var sb2 = new StringBuilder("goo");
    var s1 = st.Add(sb1);
    var s2 = st.Add(sb2);
    Assert.AreSame(s1, s2);
  }

  [Test]
  public void TestAddDifferentWithStringBuilderProducesDifferentStringInstance()
  {
    var st = new StringTable();
    var sb1 = new StringBuilder("goo");
    var sb2 = new StringBuilder("bar");
    var s1 = st.Add(sb1);
    var s2 = st.Add(sb2);
    Assert.AreNotSame(s1, s2);
  }

  [Test]
  public void TestAddSameWithVariousInputsProducesSameStringInstance()
  {
    var st = new StringTable();

    var s1 = st.Add(new StringBuilder(" "));
    var s2 = st.Add(' ');
    Assert.AreSame(s1, s2);

    var s3 = st.Add(" ");
    Assert.AreSame(s2, s3);

    var s4 = st.Add(new[] { ' ' }, start: 0, length: 1);
    Assert.AreSame(s3, s4);

    var s5 = st.Add("ABC DEF", 3, 1);
    Assert.AreSame(s4, s5);

    var s6 = st.AddUtf8BytesOnlyInternAscii(Encoding.UTF8.GetBytes(" "));
    Assert.AreSame(s5, s6);
  }

  [Test]
  [SuppressMessage("ReSharper", "UseUtf8StringLiteral")]
  public void TestUtf8()
  {
    var st = new StringTable();

    var s1 = st.Add("hello");
    var s2 = st.AddUtf8BytesOnlyInternAscii(Encoding.UTF8.GetBytes("hello"));
    var s3 = st.AddUtf8BytesOnlyInternAscii(Encoding.UTF8.GetBytes("привет"));
    Assert.AreSame(s1, s2);
    Assert.AreNotSame(s1, s3);
    Assert.AreNotSame(s2, s3);
  }

  [Test]
  public void TestAddSameWithCharProducesSameStringInstance()
  {
    var st = new StringTable();
    var s1 = st.Add(' ');
    var s2 = st.Add(' ');
    Assert.AreSame(s1, s2);
  }

  [Test]
  public void TestSharedAddSameWithStringBuilderProducesSameStringInstance()
  {
    var sb1 = new StringBuilder("goo");
    var sb2 = new StringBuilder("goo");
    var s1 = new StringTable().Add(sb1);
    var s2 = new StringTable().Add(sb2);
    Assert.AreSame(s1, s2);
  }

  [Test]
  public void TestSharedAddSameWithCharProducesSameStringInstance()
  {
    // Regression test for a issue where single-char strings were not being
    // found in the shared table.
    var s1 = new StringTable().Add(' ');
    var s2 = new StringTable().Add(' ');
    Assert.AreSame(s1, s2);
  }

  private static bool TestTextEqualsASCII(string str, string ascii)
  {
    var st = new StringTable();

    var s1 = st.Add(str);

    var asciiBytes = Encoding.ASCII.GetBytes(ascii);
    var s2 = st.AddUtf8BytesOnlyInternAscii(asciiBytes);

    return ReferenceEquals(s1, s2);
  }

  [Test]
  public void TextEquals1()
  {
    Assert.True(TestTextEqualsASCII("", ""));
    Assert.False(TestTextEqualsASCII("a", ""));
    Assert.False(TestTextEqualsASCII("", "a"));
    Assert.True(TestTextEqualsASCII("a", "a"));
    Assert.False(TestTextEqualsASCII("a", "ab"));
    Assert.False(TestTextEqualsASCII("ab", "a"));
    Assert.False(TestTextEqualsASCII("abc", "a"));
    Assert.False(TestTextEqualsASCII("abcd", "a"));
    Assert.False(TestTextEqualsASCII("abcde", "a"));
    Assert.False(TestTextEqualsASCII("abcdef", "a"));
    Assert.False(TestTextEqualsASCII("abcdefg", "a"));
    Assert.False(TestTextEqualsASCII("abcdefgh", "a"));
    Assert.False(TestTextEqualsASCII("a", "ab"));
    Assert.False(TestTextEqualsASCII("a", "abc"));
    Assert.False(TestTextEqualsASCII("a", "abcd"));
    Assert.False(TestTextEqualsASCII("a", "abcde"));
    Assert.False(TestTextEqualsASCII("a", "abcdef"));
    Assert.False(TestTextEqualsASCII("a", "abcdefg"));
    Assert.False(TestTextEqualsASCII("a", "abcdefgh"));
    Assert.False(TestTextEqualsASCII("\u1234", "a"));
    Assert.False(TestTextEqualsASCII("\ud800", "xx"));
    Assert.False(TestTextEqualsASCII("\uffff", ""));
  }

  [Test]
  public void TestAddEqualSubstringsFromDifferentStringsWorks()
  {
    // Make neither of the strings equal to the result of the substring call
    // to test an issue that was surfaced by pooling the wrong string.
    var str1 = "abcd1";
    var str2 = "abcd2";
    var st = new StringTable();

    var s1 = st.Add(str1, start: 0, length: 4);
    var s2 = st.Add(str2, start: 0, length: 4);

    Assert.AreSame(s1, s2);
    Assert.AreEqual("abcd", s1);
  }
}