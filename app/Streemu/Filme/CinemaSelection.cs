using System.Text.Json.Nodes;

namespace Streemu.Filme;

/// <summary>A identidade inclui o anfitrião: dois amigos podem oferecer exatamente o mesmo arquivo.</summary>
public sealed class CinemaSelection
{
    public JsonObject? Selected { get; private set; }
    public bool Detail { get; private set; }
    public bool Missing { get; private set; }
    public bool Creating => Detail && Selected == null;
    JsonObject? _source;

    public static bool Same(JsonObject a, JsonObject b) =>
        (string?)a["de"] == (string?)b["de"] && (string?)a["filme"] == (string?)b["filme"];

    public void Select(JsonObject announcement) { _source = announcement; Selected = (JsonObject)announcement.DeepClone(); Detail = true; Missing = false; }
    public void Create() { _source = Selected = null; Detail = true; Missing = false; }
    public void Back() { _source = Selected = null; Detail = false; Missing = false; }

    public void Refresh(IReadOnlyList<JsonObject> announcements, bool pinned)
    {
        if (Selected == null || pinned) return;
        var current = announcements.FirstOrDefault(a => Same(a, Selected));
        Missing = current == null;
        // Nunca trocar por outro anfitrião, nem pela reabertura de outro arquivo do mesmo anfitrião.
        if (current != null && !ReferenceEquals(current, _source)) { _source = current; Selected = (JsonObject)current.DeepClone(); }
    }
}
