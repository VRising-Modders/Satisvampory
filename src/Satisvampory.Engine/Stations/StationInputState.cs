// SPDX-License-Identifier: AGPL-3.0-only
namespace Satisvampory.Engine.Stations
{
    /// <summary>Read-only queue/activity facts. Unknown or inconsistent reads retain every input.</summary>
    public readonly record struct StationInputState(bool Known, int QueueLength, bool Active)
    {
        public static StationInputState Unknown => default;
        public static StationInputState Idle => new(true, 0, false);
        public bool CanReclaim => Known && QueueLength == 0 && !Active;
    }
}
