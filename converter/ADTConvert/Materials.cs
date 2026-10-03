using MaterialLib;

namespace ADTConvert;

/// <summary>Reads BGSM and BGEM material files and exposes their texture slots uniformly.</summary>
public static class MaterialFiles
{
    public static BaseMaterialFile Read(byte[] bytes)
    {
        var signature = bytes.Length >= 4 ? BitConverter.ToUInt32(bytes, 0) : 0u;
        BaseMaterialFile material = signature == BGEM.Signature ? new BGEM() : new BGSM();
        using var reader = new BinaryReader(new MemoryStream(bytes));
        material.Deserialize(reader);
        return material;
    }

    /// <summary>Every texture slot of the material as a getter and setter pair; paths are stored as the file has them.</summary>
    public static IEnumerable<(Func<string?> Get, Action<string?> Set)> TextureSlots(BaseMaterialFile material)
    {
        switch (material)
        {
            case BGSM m:
                yield return (() => m.DiffuseTexture, v => m.DiffuseTexture = v);
                yield return (() => m.NormalTexture, v => m.NormalTexture = v);
                yield return (() => m.SmoothSpecTexture, v => m.SmoothSpecTexture = v);
                yield return (() => m.GreyscaleTexture, v => m.GreyscaleTexture = v);
                yield return (() => m.EnvmapTexture, v => m.EnvmapTexture = v);
                yield return (() => m.GlowTexture, v => m.GlowTexture = v);
                yield return (() => m.InnerLayerTexture, v => m.InnerLayerTexture = v);
                yield return (() => m.WrinklesTexture, v => m.WrinklesTexture = v);
                yield return (() => m.DisplacementTexture, v => m.DisplacementTexture = v);
                yield return (() => m.SpecularTexture, v => m.SpecularTexture = v);
                yield return (() => m.LightingTexture, v => m.LightingTexture = v);
                yield return (() => m.FlowTexture, v => m.FlowTexture = v);
                yield return (() => m.DistanceFieldAlphaTexture, v => m.DistanceFieldAlphaTexture = v);
                break;
            case BGEM e:
                yield return (() => e.BaseTexture, v => e.BaseTexture = v);
                yield return (() => e.GrayscaleTexture, v => e.GrayscaleTexture = v);
                yield return (() => e.EnvmapTexture, v => e.EnvmapTexture = v);
                yield return (() => e.NormalTexture, v => e.NormalTexture = v);
                yield return (() => e.EnvmapMaskTexture, v => e.EnvmapMaskTexture = v);
                yield return (() => e.SpecularTexture, v => e.SpecularTexture = v);
                yield return (() => e.LightingTexture, v => e.LightingTexture = v);
                yield return (() => e.GlowTexture, v => e.GlowTexture = v);
                break;
        }
    }

    /// <summary>The textures the material uses, relative to Textures, lowercase with backslashes.</summary>
    public static List<string> Textures(BaseMaterialFile material)
    {
        return TextureSlots(material).Select(s => Paths.Normalize(s.Get() ?? "", "textures")).Where(t => t.Length > 0).Distinct().ToList();
    }

    /// <summary>The diffuse (BGSM) or base (BGEM) texture, relative to Textures.</summary>
    public static string Diffuse(BaseMaterialFile material)
    {
        var path = material switch { BGSM m => m.DiffuseTexture, BGEM e => e.BaseTexture, _ => null };
        return Paths.Normalize(path ?? "", "textures");
    }

    public static string? Normal(BaseMaterialFile material)
    {
        var path = material switch { BGSM m => m.NormalTexture, BGEM e => e.NormalTexture, _ => null };
        return path == null ? null : Paths.Normalize(path, "textures");
    }
}
