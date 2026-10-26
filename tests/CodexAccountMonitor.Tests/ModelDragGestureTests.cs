using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class ModelDragGestureTests
{
    [Fact]
    public void SmallPointerJitterRemainsOneClickWithoutMovingTheWindow()
    {
        var gesture = Begin();
        Assert.Null(gesture.Move(102, 203));
        Assert.Null(gesture.Move(99, 198));
        Assert.Equal(ModelDragOutcome.Click, gesture.Complete());
        Assert.Equal(ModelDragOutcome.None, gesture.Complete());
        Assert.Null(gesture.Move(300, 400));
    }

    [Theory]
    [InlineData(104, 200, 54, 120)]
    [InlineData(100, 204, 50, 124)]
    [InlineData(96, 200, 46, 120)]
    [InlineData(100, 196, 50, 116)]
    public void EitherAxisCrossingThresholdStartsDraggingWithOriginalGrabOffset(int x, int y, int left, int top)
    {
        var gesture = Begin();
        Assert.Equal(new ModelDragPosition(left, top), gesture.Move(x, y));
        Assert.Equal(ModelDragOutcome.Drag, gesture.Complete());
    }

    [Fact]
    public void FastMovementBeyondWindowAcrossNegativeScreenCoordinatesKeepsPointerOffset()
    {
        var gesture = Begin();
        Assert.Equal(new ModelDragPosition(-1570, -560), gesture.Move(-1520, -480));
        Assert.Equal(new ModelDragPosition(-2590, 1760), gesture.Move(-2540, 1840));
        Assert.Equal(ModelDragOutcome.Drag, gesture.Complete());
    }

    [Fact]
    public void DragAwayAndBackDoesNotBecomeAShowLimitsClick()
    {
        var gesture = Begin();
        Assert.NotNull(gesture.Move(450, 600));
        Assert.Equal(new ModelDragPosition(50, 120), gesture.Move(100, 200));
        Assert.Equal(ModelDragOutcome.Drag, gesture.Complete());
    }

    [Fact]
    public void ScaledThresholdAffectsOnlyDragStartAndNeverScalesScreenPixelMovement()
    {
        var gesture = new ModelDragGesture();
        gesture.Begin(100, 200, 50, 120, 8, 8);
        Assert.Null(gesture.Move(107, 207));
        Assert.Equal(new ModelDragPosition(58, 120), gesture.Move(108, 200));
        Assert.Equal(new ModelDragPosition(250, 20), gesture.Move(300, 100));
        Assert.Equal(ModelDragOutcome.Drag, gesture.Complete());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationNeitherClicksNorPersistsAndNextPressStartsFresh(bool moved)
    {
        var gesture = Begin();
        if (moved) gesture.Move(300, 400);
        Assert.Equal(ModelDragOutcome.None, gesture.Complete(cancel: true));
        Assert.False(gesture.IsActive);
        Assert.Null(gesture.Move(400, 500));
        gesture.Begin(-900, 15, -930, -25, 4, 4);
        Assert.Null(gesture.Move(-898, 17));
        Assert.Equal(ModelDragOutcome.Click, gesture.Complete());
    }

    [Fact]
    public void MissingPressCannotClickOrMove()
    {
        var gesture = new ModelDragGesture();
        Assert.Null(gesture.Move(100, 200));
        Assert.Equal(ModelDragOutcome.None, gesture.Complete());
    }

    [Fact]
    public void ExtremeCoordinateInputCannotWrapToTheOppositeSideOfTheScreen()
    {
        var gesture = new ModelDragGesture();
        gesture.Begin(int.MinValue, int.MaxValue, 10, -10, 4, 4);
        Assert.Equal(new ModelDragPosition(int.MaxValue, int.MinValue), gesture.Move(int.MaxValue, int.MinValue));
    }

    private static ModelDragGesture Begin()
    {
        var gesture = new ModelDragGesture();
        gesture.Begin(100, 200, 50, 120, 4, 4);
        return gesture;
    }
}
