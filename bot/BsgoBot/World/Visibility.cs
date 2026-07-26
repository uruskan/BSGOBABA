using BsgoBot.Protocol;

namespace BsgoBot.World;

/// <summary>
/// How far a contact is from being drawn by your own game client.
///
/// The server streams the whole sector; the client then throws most of it away locally,
/// in DradisHelper, using three radii off your ship's stats. That means every contact the
/// bot knows about sits in one of these bands — and the last one is intel your client is
/// actively hiding from you.
/// </summary>
public enum ContactLayer
{
    /// <summary>Inside DetectionVisualRadius. Drawn by the client even when cloaked.</summary>
    Visual = 0,

    /// <summary>Inside DetectionInnerRadius. On your DRADIS.</summary>
    Dradis = 1,

    /// <summary>Inside DetectionOuterRadius, or an always-visible type. On your map only.</summary>
    Map = 2,

    /// <summary>Beyond every detection radius. The server told us; your client draws nothing.</summary>
    Dark = 3,

    /// <summary>Your ship's detection radii haven't been published, so the band is unknowable.</summary>
    Unknown = 4,
}

/// <summary>Your ship's three detection radii, as the client reads them.</summary>
public readonly record struct DetectionRanges(float Visual, float Dradis, float Map)
{
    public bool Known => Dradis > 0f || Map > 0f;

    public static readonly DetectionRanges None = new(0f, 0f, 0f);
}

public static class Visibility
{
    /// <summary>
    /// Client: DradisHelper.IsAlwaysInMapRange. These types ignore detection range entirely,
    /// which is why a distant asteroid still shows up on your in-game map.
    /// </summary>
    public static bool AlwaysVisible(SpaceEntityType type) =>
        type is SpaceEntityType.Asteroid or SpaceEntityType.AsteroidBot or SpaceEntityType.Planet
            or SpaceEntityType.Planetoid or SpaceEntityType.SectorEvent or SpaceEntityType.Comet;

    /// <summary>
    /// Which band a contact falls into, following DradisHelper's own comparisons: inside the
    /// visual radius it counts regardless of cloak, past the outer radius it never counts,
    /// and in between a cloaked object drops out.
    /// </summary>
    public static ContactLayer Classify(SpaceObj o, float distance, DetectionRanges r)
    {
        if (!r.Known) return ContactLayer.Unknown;

        if (r.Visual > 0f && distance < r.Visual) return ContactLayer.Visual;

        if (!o.Cloaked)
        {
            if (r.Dradis > 0f && distance <= r.Dradis) return ContactLayer.Dradis;
            if (r.Map > 0f && distance <= r.Map) return ContactLayer.Map;
        }

        return AlwaysVisible(o.Type) ? ContactLayer.Map : ContactLayer.Dark;
    }

    public static string Describe(ContactLayer layer) => layer switch
    {
        ContactLayer.Visual => "VISUAL",
        ContactLayer.Dradis => "DRADIS",
        ContactLayer.Map => "MAP",
        ContactLayer.Dark => "DARK",
        _ => "UNKNOWN",
    };
}
