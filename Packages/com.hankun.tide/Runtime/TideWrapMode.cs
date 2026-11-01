namespace Tide
{
    /// <summary>What a timeline player does when playback reaches the end.</summary>
    public enum TideWrapMode
    {
        // Stay on the last frame
        Hold,
        // Start over
        Loop,
        // Stop and put every target back as it was
        None
    }
}
