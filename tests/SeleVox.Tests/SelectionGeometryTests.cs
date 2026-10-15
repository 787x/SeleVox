using System.Windows;
using SeleVox.App;

namespace SeleVox.Tests;

[Trait("Category", "Offline")]
public sealed class SelectionGeometryTests
{
    [Fact] public void DragCanEndInWhitespaceOnEitherSideOfSelectedText()
    {
        var selection = new Rect(100, 100, 120, 20);
        Assert.True(SelectionGeometry.Intersects(selection, new(110, 110), new(600, 110)));
        Assert.True(SelectionGeometry.Intersects(selection, new(600, 110), new(110, 110)));
        Assert.True(SelectionGeometry.Intersects(selection, new(110, 110), new(20, 110)));
        Assert.True(SelectionGeometry.Intersects(selection, new(110, 110), new(600, 200)));
    }
    [Fact] public void UnrelatedAndDiagonalDragsDoNotMatchExistingSelection()
    {
        var selection = new Rect(100, 100, 120, 20);
        Assert.False(SelectionGeometry.Intersects(selection, new(300, 110), new(600, 110)));
        Assert.False(SelectionGeometry.Intersects(selection, new(0, 0), new(400, 130)));
        Assert.False(SelectionGeometry.Intersects(selection, new(10, 10), new(10, 200)));
        Assert.False(SelectionGeometry.Intersects(Rect.Empty, new(110, 110), new(110, 110)));
    }
    [Fact] public void DoubleClickRequiresThePointToBeInTheActualSelection()
    {
        var selection = new Rect(100, 100, 120, 20);
        Assert.True(SelectionGeometry.Intersects(selection, new(110, 110), new(110, 110)));
        Assert.False(SelectionGeometry.Intersects(selection, new(400, 110), new(400, 110)));
    }
    [Fact] public void RecognizedGestureRetainsStartAndClearsItOnNextOperation()
    {
        var gesture = new SeleVox.Core.MouseSelectionGesture(500, 8, 8);
        gesture.Down(1, 110, 110, 100); Assert.True(gesture.Up(1, 600, 110, 200));
        Assert.Equal((110, 110), gesture.SelectionStart);
        gesture.Down(1, 300, 300, 300); Assert.Null(gesture.SelectionStart);
        Assert.False(gesture.Up(1, 300, 300, 400)); Assert.Null(gesture.SelectionStart);
        gesture.Down(1, 110, 110, 500); gesture.Reset();
        Assert.False(gesture.Up(1, 600, 110, 600)); Assert.Null(gesture.SelectionStart);
    }
}
