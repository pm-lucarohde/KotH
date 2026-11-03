using System;
using KotH.Shared.Net;
using Stride.Animations;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Particles.Components;
using Stride.Particles.Updaters;
using Stride.Rendering;
using Stride.Rendering.Materials;

namespace KotH.Client.Player
{
    /// <summary>A player's colours on a figure: its own copies of the model's materials with
    /// FigureTint's shifts set (same shaders, only other parameters), and its spores' colour
    /// scale. A default look leaves everything shared and untouched.</summary>
    public static class FigureColors
    {
        // Single fields: Stride's assembly processor breaks on an array of keys.
        static readonly ValueParameterKey<Vector4> CapShift = ParameterKeys.NewValue<Vector4>(default, "FigureTint.CapShift");
        static readonly ValueParameterKey<Vector4> SkinShift = ParameterKeys.NewValue<Vector4>(default, "FigureTint.SkinShift");
        static readonly ValueParameterKey<Vector4> LeafShift = ParameterKeys.NewValue<Vector4>(default, "FigureTint.LeafShift");
        static readonly ValueParameterKey<Vector4> GloveShift = ParameterKeys.NewValue<Vector4>(default, "FigureTint.GloveShift");

        /// <summary>Recolours model's figure materials; once copied, a model keeps its copies.</summary>
        public static void Apply(ModelComponent model, FigureLook look)
        {
            if (model?.Model == null)
                return;
            bool plain = look == FigureLook.Default;
            for (int i = 0; i < model.Model.Materials.Count; i++)
            {
                if (!model.Materials.TryGetValue(i, out Material own) || own == null)
                {
                    if (plain || model.Model.Materials[i].Material is not { } shared)
                        continue;
                    own = Copy(shared);
                    model.Materials[i] = own;
                }
                foreach (MaterialPass pass in own.Passes)
                {
                    pass.Parameters.Set(CapShift, Shift(look, FigureLook.Cap));
                    pass.Parameters.Set(SkinShift, Shift(look, FigureLook.Skin));
                    pass.Parameters.Set(LeafShift, Shift(look, FigureLook.Leaves));
                    pass.Parameters.Set(GloveShift, Shift(look, FigureLook.Gloves));
                }
            }
        }

        // colour / lum(reference) in linear light, the shader times the texel's lum; w how much of
        // that replaces the texture: none at the painted colour, all a quarter of the cube away.
        static Vector4 Shift(FigureLook look, int part)
        {
            Vector3 c = Linear(look[part]), r = Linear(FigureLook.PaintedColor(part));
            float away = (Unit(look[part]) - Unit(FigureLook.PaintedColor(part))).Length();
            return new Vector4(c / MathF.Max(1e-3f, Luminance(r)), Math.Clamp(away * 4f, 0f, 1f));
        }

        /// <summary>The spores of a particle effect in look's spore colour, scaled to the brightness of
        /// its colour curve's first key; the painted yellow leaves them as they are.</summary>
        public static void TintSpores(Entity effect, FigureLook look)
        {
            if (effect?.Get<ParticleSystemComponent>() is not { } particles)
                return;
            Color3 start = StartColor(particles) ?? new Color3(1f);
            particles.Color = new Color4(SporeFactor(look, start), 1f);
        }

        /// <summary>A spore ball's glow (its own material copy) moved the same way as the trails.</summary>
        public static void TintGlow(Entity ball, FigureLook look)
        {
            if (ball?.Get<ModelComponent>() is not { Model: { } model } component || model.Materials.Count == 0)
                return;
            if (!component.Materials.TryGetValue(0, out Material own) || own == null)
            {
                if (look[FigureLook.Spores] == FigureLook.PaintedColor(FigureLook.Spores) || model.Materials[0].Material is not { } shared)
                    return;
                own = Copy(shared);
                component.Materials[0] = own;
            }
            foreach (MaterialPass pass in own.Passes)
            {
                Color4 painted = model.Materials[0].Material.Passes[0].Parameters.Get(MaterialKeys.EmissiveValue);
                // The material holds linear light; the factor works on the painted (sRGB) colour.
                Vector3 f = SporeFactor(look, SporeGlowPainted);
                pass.Parameters.Set(MaterialKeys.EmissiveValue, new Color4(painted.R * MathF.Pow(f.X, 2.2f),
                    painted.G * MathF.Pow(f.Y, 2.2f), painted.B * MathF.Pow(f.Z, 2.2f), painted.A));
            }
        }

        // Spore.sdmat's emissive colour as entered.
        static readonly Color3 SporeGlowPainted = new Color3(0.55f, 1f, 0.25f);

        // Per channel moved / start, as FigureTint does it: the chosen colour at the start colour's
        // brightness, blended in the more the further the choice is from the painted yellow.
        static Vector3 SporeFactor(FigureLook look, Color3 start)
        {
            Vector3 chosen = Unit(look[FigureLook.Spores]), painted = Unit(FigureLook.PaintedColor(FigureLook.Spores));
            var s = new Vector3(start.R, start.G, start.B);
            float blend = Math.Clamp((chosen - painted).Length() * 4f, 0f, 1f);
            Vector3 moved = Vector3.Lerp(s, chosen * (Luminance(s) / MathF.Max(1e-3f, Luminance(painted))), blend);
            return new Vector3(Ratio(moved.X, s.X), Ratio(moved.Y, s.Y), Ratio(moved.Z, s.Z));
        }

        static float Ratio(float moved, float start) => start > 1e-3f ? MathF.Max(0f, moved) / start : 1f;

        static Color3? StartColor(ParticleSystemComponent particles)
        {
            foreach (var emitter in particles.ParticleSystem.Emitters)
                foreach (var updater in emitter.Updaters)
                    if (updater is UpdaterColorOverTime { SamplerMain: ComputeCurveSamplerColor4 { Curve: ComputeAnimationCurveColor4 curve } }
                        && curve.KeyFrames.Count > 0)
                        return curve.KeyFrames[0].Value.ToColor3();
            return null;
        }

        /// <summary>A copy sharing the shaders and textures, with parameters of its own.</summary>
        static Material Copy(Material shared)
        {
            var copy = new Material { Descriptor = shared.Descriptor };
            foreach (MaterialPass pass in shared.Passes)
                copy.Passes.Add(new MaterialPass(new ParameterCollection(pass.Parameters))
                {
                    CullMode = pass.CullMode, DepthFunction = pass.DepthFunction, BlendState = pass.BlendState,
                    TessellationMethod = pass.TessellationMethod, HasTransparency = pass.HasTransparency,
                    AlphaToCoverage = pass.AlphaToCoverage, IsLightDependent = pass.IsLightDependent, PassIndex = pass.PassIndex,
                });
            return copy;
        }

        static Vector3 Unit(uint rgb) => new Vector3((rgb >> 16 & 0xFF) / 255f, (rgb >> 8 & 0xFF) / 255f, (rgb & 0xFF) / 255f);

        static Vector3 Linear(uint rgb)
        {
            Vector3 u = Unit(rgb);
            return new Vector3(MathUtil.SRgbToLinear(u.X), MathUtil.SRgbToLinear(u.Y), MathUtil.SRgbToLinear(u.Z));
        }

        static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
    }
}
