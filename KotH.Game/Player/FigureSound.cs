namespace KotH.Client.Player
{
    /// <summary>What a figure just did that can be heard (PlayerVisualSystem.Sounded).</summary>
    public enum FigureSound
    {
        /// <summary>A foot touched down; the amount is how hard, 0 to 1.</summary>
        Step,
        /// <summary>Off the ground.</summary>
        Jump,
        /// <summary>The double jump's kick in the air.</summary>
        DoubleJump,
        /// <summary>Back on the ground; the amount is the drop in metres from the highest point.</summary>
        Land,
        /// <summary>A swing of the cane started.</summary>
        Swing,
        /// <summary>The figure was hit.</summary>
        Hit,
    }
}
