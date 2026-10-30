using SLCopilot.Core;
using Xunit;

namespace SLCopilot.Core.Tests;

public sealed class CompatTests
{
    private static readonly char Sep = Path.DirectorySeparatorChar;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "slc-root");

    [Fact]
    public void RelativeToAcceptsOnlyPathsInsideRoot()
    {
        Assert.Equal("a" + Sep + "b.cs", Compat.RelativeTo(Root, Path.Combine(Root, "a", "b.cs")));
        Assert.Equal(".", Compat.RelativeTo(Root, Root));
        Assert.Equal("x.cs", Compat.RelativeTo(Root + Sep, Path.Combine(Root, "x.cs")));
        // 前缀相同但不在根目录内（slc-root2）必须拒绝。
        Assert.Throws<WorkspaceException>(() => Compat.RelativeTo(Root, Root + "2" + Sep + "x.cs"));
        Assert.Throws<WorkspaceException>(() => Compat.RelativeTo(Root, Path.GetTempPath()));
    }

    [Fact]
    public void TrimEndingSeparatorKeepsRoot()
    {
        var systemRoot = Path.GetPathRoot(Root)!;
        Assert.Equal(systemRoot, Compat.TrimEndingDirectorySeparator(systemRoot));
        Assert.Equal(Root, Compat.TrimEndingDirectorySeparator(Root + Sep + Sep));
    }

    [Fact]
    public void FullyQualifiedPathsAreRecognized()
    {
        Assert.True(Compat.IsPathFullyQualified(Root));
        Assert.False(Compat.IsPathFullyQualified("relative" + Sep + "x"));
        Assert.False(Compat.IsPathFullyQualified(""));
        if (Compat.IsWindows)
        {
            Assert.False(Compat.IsPathFullyQualified("C:relative"));
            Assert.False(Compat.IsPathFullyQualified("\\rooted-without-drive"));
            Assert.True(Compat.IsPathFullyQualified("\\\\server\\share"));
        }
    }

    [Fact]
    public void Sha256HexMatchesKnownVector()
        => Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", Compat.Sha256Hex("abc"u8.ToArray()));

    [Fact]
    public void MoveOverwriteReplacesExistingFile()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "slc-move-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var source = Path.Combine(dir, "a.tmp");
            var target = Path.Combine(dir, "a.json");
            System.IO.File.WriteAllText(target, "old");
            System.IO.File.WriteAllText(source, "new");
            Compat.MoveOverwrite(source, target);
            Assert.Equal("new", System.IO.File.ReadAllText(target));
            Assert.False(System.IO.File.Exists(source));
        }
        finally { Directory.Delete(dir, true); }
    }
}
