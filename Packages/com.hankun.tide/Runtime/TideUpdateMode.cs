namespace Tide
{
    /// <summary>What moves a timeline player's time on.</summary>
    public enum TideUpdateMode
    {
        // Time.deltaTime: follows timeScale and captureFramerate (frame-exact recordings)
        GameTime,
        // Time.unscaledDeltaTime
        UnscaledGameTime,
        // An AudioSource's playback position, so the picture stays on the music
        Audio,
        // Only Seek moves it
        Manual
    }
}
