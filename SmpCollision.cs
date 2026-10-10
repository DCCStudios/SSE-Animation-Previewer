using V3d=OpenTK.Mathematics.Vector3d;

namespace SSEPreview;

// Port of FSMP's skinned-mesh collision (hdtCollider, hdtSkinnedMeshShape, hdtSkinnedMeshBody,
// hdtSkinnedMeshAlgorithm). Collision proxies are skinned meshes: every vertex is a sphere, every triangle a
// plate, and each contact is redistributed onto the bones that drive the two colliders. Kept in the same
// order and with the same thresholds as the plugin so an XML behaves here as it does in game.
public enum SmpShared {Public,Internal,External,Private}

public struct SmpCollider {public int V0,V1,V2;public float Flexible;}

public sealed class SmpColliderTree
{
    public const int MaxCollisionPairs=6024;
    public int Key;
    public List<SmpColliderTree> Children=[];
    public List<SmpCollider> Building=[];
    public SmpAabb AabbAll=SmpAabb.Empty,AabbMe=SmpAabb.Empty;
    public bool IsKinematic;
    public int Start,NumCollider,DynCollider,DynChild;
    bool Empty=>Children.Count==0&&Building.Count==0;

    public void Insert(ReadOnlySpan<int> keys,SmpCollider collider)
    {
        var node=this;
        for(int i=0;i<keys.Length&&i<4;i++){
            int key=keys[i];var next=node.Children.Find(c=>c.Key==key);
            if(next==null){next=new SmpColliderTree{Key=key};node.Children.Add(next);}
            node=next;
        }
        node.Building.Add(collider);
    }
    public void Clip(Func<SmpCollider,bool> remove)
    {
        foreach(var child in Children)child.Clip(remove);
        Building.RemoveAll(c=>remove(c));
        Children.RemoveAll(c=>c.Empty);
    }
    public void Optimize()
    {
        foreach(var child in Children)child.Optimize();
        Children.RemoveAll(c=>c.Empty);
        while(Children.Count==1&&Children[0].Building.Count==0)Children=Children[0].Children;
        if(Children.Count==1&&Building.Count==0){Building=Children[0].Building;Children=Children[0].Children;}
    }
    public void UpdateKinematic(Func<SmpCollider,float> flexible)
    {
        bool kinematic=true;
        for(int i=0;i<Building.Count;i++){var c=Building[i];c.Flexible=flexible(c);Building[i]=c;kinematic&=c.Flexible<SmpCollision.Epsilon;}
        foreach(var child in Children){child.UpdateKinematic(flexible);kinematic&=child.IsKinematic;}
        Building=Building.OrderByDescending(c=>c.Flexible).ToList();
        Children=Children.OrderBy(c=>c.IsKinematic).ToList();
        IsKinematic=kinematic;
        if(kinematic){DynChild=DynCollider=0;return;}
        for(DynChild=0;DynChild<Children.Count;DynChild++)if(Children[DynChild].IsKinematic)break;
        for(DynCollider=0;DynCollider<Building.Count;DynCollider++)if(Building[DynCollider].Flexible<SmpCollision.Epsilon)break;
    }
    public void Export(List<SmpCollider> all)
    {
        NumCollider=Building.Count;Start=all.Count;all.AddRange(Building);
        foreach(var child in Children)child.Export(all);
        Building=[];
    }
    public void UpdateAabb(SmpAabb[] aabbs)
    {
        foreach(var child in Children)child.UpdateAabb(aabbs);
        if(NumCollider>0){
            var box=aabbs[Start];
            for(int i=1;i<NumCollider;i++)box.Merge(aabbs[Start+i]);
            AabbMe=box;
        }
        var all=AabbMe;
        foreach(var child in Children)all.Merge(child.AabbAll);
        AabbAll=all;
    }
    public IEnumerable<SmpColliderTree> Nodes(){yield return this;foreach(var child in Children)foreach(var node in child.Nodes())yield return node;}

    // Tree-node pairs whose own colliders may touch. Left mode splits this tree, right mode drills into the other.
    [ThreadStatic] static Stack<(SmpColliderTree A,SmpColliderTree B,bool Right)>? walk;
    public void CheckCollision(SmpColliderTree other,List<(SmpColliderTree A,SmpColliderTree B)> pairs)
    {
        var stack=walk??=new(64);stack.Clear();
        stack.Push((this,other,false));
        while(stack.Count>0){
            if(pairs.Count>MaxCollisionPairs)break;
            var (a,b,right)=stack.Pop();
            if(a.IsKinematic&&b.IsKinematic)continue;
            if(!right){
                if(!a.AabbAll.CollideWith(b.AabbAll))continue;
                if(a.NumCollider>0&&a.AabbMe.CollideWith(b.AabbAll)){
                    if(a.AabbMe.CollideWith(b.AabbMe))pairs.Add((a,b));
                    int count=a.IsKinematic?b.DynChild:b.Children.Count;
                    for(int i=0;i<count;i++)stack.Push((a,b.Children[i],true));
                }
                int own=b.IsKinematic?a.DynChild:a.Children.Count;
                for(int i=0;i<own;i++)stack.Push((a.Children[i],b,false));
            }else{
                if(a.NumCollider==0)continue;
                if(!a.AabbMe.CollideWith(b.AabbAll))continue;
                if(a.AabbMe.CollideWith(b.AabbMe))pairs.Add((a,b));
                int count=a.IsKinematic?b.DynChild:b.Children.Count;
                for(int i=0;i<count;i++)stack.Push((a,b.Children[i],true));
            }
        }
    }
}

// One collider set over a body's vertices: spheres (per-vertex-shape) or plates (per-triangle-shape).
public sealed class SmpShape
{
    public readonly SmpBody Owner;public readonly bool Triangles;
    public double Margin=1,Penetration=1;
    public readonly SmpColliderTree Tree=new();
    public SmpCollider[] Colliders=[];public SmpAabb[] Aabbs=[];
    // Triangle shapes also collide as spheres at their vertices (FSMP m_verticesCollision).
    public SmpShape? VertexCollision;
    internal long UpdatedStep=-1;
    public SmpShape(SmpBody owner,bool triangles){Owner=owner;Triangles=triangles;}
    public int BonesPerCollider=>Triangles?12:4;
    int Vertex(in SmpCollider c,int slot)=>slot==0?c.V0:slot==1?c.V1:c.V2;
    public float BoneWeight(in SmpCollider c,int i)=>Triangles?Owner.Weights[Vertex(c,i/4)*4+i%4]:Owner.Weights[c.V0*4+i];
    public int BoneIndex(in SmpCollider c,int i)=>Triangles?Owner.BoneIndex[Vertex(c,i/4)*4+i%4]:Owner.BoneIndex[c.V0*4+i];

    public void AutoGenerate()
    {
        Tree.Children.Clear();Span<int> keys=stackalloc int[4];
        for(int v=0;v<Owner.SkinPos.Length;v++){
            int count=0;
            for(int j=0;j<4;j++)if(Owner.Weights[v*4+j]>SmpCollision.Epsilon)keys[count++]=Owner.BoneIndex[v*4+j];
            Tree.Insert(keys[..count],new SmpCollider{V0=v});
        }
    }
    public void AddTriangle(int a,int b,int c)
    {
        Span<int> keys=stackalloc int[12];Span<float> weights=stackalloc float[12];int count=0;
        foreach(int vertex in (ReadOnlySpan<int>)[a,b,c])for(int wi=0;wi<4;wi++){
            float weight=Owner.Weights[vertex*4+wi];if(weight<SmpCollision.Epsilon)continue;
            int bone=Owner.BoneIndex[vertex*4+wi];int found=-1;
            for(int k=0;k<count;k++)if(keys[k]==bone){found=k;break;}
            if(found>=0)weights[found]+=weight;else{keys[count]=bone;weights[count]=weight;count++;}
        }
        // Heaviest bone first; ties go to the lower bone index.
        for(int i=1;i<count;i++){
            float w=weights[i];int k=keys[i];int j=i;
            while(j>0&&(weights[j-1]<w||(weights[j-1]==w&&keys[j-1]>k))){weights[j]=weights[j-1];keys[j]=keys[j-1];j--;}
            weights[j]=w;keys[j]=k;
        }
        Tree.Insert(keys[..count],new SmpCollider{V0=a,V1=b,V2=c});
    }
    // Drops colliders with no bone weight above that bone's weight-threshold.
    public void ClipColliders()=>Tree.Clip(c=>{
        for(int i=0;i<BonesPerCollider;i++){
            float weight=BoneWeight(c,i);
            if(weight>SmpCollision.Epsilon&&weight>Owner.SkinnedBones[BoneIndex(c,i)].WeightThreshold)return false;
        }
        return true;
    });
    public void FinishBuild()
    {
        Tree.Optimize();
        Tree.UpdateKinematic(c=>Triangles?(Owner.Flexible(c.V0)+Owner.Flexible(c.V1)+Owner.Flexible(c.V2))/3:Owner.Flexible(c.V0));
        var all=new List<SmpCollider>();Tree.Export(all);Colliders=all.ToArray();Aabbs=new SmpAabb[Colliders.Length];
        if(!Triangles)return;
        VertexCollision=new SmpShape(Owner,false){Margin=Margin};
        VertexCollision.AutoGenerate();VertexCollision.ClipColliders();VertexCollision.FinishBuild();
    }
    public void MarkUsed(bool[] used)
    {
        foreach(var c in Colliders){used[c.V0]=true;if(Triangles){used[c.V1]=true;used[c.V2]=true;}}
        VertexCollision?.MarkUsed(used);
    }
    public void Remap(int[] map)
    {
        for(int i=0;i<Colliders.Length;i++){var c=Colliders[i];c.V0=map[c.V0];if(Triangles){c.V1=map[c.V1];c.V2=map[c.V2];}Colliders[i]=c;}
        VertexCollision?.Remap(map);
    }
    public void Update(long step)
    {
        if(UpdatedStep==step)return;UpdatedStep=step;
        var pos=Owner.Pos;var margins=Owner.MarginMultiplier;
        double penetration=Math.Abs(Penetration);
        void Box(int i){
            var c=Colliders[i];
            if(!Triangles){
                var radius=new V3d(margins[c.V0]*Margin);
                Aabbs[i]=new SmpAabb{Min=pos[c.V0]-radius,Max=pos[c.V0]+radius};
                return;
            }
            V3d p0=pos[c.V0],p1=pos[c.V1],p2=pos[c.V2];
            var margin=new V3d(Math.Max((margins[c.V0]+margins[c.V1]+margins[c.V2])*Margin/3,penetration));
            Aabbs[i]=new SmpAabb{Min=V3d.ComponentMin(V3d.ComponentMin(p0,p1),p2)-margin,Max=V3d.ComponentMax(V3d.ComponentMax(p0,p1),p2)+margin};
        }
        if(Colliders.Length>=4096)Parallel.For(0,Colliders.Length,Box);else for(int i=0;i<Colliders.Length;i++)Box(i);
        Tree.UpdateAabb(Aabbs);
    }
}

public sealed class SmpSkinnedBone
{
    public required SmpBone Bone;public SmpTransform VertexToBone;public float WeightThreshold;public bool IsKinematic;
}

// A named collision mesh from the physics XML, skinned to simulation bones.
public sealed class SmpBody
{
    public string Name="";public required SmpSystem System;
    public readonly List<SmpSkinnedBone> SkinnedBones=[];
    public V3d[] SkinPos=[];public float[] Weights=[];public int[] BoneIndex=[];
    public V3d[] Pos=[];public double[] MarginMultiplier=[];
    // NiSkinData bone bounds in bone space. FSMP's broadphase uses them for bodies with more than ten colliders.
    public (V3d Center,double Radius)[] BoneBounds=[];
    public SmpAabb Broadphase=SmpAabb.Empty;
    public bool UseBoundingSpheres=>Shape.Colliders.Length>10&&BoneBounds.Length==SkinnedBones.Count;
    public bool IsKinematic,Disabled;
    public SmpShared Shared=SmpShared.Public;
    public readonly List<string> Tags=[];
    public readonly HashSet<string> CanCollideWithTags=new(StringComparer.OrdinalIgnoreCase),NoCollideWithTags=new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<SmpBone> CanCollideWithBones=[],NoCollideWithBones=[];
    public string? DisableTag;public int DisablePriority;
    public SmpShape Shape=null!;
    internal long UpdatedStep=-1;
    // Columns of each bone's vertex-to-world matrix plus its scaled margin multiplier.
    V3d[] axisX=[],axisY=[],axisZ=[],origin=[];double[] boneMargin=[];

    // Share of a vertex carried by simulated bones; zero means the vertex only follows the animation.
    public float Flexible(int vertex)
    {
        float sum=0;
        for(int i=0;i<4;i++){
            float weight=Weights[vertex*4+i];if(weight<SmpCollision.Epsilon)break;
            if(!SkinnedBones[BoneIndex[vertex*4+i]].IsKinematic)sum+=weight;
        }
        return sum;
    }
    // Heaviest influence first, as FSMP sorts each vertex after reading it.
    public void SortWeights()
    {
        for(int v=0;v<SkinPos.Length;v++)for(int i=0;i<4;i++)for(int j=0;j<3;j++){
            int a=v*4+j,b=a+1;
            if(Weights[a]<Weights[b]){(Weights[a],Weights[b])=(Weights[b],Weights[a]);(BoneIndex[a],BoneIndex[b])=(BoneIndex[b],BoneIndex[a]);}
        }
    }
    public void FinishBuild()
    {
        Shape.ClipColliders();
        IsKinematic=true;
        foreach(var bone in SkinnedBones){bone.IsKinematic=bone.Bone.Kinematic;if(!bone.IsKinematic)IsKinematic=false;}
        Shape.FinishBuild();
        // Keep only the vertices a collider still uses.
        var used=new bool[SkinPos.Length];Shape.MarkUsed(used);
        var map=new int[SkinPos.Length];int count=0;
        for(int i=0;i<used.Length;i++){
            if(!used[i])continue;
            SkinPos[count]=SkinPos[i];
            for(int k=0;k<4;k++){Weights[count*4+k]=Weights[i*4+k];BoneIndex[count*4+k]=BoneIndex[i*4+k];}
            map[i]=count++;
        }
        Shape.Remap(map);
        Array.Resize(ref SkinPos,count);Array.Resize(ref Weights,count*4);Array.Resize(ref BoneIndex,count*4);
        Pos=new V3d[count];MarginMultiplier=new double[count];
        int bones=SkinnedBones.Count;axisX=new V3d[bones];axisY=new V3d[bones];axisZ=new V3d[bones];origin=new V3d[bones];boneMargin=new double[bones];
    }
    public bool CanCollideWith(SmpBone bone)=>CanCollideWithBones.Count>0?CanCollideWithBones.Contains(bone):!NoCollideWithBones.Contains(bone);
    public bool CanCollideWith(SmpBody other)
    {
        if(Disabled||other.Disabled)return false;
        // One actor is simulated, so every system shares a skeleton: internal always passes, external never does.
        if(Shared==SmpShared.External)return false;
        if(Shared==SmpShared.Private&&System!=other.System)return false;
        if(IsKinematic&&other.IsKinematic)return false;
        if(CanCollideWithTags.Count==0){
            foreach(string tag in other.Tags)if(NoCollideWithTags.Contains(tag))return false;
            return true;
        }
        foreach(string tag in other.Tags)if(CanCollideWithTags.Contains(tag))return true;
        return false;
    }
    // Skins the collision vertices with the bones' predicted transforms and refreshes the collider boxes.
    public void Update(long step)
    {
        if(UpdatedStep==step)return;UpdatedStep=step;
        for(int i=0;i<SkinnedBones.Count;i++){
            var skinned=SkinnedBones[i];var bone=skinned.Bone.Current;var world=bone*skinned.VertexToBone;
            axisX[i]=SmpTransform.Rotate(world.Rotation,V3d.UnitX)*world.Scale;axisY[i]=SmpTransform.Rotate(world.Rotation,V3d.UnitY)*world.Scale;
            axisZ[i]=SmpTransform.Rotate(world.Rotation,V3d.UnitZ)*world.Scale;origin[i]=world.Origin;
            boneMargin[i]=skinned.Bone.MarginMultiplier*bone.Scale;
        }
        void Skin(int v){
            var p=SkinPos[v];V3d sum=V3d.Zero;double margin=0;
            for(int k=0;k<4;k++){
                double w=Weights[v*4+k];if(w==0)continue;int b=BoneIndex[v*4+k];
                sum+=(origin[b]+axisX[b]*p.X+axisY[b]*p.Y+axisZ[b]*p.Z)*w;margin+=boneMargin[b]*w;
            }
            Pos[v]=sum;MarginMultiplier[v]=margin;
        }
        if(SkinPos.Length>=4096)Parallel.For(0,SkinPos.Length,Skin);else for(int v=0;v<SkinPos.Length;v++)Skin(v);
        Shape.Update(step);
        if(!UseBoundingSpheres){Broadphase=Shape.Tree.AabbAll;return;}
        UpdateBounds();
    }
    // FSMP's broadphase for bodies with more than ten colliders: a box around the bone bounding spheres, which needs
    // no skinning. The vertices are skinned only when a pair's boxes overlap.
    public void UpdateBounds()
    {
        var box=SmpAabb.Empty;
        for(int i=0;i<SkinnedBones.Count;i++){
            var bone=SkinnedBones[i].Bone.Current;var center=bone.Apply(BoneBounds[i].Center);var radius=new V3d(bone.Scale*BoneBounds[i].Radius);
            box.Merge(new SmpAabb{Min=center-radius,Max=center+radius});
        }
        Broadphase=box;
    }
}

// A solver contact between two bones, already merged from every collider pair that touched.
public readonly record struct SmpContact(SmpBone A,SmpBone B,V3d WorldA,V3d WorldB,V3d Normal,double Depth);

public static class SmpCollision
{
    public const float Epsilon=1.1920929e-7f;
    public const int MaxCollisionCount=512;
    struct Result {public V3d PosA,PosB,NormOnB;public SmpCollider ColliderA,ColliderB;public double Depth;}
    struct Merge {public V3d Normal,PosA,PosB;public double Weight;}

    public static bool NeedsCollision(SmpBody a,SmpBody b)=>a!=b&&!(a.IsKinematic&&b.IsKinematic)&&a.CanCollideWith(b)&&b.CanCollideWith(a);

    // FSMP updateActiveState: among bodies that share a disable-tag, only the highest priority stays active,
    // and all of them switch off when another body carries that tag as an ordinary tag.
    public static void UpdateActiveState(IReadOnlyList<SmpBody> bodies)
    {
        var tags=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var groups=new Dictionary<string,List<SmpBody>>(StringComparer.OrdinalIgnoreCase);
        foreach(var body in bodies){
            if(string.IsNullOrEmpty(body.DisableTag))foreach(string tag in body.Tags)tags.Add(tag);
            else{if(!groups.TryGetValue(body.DisableTag,out var list))groups[body.DisableTag]=list=[];list.Add(body);}
        }
        foreach(var (tag,list) in groups){
            foreach(var body in list)body.Disabled=true;
            if(!tags.Contains(tag))list.OrderByDescending(b=>b.DisablePriority).First().Disabled=false;
        }
    }

    static bool VertexVertex(SmpShape a,SmpShape b,in SmpCollider ca,in SmpCollider cb,ref Result result)
    {
        var pos0=a.Owner.Pos[ca.V0];var pos1=b.Owner.Pos[cb.V0];
        double r0=a.Owner.MarginMultiplier[ca.V0]*a.Margin,r1=b.Owner.MarginMultiplier[cb.V0]*b.Margin;
        var diff=pos0-pos1;double dist2=diff.LengthSquared,sum=r0+r1;
        if(dist2>sum*sum)return false;
        double len=Math.Sqrt(dist2);var normal=len>Epsilon?diff/len:V3d.UnitX;
        result.NormOnB=normal;result.Depth=len-sum;result.PosA=pos0-normal*r0;result.PosB=pos1+normal*r1;result.ColliderA=ca;result.ColliderB=cb;
        return true;
    }
    static bool VertexTriangle(SmpShape a,SmpShape b,in SmpCollider ca,in SmpCollider cb,ref Result result)
    {
        var s=a.Owner.Pos[ca.V0];double r=a.Owner.MarginMultiplier[ca.V0]*a.Margin;
        var owner=b.Owner;V3d p0=owner.Pos[cb.V0],p1=owner.Pos[cb.V1],p2=owner.Pos[cb.V2];
        double margin=(owner.MarginMultiplier[cb.V0]+owner.MarginMultiplier[cb.V1]+owner.MarginMultiplier[cb.V2])/3;
        double penetration=b.Penetration*margin;margin*=b.Margin;
        if(penetration>-Epsilon&&penetration<Epsilon)penetration=0;
        var raw=V3d.Cross(p1-p0,p2-p0);double len=raw.Length;
        if(len<Epsilon)return false;
        var normal=raw/len;
        // A negative penetration flips which side of the plate counts as outside.
        if(penetration<0){normal=-normal;penetration=-penetration;}
        double distance=V3d.Dot(s-p0,normal);var projection=s-normal*distance;double radius=r+margin;bool inside;
        if(penetration>=Epsilon)inside=distance<radius&&distance>=-penetration;
        else{
            // Without penetration the plate is two-sided.
            if(distance<0){distance=-distance;normal=-normal;}
            inside=distance<radius;
        }
        if(!inside)return false;
        V3d ap=projection-p0,bp=projection-p1,cp=projection-p2;
        double aa=V3d.Cross(bp,cp).Length,ab=V3d.Cross(cp,ap).Length,ac=V3d.Cross(ap,bp).Length;
        // The projection lies in the triangle when no two sub-areas exceed the whole.
        if(ac+ab>len||ab+aa>len||aa+ac>len)return false;
        result.ColliderA=ca;result.ColliderB=cb;result.NormOnB=normal;result.PosA=s-normal*r;result.PosB=projection;result.Depth=distance-radius;
        return result.Depth<-Epsilon;
    }

    // Sphere colliders of `spheres` against the colliders of `other`. With swap the results are reported
    // from the other shape's point of view (FSMP CollisionCheckBase2<T,true>).
    static int Check(SmpShape spheres,SmpShape other,bool swap,Result[] results)
    {
        var pairs=treePairs??=new(256);pairs.Clear();
        spheres.Tree.CheckCollision(other.Tree,pairs);
        if(pairs.Count==0||pairs.Count>SmpColliderTree.MaxCollisionPairs)return 0;
        // Each tree-node pair yields at most one contact and pairs do not share state, so large sets run in parallel
        // (as FSMP does from 32 pairs). Results are gathered in pair order and cut at MaxCollisionCount, which gives
        // exactly the contacts a sequential pass would.
        int total=pairs.Count;
        var found=System.Buffers.ArrayPool<Result>.Shared.Rent(total);var hit=System.Buffers.ArrayPool<bool>.Shared.Rent(total);
        try{
            if(total>=32&&!serial)Parallel.For(0,total,k=>hit[k]=CheckPair(spheres,other,swap,pairs[k].A,pairs[k].B,out found[k]));
            else for(int k=0;k<total;k++)hit[k]=CheckPair(spheres,other,swap,pairs[k].A,pairs[k].B,out found[k]);
            int count=0;
            for(int k=0;k<total&&count<MaxCollisionCount;k++)if(hit[k])results[count++]=found[k];
            return count;
        }finally{System.Buffers.ArrayPool<Result>.Shared.Return(found);System.Buffers.ArrayPool<bool>.Shared.Return(hit);}
    }
    [ThreadStatic] static List<int>? pairListA,pairListB;
    static bool CheckPair(SmpShape spheres,SmpShape other,bool swap,SmpColliderTree a,SmpColliderTree b,out Result result)
    {
        result=default;var listA=pairListA??=new(64);var listB=pairListB??=new(64);
        {
            int asize=b.IsKinematic?a.DynCollider:a.NumCollider,bsize=a.IsKinematic?b.DynCollider:b.NumCollider;
            listA.Clear();listB.Clear();var boxA=SmpAabb.Empty;var boxB=b.AabbMe;
            for(int i=a.Start;i<a.Start+asize;i++)if(spheres.Aabbs[i].CollideWith(boxB)){listA.Add(i);boxA.Merge(spheres.Aabbs[i]);}
            if(listA.Count>0){
                boxB=SmpAabb.Empty;
                for(int j=b.Start;j<b.Start+bsize;j++)if(other.Aabbs[j].CollideWith(boxA)){listB.Add(j);boxB.Merge(other.Aabbs[j]);}
            }
            if(listA.Count==0||listB.Count==0)return false;
            // One contact per tree-node pair: the deepest.
            Result best=default,temp=default;bool any=false;
            foreach(int i in listA){
                if(!spheres.Aabbs[i].CollideWith(boxB))continue;
                foreach(int j in listB){
                    if(!spheres.Aabbs[i].CollideWith(other.Aabbs[j]))continue;
                    bool hit=other.Triangles?VertexTriangle(spheres,other,spheres.Colliders[i],other.Colliders[j],ref temp):VertexVertex(spheres,other,spheres.Colliders[i],other.Colliders[j],ref temp);
                    if(hit&&(!any||best.Depth>temp.Depth)){any=true;best=temp;}
                }
            }
            if(!any)return false;
            if(swap)best=new Result{PosA=best.PosB,PosB=best.PosA,ColliderA=best.ColliderB,ColliderB=best.ColliderA,NormOnB=-best.NormOnB,Depth=best.Depth};
            result=best;return true;
        }
    }
    static void DoMerge(SmpShape a,SmpShape b,Result[] results,int count,Dictionary<(int,int),Merge> cells)
    {
        Array.Sort(results,0,count,Comparer<Result>.Create((x,y)=>x.Depth.CompareTo(y.Depth)));
        for(int n=0;n<count;n++){
            ref var res=ref results[n];
            if(res.Depth>=-Epsilon)break;
            double flexible=Math.Max(res.ColliderA.Flexible,res.ColliderB.Flexible);
            if(flexible<Epsilon)continue;
            // Cubic weight on the normal bakes the depth into its length; positions use the squared weight.
            double w=flexible*res.Depth,w2=w*w;var normal=res.NormOnB*w*w2;var posA=res.PosA*w2;var posB=res.PosB*w2;
            for(int ib=0;ib<a.BonesPerCollider;ib++){
                float w0=a.BoneWeight(res.ColliderA,ib);int bone0=a.BoneIndex(res.ColliderA,ib);
                if(w0<=a.Owner.SkinnedBones[bone0].WeightThreshold)continue;
                for(int jb=0;jb<b.BonesPerCollider;jb++){
                    float w1=b.BoneWeight(res.ColliderB,jb);int bone1=b.BoneIndex(res.ColliderB,jb);
                    if(w1<=b.Owner.SkinnedBones[bone1].WeightThreshold)continue;
                    if(a.Owner.SkinnedBones[bone0].IsKinematic&&b.Owner.SkinnedBones[bone1].IsKinematic)continue;
                    ref var cell=ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(cells,(bone0,bone1),out _);
                    // Contributions pushing against what is already merged are dropped.
                    if(cell.Weight>Epsilon&&V3d.Dot(cell.Normal,normal)<0)continue;
                    cell.Weight+=w2;cell.Normal+=normal;cell.PosA+=posA;cell.PosB+=posB;
                }
            }
        }
    }
    static void Process(SmpShape shape0,SmpShape shape1,Result[] buffer,Dictionary<(int,int),Merge> cells)
    {
        // shape0 belongs to body 0. A triangle shape on the left is checked from the sphere side and swapped back.
        int count=shape0.Triangles?Check(shape1,shape0,true,buffer):Check(shape0,shape1,false,buffer);
        if(count>0)DoMerge(shape0,shape1,buffer,count,cells);
    }

    [ThreadStatic] static Result[]? buffer;
    [ThreadStatic] static Dictionary<(int,int),Merge>? cells;
    [ThreadStatic] static List<(SmpColliderTree A,SmpColliderTree B)>? treePairs;
    [ThreadStatic] static bool serial;
    // Collides two bodies and appends one contact per bone pair.
    public static void Collide(SmpBody body0,SmpBody body1,long step,List<SmpContact> contacts,bool sequential=false)
    {
        serial=sequential;
        buffer??=new Result[MaxCollisionCount];cells??=new();cells.Clear();
        var s0=body0.Shape;var s1=body1.Shape;
        // Both bodies, and the vertex shapes of triangle bodies, are skinned by the caller before pairs run in parallel.
        if(s0.Triangles&&s1.Triangles){
            Process(s0,s1.VertexCollision!,buffer,cells);
            Process(s0.VertexCollision!,s1,buffer,cells);
        }
        else Process(s0,s1,buffer,cells);
        foreach(var ((i,j),cell) in cells){
            if(cell.Weight<Epsilon)continue;
            var bone0=body0.SkinnedBones[i];var bone1=body1.SkinnedBones[j];
            if(!body1.CanCollideWith(bone0.Bone)||!body0.CanCollideWith(bone1.Bone))continue;
            if(bone0.IsKinematic&&bone1.IsKinematic)continue;
            if(bone0.Bone==bone1.Bone)continue;
            double inverse=1/cell.Weight;var normal=cell.Normal*inverse;
            if(normal.LengthSquared<Epsilon*Epsilon)continue;
            double depth=-normal.Length;normal=-normal.Normalized();
            if(depth>=-Epsilon)continue;
            contacts.Add(new(bone0.Bone,bone1.Bone,cell.PosA*inverse,cell.PosB*inverse,normal,depth));
        }
    }
}
