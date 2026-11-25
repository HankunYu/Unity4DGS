namespace GaussianSplatting.Runtime
{
    // What a style's flow runs through (GaussianStyleFlowBlock): where its
    // columns lie and what its lengths are measured in.
    // Values match STYLE_FLOW_* in GaussianStyleFlow.hlsl.
    public enum GaussianSplatStyleFlowSpace
    {
        // Straight columns of world space along the direction, whatever
        // passes through them
        World = 0,
        // Strips lying in the surface the splats sit on: across it they
        // follow its own tangent, so a wall at any angle carries even
        // strips, and a face lying across the direction carries strips
        // running over it instead of cells. Still placed in world space
        Surface = 1,
        // Strips in the UV space of the mesh the splats were sampled from
        // (a .meshsplat with Store UVs), so they follow its unwrap and stay
        // on it as it moves. Lengths and speed are in UV units. World stands
        // in for splats without UVs
        Uv = 2,
    }
}
