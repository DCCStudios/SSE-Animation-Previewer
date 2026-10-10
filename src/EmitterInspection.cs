using System.Text.Json;
using FO4_Base_Library;
using NiflySharp;
using NiflySharp.Blocks;
using static SSEPreview.NifAnimation;
namespace SSEPreview;
internal static class EmitterInspection
{
    internal static void Run(string path)
    {
        var nif=new Nifcontent_Class_Manolo();nif.Load_Manolo(File.ReadAllBytes(path));
        var animation=new NifAnimation(nif);var sequences=animation.SequenceNames;
        object Curve(NiTimeController controller,string property){
            var interpolator=animation.ResolveInterpolator(controller,property,0,out var time);
            var data=animation.Block(Get(interpolator,"Data"));var group=Get(data,"Data");
            return new{interpolator=interpolator?.GetType().Name,value=Get(interpolator,"Value")?.ToString(),time,
                interpolation=Get(group,"Interpolation")?.ToString(),keys=Items(Get(group,"Keys")).Select(k=>new{time=Number(Get(k,"Time")),value=Get(k,"Value")?.ToString(),forward=Get(k,"Forward")?.ToString(),backward=Get(k,"Backward")?.ToString()})};
        }
        var systems=new List<object>();
        foreach(var system in nif.Blocks.OfType<NiParticleSystem>()){
            var emitters=Items(Get(system,"Modifiers")).Select(animation.Block).OfType<NiPSysMeshEmitter>().ToArray();
            var timelines=new List<object>();
            foreach(string sequence in sequences.Length>0?sequences:[""]){
                animation.SelectedSequence=sequence.Length==0?null:sequence;
                foreach(var controller in nif.Blocks.OfType<NiPSysEmitterCtlr>().Where(c=>ReferenceEquals(animation.Block(c.Target),system)))
                    timelines.Add(new{sequence,controller=nif.Blocks.IndexOf(controller),modifier=controller.ModifierName.ToString(),rate=Curve(controller,"Interpolator"),enabled=Curve(controller,"VisibilityInterpolator")});
            }
            var particleData=animation.Block(system.Data_NiBlockRef_NPSD)??animation.Block(system.DataRef);
            object? strip=particleData is BSStripPSysData d?new{d.MaxPointCount,d.StartCapSize,d.EndCapSize,d.DoZPrepass,runtimeCapacity=d.MaxPointCount+3,pointLifetime=d.MaxPointCount*(1f/60),updates=Items(Get(system,"Modifiers")).Select(animation.Block).OfType<BSPSysStripUpdateModifier>().Select(m=>new{name=m.Name.ToString(),m.UpdateDeltaTime,m.Active})}:null;
            systems.Add(new{name=system.Name.ToString(),index=nif.Blocks.IndexOf(system),strip,sources=emitters.Select(e=>new{emitter=nif.Blocks.IndexOf(e),links=e.EmitterMeshes.Indices.ToArray()}),timelines});
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"emitter-inspection.json"),JsonSerializer.Serialize(new{path,systems},new JsonSerializerOptions{WriteIndented=true}));
    }
}