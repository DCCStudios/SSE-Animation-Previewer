using NiflySharp.Stream;

namespace NiflySharp.Blocks
{
    public partial class BSSkin_Instance : INiSkin
    {
        public new void BeforeSync(NiStreamReversible stream)
        {
            // Starfield standalone skinned meshes carry NPOS pointers in Bones because
            // the actual NiNode references are resolved at runtime against the external
            // skeleton. KeepEmptyRefs prevents FinalizeData -> CleanInvalidRefs from
            // stripping the placeholders, which would shrink the array to zero and
            // corrupt the round-trip. Earlier Bethesda formats (FO4, FO76) never use
            // this pattern in standalone files, so the flag is scoped to Starfield only.
            //
            // Pattern mirrors NiMultiTargetTransformController.ExtraTargets, with the
            // version gate following BSTriShape.BeforeSync's use of IsSSE().
            if (_bones == null)
                _bones = new NiBlockPtrArray<NiNode>();

            _bones.KeepEmptyRefs = stream.Version.IsSF();
        }
    }
}
