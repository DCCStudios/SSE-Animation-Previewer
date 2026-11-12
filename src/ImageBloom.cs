using System.Drawing.Imaging;

namespace SSEPreview;

/// <summary>A bloom over a finished capture, so effect pictures read closer to a game running Community Shaders' bloom.
/// <para>The viewport renders into an 8-bit buffer, so this works on display values: a soft bright pass from 0.6 up,
/// blurred at a quarter of the size with a Gaussian of about 1.2% of the image width, then added back times
/// <c>strength</c>. The game blooms the HDR scene before tone mapping, so light that clipped to white here carries
/// less energy than it did in game: treat this as a look check, not a measurement.</para></summary>
static class ImageBloom
{
    public static void Apply(Bitmap image,float strength,float threshold=.6f)
    {
        int w=image.Width,h=image.Height;
        var rect=new Rectangle(0,0,w,h);
        var data=image.LockBits(rect,ImageLockMode.ReadWrite,PixelFormat.Format32bppArgb);
        try{
            var pixels=new byte[data.Stride*h];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0,pixels,0,pixels.Length);
            const int scale=4;
            int sw=Math.Max(1,w/scale),sh=Math.Max(1,h/scale);
            var r=new float[sw*sh];var g=new float[sw*sh];var b=new float[sw*sh];
            // Bright pass with a soft knee, box-downsampled.
            for(int y=0;y<sh;y++)for(int x=0;x<sw;x++){
                float sr=0,sg=0,sb=0;int n=0;
                for(int dy=0;dy<scale;dy++)for(int dx=0;dx<scale;dx++){
                    int px=x*scale+dx,py=y*scale+dy;if(px>=w||py>=h)continue;
                    int i=py*data.Stride+px*4;
                    float cb=pixels[i]/255f,cg=pixels[i+1]/255f,cr=pixels[i+2]/255f;
                    float lum=Math.Max(cr,Math.Max(cg,cb));
                    float weight=Math.Clamp((lum-threshold)/(1-threshold),0,1);weight*=weight;
                    sr+=cr*weight;sg+=cg*weight;sb+=cb*weight;n++;
                }
                int k=y*sw+x;r[k]=sr/Math.Max(n,1);g[k]=sg/Math.Max(n,1);b[k]=sb/Math.Max(n,1);
            }
            float sigma=Math.Max(1f,w*.012f/scale);
            Blur(r,sw,sh,sigma);Blur(g,sw,sh,sigma);Blur(b,sw,sh,sigma);
            // Add back, bilinear upsampling.
            for(int y=0;y<h;y++)for(int x=0;x<w;x++){
                float fx=Math.Clamp((x+.5f)/scale-.5f,0,sw-1),fy=Math.Clamp((y+.5f)/scale-.5f,0,sh-1);
                int x0=(int)fx,y0=(int)fy,x1=Math.Min(x0+1,sw-1),y1=Math.Min(y0+1,sh-1);float tx=fx-x0,ty=fy-y0;
                float Sample(float[] c)=>(c[y0*sw+x0]*(1-tx)+c[y0*sw+x1]*tx)*(1-ty)+(c[y1*sw+x0]*(1-tx)+c[y1*sw+x1]*tx)*ty;
                int i=y*data.Stride+x*4;
                pixels[i]=(byte)Math.Clamp(pixels[i]+Sample(b)*strength*255,0,255);
                pixels[i+1]=(byte)Math.Clamp(pixels[i+1]+Sample(g)*strength*255,0,255);
                pixels[i+2]=(byte)Math.Clamp(pixels[i+2]+Sample(r)*strength*255,0,255);
            }
            System.Runtime.InteropServices.Marshal.Copy(pixels,0,data.Scan0,pixels.Length);
        }finally{image.UnlockBits(data);}
    }

    static void Blur(float[] c,int w,int h,float sigma)
    {
        int radius=(int)Math.Ceiling(sigma*3);
        var kernel=new float[radius*2+1];float sum=0;
        for(int i=-radius;i<=radius;i++){kernel[i+radius]=MathF.Exp(-i*i/(2*sigma*sigma));sum+=kernel[i+radius];}
        for(int i=0;i<kernel.Length;i++)kernel[i]/=sum;
        var tmp=new float[c.Length];
        for(int y=0;y<h;y++)for(int x=0;x<w;x++){float s=0;for(int k=-radius;k<=radius;k++)s+=c[y*w+Math.Clamp(x+k,0,w-1)]*kernel[k+radius];tmp[y*w+x]=s;}
        for(int y=0;y<h;y++)for(int x=0;x<w;x++){float s=0;for(int k=-radius;k<=radius;k++)s+=tmp[Math.Clamp(y+k,0,h-1)*w+x]*kernel[k+radius];c[y*w+x]=s;}
    }
}
