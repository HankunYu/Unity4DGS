namespace GaussianSplatting.Editor.Import
{
    // How GaussianSplatImportSettings limits the splat count
    public enum GaussianSplatBudgetMode
    {
        None,
        Count,      // keep at most budgetCount splats
        Percentage, // keep budgetPercent of the source's splats
    }
}
