namespace CodexAccountMonitor.Infrastructure;

internal readonly record struct ModelDragPosition(int X, int Y);
internal enum ModelDragOutcome { None, Click, Drag }

/// <summary>Tracks a held pointer in physical screen pixels, independent of window focus and DPI changes.</summary>
internal sealed class ModelDragGesture
{
    private int _pointerX, _pointerY, _windowX, _windowY, _thresholdX, _thresholdY;
    private bool _hasMoved;
    public bool IsActive { get; private set; }

    public void Begin(int pointerX, int pointerY, int windowX, int windowY, int thresholdX, int thresholdY)
    {
        _pointerX = pointerX; _pointerY = pointerY;
        _windowX = windowX; _windowY = windowY;
        _thresholdX = System.Math.Max(1, thresholdX); _thresholdY = System.Math.Max(1, thresholdY);
        _hasMoved = false; IsActive = true;
    }

    public ModelDragPosition? Move(int pointerX, int pointerY)
    {
        if (!IsActive) return null;
        var dx = (long)pointerX - _pointerX;
        var dy = (long)pointerY - _pointerY;
        _hasMoved |= System.Math.Abs(dx) >= _thresholdX || System.Math.Abs(dy) >= _thresholdY;
        if (!_hasMoved) return null;
        // Keep the original grab offset even across monitors with different DPI.
        return new ModelDragPosition(Pixel(_windowX + dx), Pixel(_windowY + dy));
    }

    public ModelDragOutcome Complete(bool cancel = false)
    {
        if (!IsActive) return ModelDragOutcome.None;
        IsActive = false;
        return cancel ? ModelDragOutcome.None : _hasMoved ? ModelDragOutcome.Drag : ModelDragOutcome.Click;
    }

    private static int Pixel(long value) => (int)System.Math.Clamp(value, int.MinValue, int.MaxValue);
}
