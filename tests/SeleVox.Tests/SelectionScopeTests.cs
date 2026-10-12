using SeleVox.App;
using SeleVox.Core;

namespace SeleVox.Tests;

public sealed class SelectionScopeTests
{
    [Fact]
    public void DeepBrowserChainReachesForegroundIdentityWithoutNativeHandles()
    {
        var root = new Node { Foreground = true };
        var focused = root;
        for (var i = 0; i < 30; i++) focused = new Node { Parent = focused };
        var scope = Resolve(focused);
        Assert.Equal(31, scope.Count);
        Assert.Same(focused, scope[0]); Assert.Same(root, scope[^1]);
    }

    [Fact]
    public void ForegroundBoundaryExcludesDesktopAndOtherWindows()
    {
        var desktop = new Node();
        var root = new Node { Parent = desktop, Foreground = true };
        var scope = Resolve(new Node { Parent = root });
        Assert.Equal(2, scope.Count); Assert.DoesNotContain(desktop, scope);
    }

    [Fact]
    public void UnrelatedWindowCannotProduceACaptureScope()
    {
        var unrelated = new Node { Parent = new Node() };
        Assert.Throws<UserFeedbackException>(() => Resolve(unrelated));
    }

    [Fact]
    public void PasswordBeyondOldDepthLimitRejectsEntireScopeBeforeReading()
    {
        var focused = new Node { Password = true, Parent = new Node { Foreground = true } };
        for (var i = 0; i < 20; i++) focused = new Node { Parent = focused };
        var error = Assert.Throws<UserFeedbackException>(() => Resolve(focused));
        Assert.Equal("密码控件不会被读取。", error.Message);
    }

    [Fact]
    public void CyclicProviderCannotExceedTheAncestorBudget()
    {
        var focused = new Node(); focused.Parent = focused;
        var visited = 0;
        Assert.Throws<UserFeedbackException>(() => SelectionScope.Resolve(focused, n => n.Parent,
            _ => { visited++; return false; }, n => n.Foreground, CancellationToken.None));
        Assert.Equal(SelectionScope.MaximumAncestors, visited);
    }

    [Fact]
    public void CancelledRequestDoesNotVisitProvider()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SelectionScope.Resolve(new Node(),
            _ => throw new InvalidOperationException(), _ => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException(), cancellation.Token));
    }

    private static IReadOnlyList<Node> Resolve(Node focused) => SelectionScope.Resolve(focused,
        n => n.Parent, n => n.Password, n => n.Foreground, CancellationToken.None);

    private sealed class Node
    {
        public Node? Parent;
        public bool Password, Foreground;
    }
}
