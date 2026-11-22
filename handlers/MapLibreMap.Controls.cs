namespace MapLibreNative.Maui.Handlers;

/// <summary>A control added to the map with <see cref="MapLibreMap.AddControl"/>: a view, and the corner it stacks in.</summary>
public sealed record MapControl(View View, MapControlCorner Corner);

/// <summary>
/// The room the map's controls take in each of its corners, margins included, in device-independent units: for views
/// of the app's own laid over the map (a panel, a banner) to keep clear of them. A corner with no controls is
/// <see cref="Size.Zero"/>.
/// </summary>
public sealed record MapControlInsets(Size TopLeft, Size TopRight, Size BottomLeft, Size BottomRight)
{
    public static readonly MapControlInsets None = new(Size.Zero, Size.Zero, Size.Zero, Size.Zero);

    public Size this[MapControlCorner corner] => corner switch
    {
        MapControlCorner.TopLeft => TopLeft,
        MapControlCorner.TopRight => TopRight,
        MapControlCorner.BottomLeft => BottomLeft,
        _ => BottomRight,
    };
}

public partial class MapLibreMap
{
    private readonly List<MapControl> _controls = [];

    /// <summary>The controls added with <see cref="AddControl"/>, in the order they stack.</summary>
    public IReadOnlyList<MapControl> Controls => _controls;

    /// <summary>
    /// Adds a control of the app's own to the map, stacked in <paramref name="corner"/> with the built-in controls
    /// (navigation, GPS, terrain, attribution), as maplibre-gl-js's <c>addControl</c> does: the first control in a corner
    /// sits nearest it, and later ones stack away from it, down from a top corner and up from a bottom one. The built-in
    /// controls come first. Any view will do (a Button, a small layout); it takes the map's BindingContext. Adding a
    /// control again moves it to the end of <paramref name="corner"/>'s stack.
    /// </summary>
    public void AddControl(View control, MapControlCorner corner = MapControlCorner.TopRight)
    {
        ArgumentNullException.ThrowIfNull(control);
        _controls.RemoveAll(c => ReferenceEquals(c.View, control));
        _controls.Add(new MapControl(control, corner));
        control.Parent = this;
        SetInheritedBindingContext(control, BindingContext);
        Handler?.UpdateValue(nameof(Controls));
    }

    /// <summary>Takes a control added with <see cref="AddControl"/> off the map. False when it wasn't on it.</summary>
    public bool RemoveControl(View control)
    {
        if (_controls.RemoveAll(c => ReferenceEquals(c.View, control)) == 0) return false;
        Handler?.UpdateValue(nameof(Controls));
        control.Handler?.DisconnectHandler();
        control.Parent = null;
        return true;
    }

    /// <summary>The room the controls take in each corner; <see cref="ControlInsetsChanged"/> tells when it changes.</summary>
    public MapControlInsets ControlInsets { get; private set; } = MapControlInsets.None;

    /// <summary>The room the controls take in a corner changed: one was added, removed, shown, hidden, or resized.</summary>
    public event EventHandler? ControlInsetsChanged;

    internal void OnControlInsetsChanged(MapControlInsets insets)
    {
        if (insets == ControlInsets) return;
        ControlInsets = insets;
        OnPropertyChanged(nameof(ControlInsets));
        ControlInsetsChanged?.Invoke(this, System.EventArgs.Empty);
    }

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        // The controls aren't the layout's children, so they don't inherit it on their own
        foreach (var c in _controls) SetInheritedBindingContext(c.View, BindingContext);
    }
}
