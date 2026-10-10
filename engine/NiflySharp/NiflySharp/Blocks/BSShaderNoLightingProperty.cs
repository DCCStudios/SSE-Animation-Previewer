namespace NiflySharp.Blocks
{
    public partial class BSShaderNoLightingProperty : BSShaderLightingProperty
    {
        /// <summary>
        /// The texture glow map.
        /// </summary>
        public NiString4 FileName { get => _fileName; set => _fileName = value; }

        /// <summary>
        /// At this cosine of angle falloff will be equal to Falloff Start Opacity
        /// </summary>
        public float FalloffStartAngle { get => _falloffStartAngle; set => _falloffStartAngle = value; }

        /// <summary>
        /// At this cosine of angle falloff will be equal to Falloff Stop Opacity
        /// </summary>
        public float FalloffStopAngle { get => _falloffStopAngle; set => _falloffStopAngle = value; }

        /// <summary>
        /// Alpha falloff multiplier at start angle
        /// </summary>
        public float FalloffStartOpacity { get => _falloffStartOpacity; set => _falloffStartOpacity = value; }

        /// <summary>
        /// Alpha falloff multiplier at end angle
        /// </summary>
        public float FalloffStopOpacity { get => _falloffStopOpacity; set => _falloffStopOpacity = value; }
    }
}
