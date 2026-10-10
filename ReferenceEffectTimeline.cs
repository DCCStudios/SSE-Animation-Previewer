namespace SSEPreview;
public sealed record TimedReferenceEffect(string Key,double Time,bool Stop=false,int Target=1,System.Numerics.Vector3 Position=default,float Heading=0,bool FollowCharacter=false);
internal static class ReferenceEffectTimeline
{
    internal static List<EffectCue> Build(Assets assets,Records records,IEnumerable<TimedReferenceEffect> actions,List<string> diagnostics)
    {
        var cues=new List<EffectCue>();var active=new Dictionary<(string Key,int Target),List<int>>();
        foreach(var action in actions.OrderBy(a=>a.Time)){
            if(!double.IsFinite(action.Time)||action.Time<0){diagnostics.Add("Reference-effect action has an invalid time.");continue;}
            var identity=(action.Key.ToLowerInvariant(),action.Target);
            if(action.Target<1||!float.IsFinite(action.Position.X)||!float.IsFinite(action.Position.Y)||!float.IsFinite(action.Position.Z)||!float.IsFinite(action.Heading)){diagnostics.Add("Reference-effect target has an invalid position or identity.");continue;}
            if(action.Stop){
                if(active.Remove(identity,out var indices))foreach(int index in indices)cues[index]=cues[index] with{RemovedAt=action.Time};
                continue;
            }
            if(!records.All.TryGetValue(action.Key,out var record)||record.Kind!="RFCT"){
                diagnostics.Add("Missing reference effect: "+action.Key);continue;
            }
            var entry=ReferenceEffects.Resolve(record,records,assets);diagnostics.AddRange(entry.Issues);
            if(entry.Model.Length==0||!assets.Exists(entry.Model))continue;
            if(!active.TryGetValue(identity,out var playing))active[identity]=playing=[];
            playing.Add(cues.Count);
            cues.Add(new(action.Time,entry.Model,0,0,double.PositiveInfinity,1,action.Position,
                entry.Name+$" · manual timeline play on Target {action.Target} at {action.Position}",Heading:action.Heading,WorldSpace:!action.FollowCharacter,FollowActor:action.FollowCharacter,ArtLifecycle:true));
        }
        if(cues.Count>0)diagnostics.Add("Reference-effect timeline actions preview art at explicit world or character-relative target positions. Supported root NamedNode attachments follow the selected character; Papyrus hit conditions, other target attachment techniques, and actor-surface shaders are not simulated.");
        return cues;
    }
}
