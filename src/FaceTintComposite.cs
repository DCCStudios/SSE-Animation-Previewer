using System.Drawing;
using System.Security.Cryptography;
using FO4_Base_Library;

namespace SSEPreview;

/// <summary>Builds the face tint texture the Creation Kit bakes for an NPC, from the race's tint masks and the
/// actor's layers. The rule is the game's own run-time composite (image-space shader ISAlphaBlend, AE 27040):
/// start from mid grey, then for each mask in the race's order blend towards the layer colour by the layer's
/// strength times the mask's red channel. Mid grey leaves the face texture as painted.</summary>
public static class FaceTintComposite
{
    public const int Size=512;
    static string CacheFolder=>Path.Combine(Path.GetTempPath(),"SSEAnimationPreviewer","face-tints");
    static readonly Dictionary<string,(byte[] Red,int Width,int Height)?> masks=new(StringComparer.OrdinalIgnoreCase);
    static long masksRevision=-1;

    /// <summary>The composite as BGRA pixels, Size x Size. Masks that are missing or fail to decode are named in notes.</summary>
    public static byte[] Compose(Assets assets,IReadOnlyList<(RaceTint Mask,Color Color,float Alpha)> layers,List<string> notes)
    {
        if(masksRevision!=assets.Revision){masks.Clear();masksRevision=assets.Revision;}
        var r=new float[Size*Size];var g=new float[Size*Size];var b=new float[Size*Size];
        Array.Fill(r,.5f);Array.Fill(g,.5f);Array.Fill(b,.5f);
        int used=0;
        foreach(var (mask,color,alpha) in layers){
            if(alpha<=0||mask.Texture.Length==0)continue;
            // The game draws at most 16 layers.
            if(used==16){notes.Add("More than 16 tint layers are in use; the game draws the first 16.");break;}
            string path=Assets.Normalize(mask.Texture);if(!path.StartsWith(@"textures\",StringComparison.OrdinalIgnoreCase))path=@"textures\"+path;
            if(!masks.TryGetValue(path,out var decoded)){
                decoded=null;
                try{
                    if(assets.Exists(path)){
                        var texture=DirectXTexWrapperCLI.Loader.ConvertForBitmap(assets.Read(path));
                        if(texture!=null&&texture.Loaded&&texture.Levels is {Count:>0}&&texture.Levels[0]?.Data is {} data){
                            var level=texture.Levels[0];var red=new byte[level.Width*level.Height];
                            for(int i=0;i<red.Length;i++)red[i]=data[i*4+2];
                            decoded=(red,level.Width,level.Height);
                        }
                    }
                }catch(Exception ex){notes.Add("Tint mask "+path+" could not be read: "+ex.Message);}
                masks[path]=decoded;
            }
            if(decoded is not {} m){notes.Add("Tint mask "+path+" is missing; that layer is left out.");continue;}
            used++;
            float cr=color.R/255f,cg=color.G/255f,cb=color.B/255f;
            for(int y=0;y<Size;y++){
                // Bilinear sample at the texel centre, as the GPU does when the mask is not 512 wide.
                float v=(y+.5f)*m.Height/Size-.5f;int y0=Math.Clamp((int)MathF.Floor(v),0,m.Height-1),y1=Math.Min(y0+1,m.Height-1);float fy=Math.Clamp(v-y0,0,1);
                for(int x=0;x<Size;x++){
                    float u=(x+.5f)*m.Width/Size-.5f;int x0=Math.Clamp((int)MathF.Floor(u),0,m.Width-1),x1=Math.Min(x0+1,m.Width-1);float fx=Math.Clamp(u-x0,0,1);
                    float top=m.Red[y0*m.Width+x0]+(m.Red[y0*m.Width+x1]-m.Red[y0*m.Width+x0])*fx,bottom=m.Red[y1*m.Width+x0]+(m.Red[y1*m.Width+x1]-m.Red[y1*m.Width+x0])*fx;
                    float weight=alpha*(top+(bottom-top)*fy)/255f;int i=y*Size+x;
                    r[i]+=(cr-r[i])*weight;g[i]+=(cg-g[i])*weight;b[i]+=(cb-b[i])*weight;
                }
            }
        }
        var pixels=new byte[Size*Size*4];
        for(int i=0;i<r.Length;i++){
            pixels[i*4]=(byte)Math.Clamp(b[i]*255f+.5f,0,255);pixels[i*4+1]=(byte)Math.Clamp(g[i]*255f+.5f,0,255);pixels[i*4+2]=(byte)Math.Clamp(r[i]*255f+.5f,0,255);pixels[i*4+3]=255;
        }
        return pixels;
    }
    /// <summary>Writes the composite as a DDS in the program's cache folder and makes it readable under a game path,
    /// which is returned. The file name is a hash of the pixels, so equal faces share one file.</summary>
    public static string Register(Assets assets,byte[] bgra)
    {
        Directory.CreateDirectory(CacheFolder);
        string name=Convert.ToHexString(SHA1.HashData(bgra),0,8)+".dds",file=Path.Combine(CacheFolder,name);
        if(!File.Exists(file))File.WriteAllBytes(file,DirectXTextureConversionHelper.Bgra32BytesToDdsBytes(Size,Size,bgra,DirectXTextureConversionHelper.DxgiFormatB8G8R8A8Unorm,generateMipMaps:true));
        string game=$@"textures\actors\character\facegendata\facetint\_preview\{name}";
        assets.External[game]=file;FilesDictionary_class.Dictionary[game]=new(){FullPath=file,BA2File="",Index=-1};
        return game;
    }
    /// <summary>Mean and largest channel difference (0-255) between a composite and a baked tint texture, which is
    /// resampled to the composite's size. Null when the baked file cannot be read.</summary>
    public static (double Mean,int Max)? Compare(Assets assets,byte[] bgra,string bakedPath)
    {
        if(!assets.Exists(bakedPath))return null;
        var texture=DirectXTexWrapperCLI.Loader.ConvertForBitmap(assets.Read(bakedPath));
        if(texture==null||!texture.Loaded||texture.Levels is not {Count:>0}||texture.Levels[0]?.Data is not {} data)return null;
        var level=texture.Levels[0];long sum=0;int max=0;
        for(int y=0;y<Size;y++)for(int x=0;x<Size;x++){
            int sx=Math.Min(x*level.Width/Size,level.Width-1),sy=Math.Min(y*level.Height/Size,level.Height-1);
            for(int c=0;c<3;c++){int d=Math.Abs(bgra[(y*Size+x)*4+c]-data[(sy*level.Width+sx)*4+c]);sum+=d;if(d>max)max=d;}
        }
        return (sum/(double)(Size*Size*3),max);
    }
}
