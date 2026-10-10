using FO4_Base_Library;
using NiflySharp.Blocks;
using static SSEPreview.NifAnimation;
namespace SSEPreview;
internal static class LightingControllerTests
{
    internal static void Run(Action<bool,string> check)
    {
        string root=Path.Combine(SelfTest.Mods,@"Anchor MCO Animations v2 - Cloud Fenrir\meshes\magiceffects\FF7Fenrir");
        if(!Directory.Exists(root)){SelfTest.Skip("Real Anchor lighting UV controller checks: "+SelfTest.Missing(root));return;}
        int found=0;
        foreach(string file in Directory.EnumerateFiles(root,"*.nif",SearchOption.AllDirectories)){
            var nif=new Nifcontent_Class_Manolo();nif.Load_Manolo(File.ReadAllBytes(file));var animation=new NifAnimation(nif);
            var controllers=nif.Blocks.OfType<BSLightingShaderPropertyFloatController>().Where(c=>Get(c,"ControlledVariable")?.ToString()=="VOffset").ToArray();
            foreach(var controller in controllers){
                var shader=(BSLightingShaderProperty)animation.Block(controller.Target)!;
                float Expected(double age){var interp=animation.ResolveInterpolator(controller,"Interpolator",age,out var time);var data=animation.Block(Get(interp,"Data"));return Number(Sample(Get(data,"Data"),time)??Get(interp,"Value"));}
                float first=Expected(.2),second=Expected(.5);
                animation.Evaluate(.2);float actual=shader.UVOffset.V;
                animation.Evaluate(.5);float later=shader.UVOffset.V;
                animation.Evaluate(.2);
                check(Math.Abs(actual-first)<1e-6&&Math.Abs(later-second)<1e-6&&Math.Abs(shader.UVOffset.V-first)<1e-6,"Real Anchor lighting UV controller evaluates and rewinds: "+Path.GetFileName(file));
                found++;
            }
        }
        check(found==22,"All 22 authored Anchor lighting UV controllers were exercised");
    }
}
