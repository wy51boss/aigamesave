using AiGameSave.Core;
using AiGameSave.Infrastructure;

namespace AiGameSave.Tests;

public sealed class PathTemplateResolverTests
{
    [Fact]
    public void Resolve_ReplacesUserVariables()
    {
        var resolver = new PathTemplateResolver();
        var result = resolver.Resolve("%LOCALAPPDATA%\\ExampleGame\\Saves");
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), result, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("ExampleGame", "Saves"), result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsAllowedPath_RejectsWindowsDirectoryAndDriveRoot()
    {
        var resolver = new PathTemplateResolver();
        Assert.False(resolver.IsAllowedPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.False(resolver.IsAllowedPath(Path.GetPathRoot(Environment.SystemDirectory)!));
    }

    [Fact]
    public void IsSubPath_RejectsSiblingPrefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "game");
        Assert.True(PathTemplateResolver.IsSubPath(Path.Combine(root, "saves", "slot.sav"), root));
        Assert.False(PathTemplateResolver.IsSubPath(root + "-other", root));
    }
}
