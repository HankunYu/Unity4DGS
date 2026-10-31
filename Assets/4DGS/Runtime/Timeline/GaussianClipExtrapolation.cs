namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// What a clip does in the gap before or after it, up to the neighbouring
    /// clip. The clip keeps the weight it has at that edge, so the side it
    /// extrapolates into wants no ease.
    /// </summary>
    public enum GaussianClipExtrapolation
    {
        None,
        Hold,
        Loop,
        PingPong
    }
}
