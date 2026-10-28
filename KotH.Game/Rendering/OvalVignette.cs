using Stride.Core;
using Stride.Core.Annotations;
using Stride.Core.Mathematics;
using Stride.Graphics;
using Stride.Rendering;
using Stride.Rendering.Images;

namespace KotH.Client.Rendering
{
    /// <summary>
    /// Stride's vignette, but starting later at top and bottom than at the sides
    /// (Luca); in the compositor's colour transforms. Shader: OvalVignette.sdsl.
    /// </summary>
    /// <userdoc>Vignette with its own start at the sides and at top/bottom.</userdoc>
    [DataContract("OvalVignette")]
    [Display("Vignette (oval)")]
    public sealed class OvalVignette : ColorTransform
    {
        static readonly ValueParameterKey<float> AmountKey = ParameterKeys.NewValue(0f, "OvalVignette.Amount");
        static readonly ValueParameterKey<float> SidesKey = ParameterKeys.NewValue(0f, "OvalVignette.RadiusSides");
        static readonly ValueParameterKey<float> TopBottomKey = ParameterKeys.NewValue(0f, "OvalVignette.RadiusTopBottom");
        static readonly ValueParameterKey<Color3> ColorKey = ParameterKeys.NewValue(default(Color3), "OvalVignette.Color");

        public OvalVignette() : base("OvalVignette")
        {
        }

        /// <userdoc>Strength: 1 = fully the colour in the corners.</userdoc>
        [DataMember(10)]
        [DataMemberRange(0.0, 1.0, 0.01, 0.1, 3)]
        public float Amount { get; set; } = 1f;

        /// <userdoc>Where it starts at the sides (0 = centre, 1 = corner).</userdoc>
        [DataMember(20)]
        [DataMemberRange(0.0, 1.0, 0.01, 0.1, 3)]
        public float RadiusSides { get; set; } = 0.25f;

        /// <userdoc>Where it starts at top and bottom (0 = centre, 1 = corner).</userdoc>
        [DataMember(30)]
        [DataMemberRange(0.0, 1.0, 0.01, 0.1, 3)]
        public float RadiusTopBottom { get; set; } = 0.5f;

        /// <userdoc>Colour towards the edge.</userdoc>
        [DataMember(40)]
        public Color3 Color { get; set; } = new Color3(0f);

        public override void UpdateParameters(ColorTransformContext context)
        {
            Parameters.Set(AmountKey, Amount);
            Parameters.Set(SidesKey, RadiusSides);
            Parameters.Set(TopBottomKey, RadiusTopBottom);
            Parameters.Set(ColorKey, Color.ToColorSpace(context.RenderContext.GraphicsDevice.ColorSpace));
            base.UpdateParameters(context);
        }
    }
}
