using System;

namespace CairnMultiplayerMod.Internal.Game.Roping;

internal static class RopeAttachmentPolicy
{
    private const float FallSlackMeters = .35f;

    internal static bool CanReach(float maxLength, float distance)
        => float.IsFinite(maxLength) && float.IsFinite(distance)
           && maxLength > 0 && distance >= 0 && distance < maxLength;

    internal static bool TryGetInitialLength(float maxLength, float distance, out float length)
    {
        length = 0;
        if (!CanReach(maxLength, distance)) return false;
        length = maxLength;
        return true;
    }

    internal static bool TryGetFallLength(float maxLength, float distance, out float length)
    {
        length = 0;
        if (!CanReach(maxLength, distance)) return false;
        length = Math.Min(maxLength, distance + FallSlackMeters);
        return float.IsFinite(length) && length >= distance && length <= maxLength;
    }

    /// <summary>
    /// The paid-out length has reached the request. In game this reading has stayed
    /// at 0, so the visible line is drawn from the harnesses and does not wait on it.
    /// </summary>
    internal static bool SnapLanded(float requested, float reported)
    {
        if (!float.IsFinite(requested) || !float.IsFinite(reported) || requested <= 0f) return false;
        var tolerance = Math.Max(0.5f, requested * 0.15f);
        return Math.Abs(reported - requested) <= tolerance;
    }
}
