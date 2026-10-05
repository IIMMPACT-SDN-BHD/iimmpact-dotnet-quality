using Xunit;

namespace IIMMPACT.CodeQuality.Tool.Tests;

/// <summary>
/// Declaration attribution assigns a diagnostic position to its owning declaration.
/// Tests use real C# sources so overloading, nesting and comments behave correctly.
/// </summary>
public class DeclarationIndexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"iimmpact-decl-tests-{Guid.NewGuid():N}");

    public DeclarationIndexTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private DeclarationIndex Index(string source)
    {
        var path = Path.Combine(_dir, "Source.cs");
        File.WriteAllText(path, source);
        return DeclarationIndex.Build(path, new CompilationKey("P.csproj", "net8.0"), "Source.cs");
    }

    [Fact]
    public void Attributes_PositionToEnclosingMethod()
    {
        var index = Index("""
            public static class Foo
            {
                public static int Run(int x)
                {
                    return x + 1;
                }
            }
            """);
        var owner = index.OwnerOf(line: 5, column: 9);
        Assert.NotNull(owner);
        Assert.Contains("method", owner);
        Assert.Contains("Run(int)", owner);
        Assert.Contains("class Foo", owner);
    }

    [Fact]
    public void Attributes_PositionToFile_WhenOutsideAnyDeclaration()
    {
        var index = Index("""
            // file header comment
            public static class Foo
            {
            }
            """);
        var owner = index.OwnerOf(line: 1, column: 1);
        Assert.Null(owner);
    }

    [Fact]
    public void Overloads_GetDistinctIdentities()
    {
        var index = Index("""
            public static class Foo
            {
                public static int Run(int x) => x;
                public static int Run(string s) => s.Length;
                public static int Run(int x, int y) => x + y;
            }
            """);
        var first = index.OwnerOf(3, 30);
        var second = index.OwnerOf(4, 30);
        var third = index.OwnerOf(5, 30);
        Assert.NotEqual(first, second);
        Assert.NotEqual(first, third);
        Assert.NotEqual(second, third);
        Assert.Contains("Run(int)", first);
        Assert.Contains("Run(string)", second);
        Assert.Contains("Run(int,int)", third);
    }

    [Fact]
    public void RefKindsAccessorsLocalsAndIndexers_GetDistinctIdentities()
    {
        var index = Index("""
            public class C {
              public int P { get { return 1; } }
              public int Q { get { return 2; } }
              public int Run(int x) => x;
              public int Run(ref int x) => x;
              public int A(int x) { int Local(int n) => n; return Local(x); }
              public int B(int x) { int Local(int n) => n; return Local(x); }
              public int this[int i] => i;
              public int this[string s] => s.Length;
            }
            """);

        Assert.NotEqual(index.OwnerOf(2, 18), index.OwnerOf(3, 18));
        Assert.NotEqual(index.OwnerOf(4, 25), index.OwnerOf(5, 29));
        Assert.NotEqual(index.OwnerOf(6, 40), index.OwnerOf(7, 40));
        Assert.NotEqual(index.OwnerOf(8, 20), index.OwnerOf(9, 20));
    }

    [Fact]
    public void GenericArity_IsPartOfIdentity()
    {
        var index = Index("""
            public static class Foo
            {
                public static T Pick<T>(T a) => a;
                public static T Pick<T, U>(T a, U b) => a;
            }
            """);
        var single = index.OwnerOf(3, 30);
        var pair = index.OwnerOf(4, 30);
        Assert.Contains("Pick`1", single);
        Assert.Contains("Pick`2", pair);
    }

    [Fact]
    public void CommentsAndBlankLines_DoNotChangeIdentity()
    {
        var before = Index("""
            public static class Foo
            {
                public static int Run(int x)
                {
                    return x;
                }
            }
            """);
        var after = Index("""
            // leading comment
            // another comment
            public static class Foo
            {
                // comment inside the class
                public static int Run(int x)
                {
                    return x;
                }
            }
            """);
        // The declaration's identity (its name shape) is unchanged even though the
        // method moved down three lines.
        var identityBefore = before.OwnerOf(3, 30);
        var identityAfter = after.OwnerOf(6, 30);
        Assert.Equal(identityBefore, identityAfter);
    }

    [Fact]
    public void TokenDigest_ChangesWhenDeclarationBodyChanges()
    {
        var before = Index("""
            public static class Foo
            {
                public static int Run(int x)
                {
                    return x;
                }
            }
            """);
        var after = Index("""
            public static class Foo
            {
                public static int Run(int x)
                {
                    return x + 1;
                }
            }
            """);
        var identity = before.OwnerOf(3, 30)!;
        var suffix = identity[(identity.IndexOf('|', identity.IndexOf('|') + 1) + 1)..];
        var digestBefore = before.TokenDigestOf(suffix);
        var digestAfter = after.TokenDigestOf(suffix);
        Assert.NotEqual(digestBefore, digestAfter);
    }

    [Fact]
    public void TokenDigest_DoesNotChangeForSiblingEdit()
    {
        var before = Index("""
            public static class Foo
            {
                public static int A(int x) { return x; }
                public static int B(int x) { return x; }
            }
            """);
        var after = Index("""
            public static class Foo
            {
                public static int A(int x) { return x; }
                public static int B(int x) { return x + 1; }
            }
            """);
        var identity = before.OwnerOf(3, 30)!;
        var suffix = identity[(identity.IndexOf('|', identity.IndexOf('|') + 1) + 1)..];
        Assert.Equal(before.TokenDigestOf(suffix), after.TokenDigestOf(suffix));
    }
}
