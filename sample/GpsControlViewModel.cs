using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MauiSample;

/// <summary>
/// ViewModel for <see cref="GpsControlPage"/>.
///
/// Demonstrates the GPS overlay control by simulating a GPS receiver
/// walking through a series of waypoints around Seattle.  The simulation
/// calls <c>UpdateGpsLocation()</c> on the map controller, which the GPS
/// control overlay picks up according to the currently-selected tracking
/// mode (Off / Show / Follow).
/// </summary>
public partial class GpsControlViewModel : ObservableObject
{
    // ── Simulated GPS route ────────────────────────────────────────────────────

    // A short loop around Seattle highlights the GPS-follow behaviour.
    private static readonly (double Lat, double Lon, float Bearing, string Label)[] Waypoints =
    [
        (47.6062, -122.3321,   0f, "Pike Place Market"),
        (47.6089, -122.3380, 310f, "Seattle Centre approach"),
        (47.6116, -122.3493, 270f, "Space Needle"),
        (47.6145, -122.3450,  45f, "Queen Anne"),
        (47.6120, -122.3330,  90f, "South Lake Union"),
        (47.6090, -122.3290, 135f, "Capitol Hill approach"),
        (47.6062, -122.3321, 200f, "Pike Place Market (loop)"),
    ];

    private int           _waypointIndex;
    private IDispatcherTimer? _timer;

    // Delegate set by the page to push GPS fixes into the map controller.
    public Action<double, double, float, float>? SendGpsUpdate { get; set; }

    // ── Observable state ──────────────────────────────────────────────────────

    [ObservableProperty]
    private string _status = "GPS simulation stopped.  Press Start to begin.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartSimulationCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopSimulationCommand))]
    private bool _isSimulating;

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void StartSimulation()
    {
        _waypointIndex = 0;
        _timer = Application.Current!.Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1.5);
        _timer.Tick += OnTimerTick;
        _timer.Start();
        IsSimulating = true;
        Status = "Simulation running — GPS overlay cycles Off → Show → Follow…";
    }

    private bool CanStart() => !IsSimulating;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void StopSimulation()
    {
        _timer?.Stop();
        _timer = null;
        IsSimulating = false;
        Status = "GPS simulation stopped.";
    }

    private bool CanStop() => IsSimulating;

    // ── Timer tick ────────────────────────────────────────────────────────────

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var wp = Waypoints[_waypointIndex % Waypoints.Length];
        SendGpsUpdate?.Invoke(wp.Lat, wp.Lon, wp.Bearing, 8f);
        Status = $"GPS fix #{_waypointIndex + 1}: {wp.Label} ({wp.Lat:F4}, {wp.Lon:F4})  bearing={wp.Bearing:F0}°";
        _waypointIndex++;
    }
}
