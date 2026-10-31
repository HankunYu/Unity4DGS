namespace GaussianSplatting.Editor.MeshSplat
{
    public enum MeshSplatOutput
    {
        // One splat asset in the rest pose, with a binding for GaussianMeshDriver
        Asset,
        // One splat asset per animation frame, as a GaussianSplatSequence
        Sequence,
    }
}
