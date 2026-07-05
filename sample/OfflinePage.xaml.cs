using MapLibreNative.Maui;
using MapLibreNative.Maui.Handlers;

namespace MauiSample;

public partial class OfflinePage : ContentPage
{
    private readonly OfflineViewModel _vm = new();

    // Shares MbglCache.DefaultPath with the map views, so downloaded regions
    // are served to the map automatically.
    private readonly MbglOfflineManager _offline = new();

    public OfflinePage()
    {
        InitializeComponent();
        BindingContext = _vm;

        // Progress/error events arrive on MapLibre's database thread.
        _offline.RegionProgress += p => MainThread.BeginInvokeOnMainThread(() =>
        {
            _vm.Status = p.Complete
                ? $"Region {p.RegionId}: download complete — {p.CompletedResources} resources, {p.CompletedBytes / 1024} KB"
                : $"Region {p.RegionId}: {p.CompletedResources}/{(p.RequiredIsPrecise ? p.RequiredResources.ToString() : "~" + p.RequiredResources)} resources, {p.CompletedBytes / 1024} KB";
        });
        _offline.RegionError += e => MainThread.BeginInvokeOnMainThread(() =>
            _vm.Status = $"Region {e.RegionId} error ({e.Reason}): {e.Message}");
    }

    private IMapLibreMapController? Controller => (Map.Handler as MapLibreMapHandler)?.Controller;

    private async void OnDownloadRegion(object? sender, EventArgs e)
    {
        try
        {
            var ctrl = Controller;
            if (ctrl is null) { _vm.Status = "Map not ready."; return; }

            var (latSw, lonSw, latNe, lonNe) = ctrl.GetVisibleBounds();
            if (double.IsNaN(latSw)) { _vm.Status = "Map not ready."; return; }

            double zoom = ctrl.GetZoom();
            double minZoom = Math.Max(0, Math.Floor(zoom));
            double maxZoom = Math.Min(minZoom + 2, 14);

            _vm.Status = $"Creating region (z{minZoom:0}–{maxZoom:0})…";
            var region = await _offline.CreateRegionAsync(
                Map.StyleUrl!, latSw, lonSw, latNe, lonNe, minZoom, maxZoom,
                includeIdeographs: false,
                metadata: System.Text.Encoding.UTF8.GetBytes(
                    $"{{\"name\":\"Region {DateTime.Now:HH:mm:ss}\"}}"));

            _offline.ObserveRegion(region.Id);
            _offline.SetDownloadState(region.Id, active: true);
            _vm.Status = $"Region {region.Id}: downloading…";
        }
        catch (Exception ex)
        {
            _vm.Status = $"Download failed: {ex.Message}";
        }
    }

    private async void OnListRegions(object? sender, EventArgs e)
    {
        try
        {
            var regions = await _offline.ListRegionsAsync();
            if (regions.Length == 0) { _vm.Status = "No offline regions."; return; }

            var parts = new List<string>();
            foreach (var r in regions)
            {
                var status = await _offline.GetRegionStatusAsync(r.Id);
                var meta = _offline.GetRegionMetadata(r.Id);
                var name = meta != null ? System.Text.Encoding.UTF8.GetString(meta) : "";
                parts.Add($"#{r.Id} z{r.MinZoom:0}-{r.MaxZoom:0} " +
                          $"{status.CompletedResourceSize / 1024} KB {name}");
            }
            _vm.Status = $"{regions.Length} region(s): {string.Join(" | ", parts)}";
        }
        catch (Exception ex)
        {
            _vm.Status = $"List failed: {ex.Message}";
        }
    }

    private async void OnDeleteAll(object? sender, EventArgs e)
    {
        try
        {
            var regions = await _offline.ListRegionsAsync();
            foreach (var r in regions)
                await _offline.DeleteRegionAsync(r.Id);
            _vm.Status = $"Deleted {regions.Length} region(s).";
        }
        catch (Exception ex)
        {
            _vm.Status = $"Delete failed: {ex.Message}";
        }
    }

    private void OnToggleOffline(object? sender, EventArgs e)
    {
        MbglNetwork.Online = !MbglNetwork.Online;
        OfflineToggle.Text = MbglNetwork.Online ? "Go offline" : "Go online";
        _vm.Status = MbglNetwork.Online
            ? "Network ONLINE — tiles load normally."
            : "Network OFFLINE — only cached/offline tiles are served.";
    }
}
