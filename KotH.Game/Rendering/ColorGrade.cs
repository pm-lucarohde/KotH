using Stride.Core;
using Stride.Core.Annotations;
using Stride.Rendering;
using Stride.Rendering.Images;

namespace KotH.Client.Rendering
{
    /// <summary>Contrast and brightness of the finished image (Luca: a little softer and
    /// lighter than the plain render), in the compositor's colour transforms before the
    /// vignette. Shader: ColorGrade.sdsl.</summary>
    /// <userdoc>Contrast and brightness of the whole image.</userdoc>
    [DataContract("ColorGrade")]
    [Display("Colour grade")]
    public sealed class ColorGrade : ColorTransform
    {
        static readonly ValueParameterKey<float> ContrastKey = ParameterKeys.NewValue(1f, "ColorGrade.Contrast");
        static readonly ValueParameterKey<float> BrightnessKey = ParameterKeys.NewValue(1f, "ColorGrade.Brightness");

        public ColorGrade() : base("ColorGrade")
        {
        }

        /// <userdoc>Contrast: 1 = unchanged, lower brings light and dark together; the mean brightness stays.</userdoc>
        [DataMember(10)]
        [DataMemberRange(0.0, 2.0, 0.01, 0.1, 3)]
        public float Contrast { get; set; } = 1f;

        /// <userdoc>Brightness: 1 = unchanged, 1.1 = everything 10 % lighter.</userdoc>
        [DataMember(20)]
        [DataMemberRange(0.0, 3.0, 0.01, 0.1, 3)]
        public float Brightness { get; set; } = 1f;

        public override void UpdateParameters(ColorTransformContext context)
        {
            Parameters.Set(ContrastKey, Contrast);
            Parameters.Set(BrightnessKey, Brightness);
            base.UpdateParameters(context);
        }
    }
}
