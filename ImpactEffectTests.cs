using System.Text;
namespace SSEPreview;
internal static class ImpactEffectTests
{
    internal static void Run(Action<bool,string> check)
    {
        var records=new Records();var assets=new Assets();
        GameRecord Record(string kind,uint id,string name){var r=new GameRecord{Kind=kind,Plugin="impact.esp",Key=$"impact.esp|{id:X6}",Id=id};r.Fields.Add(new("EDID",Encoding.UTF8.GetBytes(name)));records.All[r.Key]=r;return r;}
        var impact=Record("IPCT",1,"StoneBurst");var data=new byte[24];BitConverter.GetBytes(.25f).CopyTo(data,0);BitConverter.GetBytes(2u).CopyTo(data,4);
        impact.Fields.Add(new("DATA",data));impact.Fields.Add(new("MODL",Encoding.UTF8.GetBytes(@"effects\burst.nif")));
        Record("MATT",2,"Stone");var set=Record("IPDS",3,"StoneImpacts");set.Fields.Add(new("PNAM",BitConverter.GetBytes(2u).Concat(BitConverter.GetBytes(1u)).ToArray()));
        var explosion=Record("EXPL",4,"Blast");var explosionData=new byte[52];BitConverter.GetBytes(3u).CopyTo(explosionData,12);BitConverter.GetBytes(80f).CopyTo(explosionData,32);explosion.Fields.Add(new("DATA",explosionData));
        var projectile=Record("PROJ",5,"Beam");var projectileData=new byte[84];BitConverter.GetBytes(4u).CopyTo(projectileData,36);projectile.Fields.Add(new("DATA",projectileData));
        var catalog=ImpactEffects.Catalog(records,assets);var entry=catalog.Single(e=>e.Record==impact);var blast=catalog.Single(e=>e.Record==explosion);
        check(entry.Duration==.25f&&entry.Model==@"meshes\effects\burst.nif"&&entry.Details.Contains("Projectile reflection"),"Impact catalog reads authored duration, orientation and virtual mesh path");
        check(entry.Details.Contains("StoneImpacts")&&entry.Details.Contains("Stone [impact.esp|000002]"),"Impact catalog resolves per-material data-set mappings");
        check(entry.Details.Contains("Missing mesh")&&blast.Details.Contains("Beam [impact.esp|000005]")&&blast.Details.Contains("radius: 80"),"Impact catalog exposes missing meshes and explosion/projectile links without inferring collision");
        BitConverter.GetBytes(float.NaN).CopyTo(data,0);
        check(ImpactEffects.Catalog(records,assets).Single(e=>e.Record==impact).Duration==10,"Invalid impact duration uses an explicit finite preview limit");
        foreach(string kind in new[]{"IPCT","IPDS","MATT"}){
            using var memory=new MemoryStream();using var writer=new BinaryWriter(memory,Encoding.UTF8,true);
            writer.Write(Encoding.ASCII.GetBytes(kind));writer.Write(0u);writer.Write(0u);writer.Write(1u);writer.Write(0u);writer.Write(0u);writer.Flush();
            check(Records.Parse(memory.ToArray(),"impact.esp").Single().Kind==kind,"Plugin parser retains "+kind+" records for impact selection");
        }
    }
}
