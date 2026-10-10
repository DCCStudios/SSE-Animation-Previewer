using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DirectXTexWrapperCLI;
namespace SSEPreview;
public static class TextureInspection
{
    public static void Save(string source,string target)
    {
        var tex=Loader.LoadTextures([File.ReadAllBytes(source)],false,true)[0];
        var level=tex.Levels[0];
        if(tex.GlPixelType!=5121||level.Data.Length!=level.Width*level.Height*4)throw new InvalidDataException($"Unsupported diagnostic format {tex.GlPixelFormat}/{tex.GlPixelType}");
        using var bmp=new Bitmap(level.Width,level.Height,PixelFormat.Format32bppArgb);
        byte[] bytes=(byte[])level.Data.Clone();
        if(tex.GlPixelFormat==6408)for(int i=0;i<bytes.Length;i+=4)(bytes[i],bytes[i+2])=(bytes[i+2],bytes[i]);
        var data=bmp.LockBits(new(0,0,bmp.Width,bmp.Height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
        Marshal.Copy(bytes,0,data.Scan0,bytes.Length);bmp.UnlockBits(data);bmp.Save(target,ImageFormat.Png);
    }
}
