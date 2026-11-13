using KotH.Shared.Mathematics;
using KotH.Shared.Physics;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Engine.Design;

namespace KotH.Shared.Match
{
    /// <summary>A ladder on a wall (The Ladder): the entity stands at its foot on the wall's face and
    /// looks out of the wall (forward, -Z). A body pushing towards the wall inside it climbs. No
    /// collider: the wall is the map's; the motors ask the strip with plain arithmetic.</summary>
    [DataContract("KotH.Ladder")]
    [Display("Ladder")]
    [ComponentCategory("KotH")]
    public sealed class Ladder : EntityComponent
    {
        /// <userdoc>Width along the wall in metres, centred on the entity.</userdoc>
        [Display("Width (m)", "Ladder")] public float Width = 1f;

        /// <userdoc>Height from the entity up in metres. At the top the feet stop level with it: walking on steps onto a ledge there.</userdoc>
        [Display("Height (m)", "Ladder")] public float Height = 2f;

        /// <summary>The strip as the motors read it, from the entity's pose.</summary>
        public LadderStrip Strip()
        {
            Vector3 foot = Entity.Transform.Position;
            Vector3 face = UMath.Rotate(Entity.Transform.Rotation, UMath.Forward);
            Vector3 out_ = UMath.Normalized(new Vector3(face.X, 0f, face.Z));
            return new LadderStrip(foot, out_, new Vector3(-out_.Z, 0f, out_.X), Width * 0.5f, foot.Y + Height);
        }
    }

    /// <summary>A bounce pad (The Ladder's slime): the entity stands in the middle of its top. A body
    /// landing on it faster than the motor's PadMinSpeed flies up again with Bounce of that speed,
    /// unless it crouches. Axis-aligned; no collider (the pad's block is the map's).</summary>
    [DataContract("KotH.BouncePad")]
    [Display("Bounce pad")]
    [ComponentCategory("KotH")]
    public sealed class BouncePad : EntityComponent
    {
        /// <userdoc>Width (X) and depth (Z) of the pad's top in metres.</userdoc>
        [Display("Size (m)", "Bounce pad")] public Vector2 Size = new Vector2(1f, 1f);

        /// <userdoc>Share of the landing speed the body flies up with again.</userdoc>
        [Display("Bounce", "Bounce pad")] public float Bounce = 0.9f;

        /// <summary>The pad as the motors read it.</summary>
        public PadArea Area()
            => new PadArea(Entity.Transform.Position, Size.X * 0.5f, Size.Y * 0.5f, Bounce);
    }
}
