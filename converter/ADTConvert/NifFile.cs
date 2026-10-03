using System.Numerics;
using System.Buffers.Binary;
using System.Text;
using MaterialLib;

namespace ADTConvert;

/// <summary>A shader a model carries inline (no material file name), with the settings needed to rebuild it as a material file.</summary>
public sealed class InlineShader
{
    public required bool Effect { get; init; }            // BSEffectShaderProperty (BGEM) instead of BSLightingShaderProperty (BGSM)
    public required string Key { get; init; }             // diffuse (lighting) or source (effect) texture, relative to Textures
    public required List<string> Textures { get; init; }  // every texture it uses, relative to Textures
    public required Func<BaseMaterialFile> Build { get; init; }  // a material file reproducing the shader
}

/// <summary>Reads the shader blocks of Fallout 4 NIF files (version 20.2.0.7, Bethesda stream 130) per the NifTools nif.xml layout.</summary>
public static class NifFile
{
    private const uint Fo4StreamVersion = 130;

    /// <summary>Every inline shader in the file; shaders that name a BGSM or BGEM file are skipped.</summary>
    public static List<InlineShader> InlineShaders(byte[] data)
    {
        var result = new List<InlineShader>();
        var blocks = Blocks(data, out var strings);
        if (blocks == null) return result;
        // Geometry blocks point at their shader and alpha property; the alpha property carries blending and testing.
        var alphaFor = new Dictionary<int, int>();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (blocks[i].Type is not ("BSTriShape" or "BSSubIndexTriShape" or "BSMeshLODTriShape" or "BSDynamicTriShape")) continue;
            var r = new Reader(data, blocks[i].Offset);
            r.Int(); r.Skip(4 * (int)r.UInt()); r.Int();  // name, extra data, controller
            r.Skip(4 + 12 + 36 + 4); r.Int();              // flags, translation, rotation, scale, collision object
            r.Skip(16); r.Int();                           // bounding sphere, skin
            var shader = r.Int();
            var alpha = r.Int();
            if (shader >= 0 && alpha >= 0) alphaFor.TryAdd(shader, alpha);
        }
        for (var i = 0; i < blocks.Count; i++)
        {
            try
            {
                var alpha = alphaFor.TryGetValue(i, out var a) && a < blocks.Count && blocks[a].Type == "NiAlphaProperty" ? ReadAlpha(data, blocks[a].Offset) : ((ushort, byte)?)null;
                var shader = blocks[i].Type switch
                {
                    "BSLightingShaderProperty" => Lighting(data, blocks[i].Offset, blocks, strings, alpha),
                    "BSEffectShaderProperty" => Effect(data, blocks[i].Offset, strings, alpha),
                    _ => null,
                };
                if (shader != null) result.Add(shader);
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                // A block that does not match the documented layout is left alone.
            }
        }
        return result;
    }

    /// <summary>The source texture (relative to Textures, lowercase) of every BSSkyShaderProperty block, which the engine loads by name.</summary>
    public static List<string> SkyTextures(byte[] data)
    {
        var result = new List<string>();
        var blocks = Blocks(data, out _);
        if (blocks == null) return result;
        foreach (var block in blocks.Where(b => b.Type == "BSSkyShaderProperty"))
        {
            try
            {
                var r = new Reader(data, block.Offset);
                r.Int(); r.Skip(4 * (int)r.UInt()); r.Int();  // name, extra data, controller
                r.Skip(8 + 16);                                // shader flags, UV offset and scale
                var texture = Paths.Normalize(r.SizedString(), "textures");
                if (texture.Length > 0) result.Add(texture);
            }
            catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                // A block that does not match the documented layout is left alone.
            }
        }
        return result;
    }

    /// <summary>A copy of the file with every material name and texture path passed through the map (null keeps a path):
    /// header strings naming a BGSM or BGEM file, BSShaderTextureSet entries and the texture paths of BSEffectShaderProperty
    /// blocks. Block sizes are recomputed. Returns null when the file is not a Fallout 4 NIF.</summary>
    public static byte[]? Rewrite(byte[] data, Func<string, string?> map)
    {
        var newline = Array.IndexOf(data, (byte)'\n');
        if (newline < 0 || newline > 64 || !Encoding.ASCII.GetString(data, 0, newline).StartsWith("Gamebryo File Format", StringComparison.Ordinal)) return null;
        var r = new Reader(data, newline + 1);
        if (r.UInt() != 0x14020007) return null;
        r.Skip(1);
        r.UInt();
        var numBlocks = (int)r.UInt();
        if (r.UInt() != Fo4StreamVersion) return null;
        r.ExportString(); r.ExportString(); r.ExportString(); r.ExportString();
        var numTypes = r.UShort();
        var types = new string[numTypes];
        for (var i = 0; i < numTypes; i++) types[i] = r.SizedString();
        var typeIndex = new ushort[numBlocks];
        for (var i = 0; i < numBlocks; i++) typeIndex[i] = r.UShort();
        var sizesOffset = r.Position;
        var sizes = new int[numBlocks];
        for (var i = 0; i < numBlocks; i++) sizes[i] = (int)r.UInt();
        var numStrings = (int)r.UInt();
        r.UInt();
        var strings = new List<string>(numStrings);
        for (var i = 0; i < numStrings; i++) strings.Add(r.SizedString());
        var groupsStart = r.Position;
        r.Skip(4 * (int)r.UInt());
        var blocksStart = r.Position;

        string Mapped(string path) => path.Length == 0 ? path : map(path) ?? path;
        var newStrings = strings.Select(s => NamesMaterialFile(s) ? Mapped(s) : s).ToList();

        var output = new MemoryStream();
        var w = new BinaryWriter(output, Encoding.Latin1);
        w.Write(data, 0, sizesOffset);
        var sizesPosition = output.Position;
        w.Write(new byte[4 * numBlocks]);
        w.Write((uint)newStrings.Count);
        w.Write((uint)(newStrings.Count == 0 ? 0 : newStrings.Max(s => Encoding.Latin1.GetByteCount(s))));
        foreach (var s in newStrings) WriteSized(w, s);
        w.Write(data, groupsStart, blocksStart - groupsStart);

        var newSizes = new int[numBlocks];
        var offset = blocksStart;
        for (var i = 0; i < numBlocks; i++)
        {
            var type = types[typeIndex[i] & 0x7FFF];
            var start = output.Position;
            if (type == "BSShaderTextureSet")
            {
                var t = new Reader(data, offset);
                var count = (int)t.UInt();
                w.Write((uint)count);
                for (var k = 0; k < count; k++) WriteSized(w, Mapped(t.SizedString()));
                w.Write(data, t.Position, offset + sizes[i] - t.Position);
            }
            else if (type == "BSEffectShaderProperty")
            {
                var e = new Reader(data, offset);
                e.Int(); e.Skip(4 * (int)e.UInt()); e.Int();  // name, extra data, controller
                e.Skip(8 + 16);                                // shader flags, UV offset and scale
                w.Write(data, offset, e.Position - offset);
                WriteSized(w, Mapped(e.SizedString()));        // source texture
                var middle = e.Position;
                e.Skip(4 + 16 + 16 + 8);                       // clamp and lighting bytes, falloff, base color, scale, soft depth
                w.Write(data, middle, e.Position - middle);
                for (var k = 0; k < 4; k++) WriteSized(w, Mapped(e.SizedString()));  // greyscale, environment, normal, environment mask
                w.Write(data, e.Position, offset + sizes[i] - e.Position);
            }
            else
            {
                w.Write(data, offset, sizes[i]);
            }
            newSizes[i] = (int)(output.Position - start);
            offset += sizes[i];
        }
        w.Write(data, offset, data.Length - offset);  // footer: the root list
        w.Flush();
        var bytes = output.ToArray();
        for (var i = 0; i < numBlocks; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)sizesPosition + 4 * i), (uint)newSizes[i]);
        return bytes;
    }

    private static void WriteSized(BinaryWriter w, string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        w.Write((uint)bytes.Length);
        w.Write(bytes);
    }

    /// <summary>Every material name and texture path the file references, as stored.</summary>
    public static List<string> References(byte[] data)
    {
        var found = new List<string>();
        Rewrite(data, path => { found.Add(path); return null; });
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private sealed record Block(string Type, int Offset, int Size);

    private static List<Block>? Blocks(byte[] data, out List<string> strings)
    {
        strings = [];
        var newline = Array.IndexOf(data, (byte)'\n');
        if (newline < 0 || newline > 64 || !Encoding.ASCII.GetString(data, 0, newline).StartsWith("Gamebryo File Format", StringComparison.Ordinal)) return null;
        var r = new Reader(data, newline + 1);
        var version = r.UInt();
        if (version != 0x14020007) return null;
        r.Skip(1);  // endian
        r.UInt();   // user version
        var numBlocks = (int)r.UInt();
        var bsVersion = r.UInt();
        if (bsVersion != Fo4StreamVersion) return null;
        r.ExportString();  // author
        r.ExportString();  // process script (stream versions below 131)
        r.ExportString();  // export script
        r.ExportString();  // max filepath (stream versions 103 and up)
        var numTypes = r.UShort();
        var types = new string[numTypes];
        for (var i = 0; i < numTypes; i++) types[i] = r.SizedString();
        var typeIndex = new ushort[numBlocks];
        for (var i = 0; i < numBlocks; i++) typeIndex[i] = r.UShort();
        var sizes = new int[numBlocks];
        for (var i = 0; i < numBlocks; i++) sizes[i] = (int)r.UInt();
        var numStrings = (int)r.UInt();
        r.UInt();  // longest string
        for (var i = 0; i < numStrings; i++) strings.Add(r.SizedString());
        var numGroups = (int)r.UInt();
        r.Skip(4 * numGroups);
        var blocks = new List<Block>(numBlocks);
        var offset = r.Position;
        for (var i = 0; i < numBlocks; i++)
        {
            blocks.Add(new Block(types[typeIndex[i] & 0x7FFF], offset, sizes[i]));
            offset += sizes[i];
        }
        return blocks;
    }

    private static (ushort Flags, byte Threshold) ReadAlpha(byte[] data, int offset)
    {
        var r = new Reader(data, offset);
        r.Int(); r.Skip(4 * (int)r.UInt()); r.Int();  // name, extra data, controller
        return (r.UShort(), r.Byte());
    }

    private static bool NamesMaterialFile(string? name)
    {
        return name != null && (name.EndsWith(".bgsm", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bgem", StringComparison.OrdinalIgnoreCase));
    }

    private static string? Str(List<string> strings, int index) => index >= 0 && index < strings.Count ? strings[index] : null;

    private static string Tex(string path) => Paths.Normalize(path, "textures");

    private static InlineShader? Lighting(byte[] data, int offset, List<Block> blocks, List<string> strings, (ushort Flags, byte Threshold)? alpha)
    {
        var r = new Reader(data, offset);
        var shaderType = r.UInt();
        var name = Str(strings, r.Int());
        if (NamesMaterialFile(name)) return null;
        r.Skip(4 * (int)r.UInt()); r.Int();  // extra data, controller
        var f1 = r.UInt();
        var f2 = r.UInt();
        float uOffset = r.Float(), vOffset = r.Float(), uScale = r.Float(), vScale = r.Float();
        var textureSet = r.Int();
        float er = r.Float(), eg = r.Float(), eb = r.Float();
        var emissiveMult = r.Float();
        var rootMaterial = Str(strings, r.Int()) ?? "";
        var clamp = r.UInt();
        var alphaValue = r.Float();
        var refraction = r.Float();
        var smoothness = r.Float();
        float sr = r.Float(), sg = r.Float(), sb = r.Float();
        var specularStrength = r.Float();
        var subsurfaceRolloff = r.Float();
        var rimPower = r.Float();
        var backlightPower = rimPower >= float.MaxValue && !float.IsPositiveInfinity(rimPower) ? r.Float() : 0f;
        var grayscaleScale = r.Float();
        var fresnel = r.Float();
        float wetSpecScale = r.Float(), wetSpecPower = r.Float(), wetMinVar = r.Float(), wetEnvScale = r.Float(), wetFresnel = r.Float(), wetMetalness = r.Float();
        float envScale = 1f;
        bool ssr = false, wetSsr = false;
        if (shaderType == 1)
        {
            envScale = r.Float();
            ssr = r.Byte() != 0;
            wetSsr = r.Byte() != 0;
        }
        if (textureSet < 0 || textureSet >= blocks.Count || blocks[textureSet].Type != "BSShaderTextureSet") return null;
        var t = new Reader(data, blocks[textureSet].Offset);
        var count = (int)t.UInt();
        var slots = new string[count];
        for (var i = 0; i < count; i++) slots[i] = t.SizedString();
        string At(int i) => i < slots.Length ? Tex(slots[i]) : "";
        if (At(0).Length == 0) return null;

        BaseMaterialFile Build()
        {
            var m = new BGSM
            {
                // Texture set order for Fallout 4: 0 diffuse, 1 normal, 2 glow, 3 greyscale, 4 environment, 5 wrinkles, 6 inner layer, 7 smooth/spec.
                DiffuseTexture = At(0), NormalTexture = At(1), GlowTexture = At(2), GreyscaleTexture = At(3), EnvmapTexture = At(4),
                WrinklesTexture = At(5), InnerLayerTexture = At(6), SmoothSpecTexture = At(7),
                SpecularEnabled = Bit(f1, 0), SpecularColor = Rgb(sr, sg, sb), SpecularMult = specularStrength, Smoothness = smoothness, FresnelPower = fresnel,
                GrayscaleToPaletteScale = grayscaleScale, SubsurfaceLightingRolloff = subsurfaceRolloff,
                RimLighting = rimPower < float.MaxValue, RimPower = rimPower < float.MaxValue ? rimPower : 2f, BackLightPower = backlightPower,
                WetnessControlSpecScale = wetSpecScale, WetnessControlSpecPowerScale = wetSpecPower, WetnessControlSpecMinvar = wetMinVar,
                WetnessControlEnvMapScale = wetEnvScale, WetnessControlFresnelPower = wetFresnel, WetnessControlMetalness = wetMetalness,
                RootMaterialPath = rootMaterial,
                EmitEnabled = Bit(f1, 22), EmittanceColor = Rgb(er, eg, eb), EmittanceMult = emissiveMult,
                ModelSpaceNormals = Bit(f1, 12), ExternalEmittance = Bit(f1, 29), CastShadows = Bit(f1, 9), ReceiveShadows = true,
                HideSecret = Bit(f1, 20), DissolveFade = Bit(f1, 19), Glowmap = Bit(f2, 6), EnvironmentMappingEye = Bit(f1, 17),
                Hair = Bit(f1, 18), Tree = Bit(f2, 29), Facegen = Bit(f1, 10), SkinTint = Bit(f1, 21), Tessellate = Bit(f1, 25),
                AnisoLighting = Bit(f2, 21), SkewSpecularAlpha = Bit(f2, 22),
            };
            Common(m, f1, f2, clamp, uOffset, vOffset, uScale, vScale, alphaValue, alpha);
            m.Refraction = Bit(f1, 15);
            m.RefractionPower = refraction;
            m.EnvironmentMapping = Bit(f1, 7);
            m.EnvironmentMappingMaskScale = envScale;
            m.ScreenSpaceReflections = ssr;
            m.WetnessControlScreenSpaceReflections = wetSsr;
            m.GrayscaleToPaletteColor = Bit(f1, 4);
            return m;
        }

        return new InlineShader { Effect = false, Key = At(0), Textures = slots.Select(Tex).Where(s => s.Length > 0).Distinct().ToList(), Build = Build };
    }

    private static InlineShader? Effect(byte[] data, int offset, List<string> strings, (ushort Flags, byte Threshold)? alpha)
    {
        var r = new Reader(data, offset);
        var name = Str(strings, r.Int());
        if (NamesMaterialFile(name)) return null;
        r.Skip(4 * (int)r.UInt()); r.Int();  // extra data, controller
        var f1 = r.UInt();
        var f2 = r.UInt();
        float uOffset = r.Float(), vOffset = r.Float(), uScale = r.Float(), vScale = r.Float();
        var source = Tex(r.SizedString());
        var clamp = r.Byte();
        var lightingInfluence = r.Byte();
        var envMinLod = r.Byte();
        r.Byte();
        float fStart = r.Float(), fStop = r.Float(), fStartOpacity = r.Float(), fStopOpacity = r.Float();
        float br = r.Float(), bg = r.Float(), bb = r.Float(), ba = r.Float();
        var baseScale = r.Float();
        var softDepth = r.Float();
        var greyscale = Tex(r.SizedString());
        var envmap = Tex(r.SizedString());
        var normal = Tex(r.SizedString());
        var envMask = Tex(r.SizedString());
        var envScale = r.Float();
        if (source.Length == 0) return null;

        BaseMaterialFile Build()
        {
            var m = new BGEM
            {
                BaseTexture = source, GrayscaleTexture = greyscale, EnvmapTexture = envmap, NormalTexture = normal, EnvmapMaskTexture = envMask,
                BaseColor = Rgb(br, bg, bb), BaseColorScale = baseScale, FalloffStartAngle = fStart, FalloffStopAngle = fStop,
                FalloffStartOpacity = fStartOpacity, FalloffStopOpacity = fStopOpacity, LightingInfluence = lightingInfluence / 255f,
                EnvmapMinLOD = envMinLod, SoftDepth = softDepth, FalloffEnabled = Bit(f1, 6), FalloffColorEnabled = Bit(f1, 8),
                GrayscaleToPaletteAlpha = Bit(f1, 5), SoftEnabled = Bit(f1, 30), EffectLightingEnabled = Bit(f2, 30), Glowmap = Bit(f2, 6),
            };
            Common(m, f1, f2, clamp, uOffset, vOffset, uScale, vScale, ba, alpha);
            m.EnvironmentMapping = Bit(f1, 7);
            m.EnvironmentMappingMaskScale = envScale;
            m.GrayscaleToPaletteColor = Bit(f1, 4);
            return m;
        }

        var textures = new[] { source, greyscale, envmap, normal, envMask }.Where(s => s.Length > 0).Distinct().ToList();
        return new InlineShader { Effect = true, Key = source, Textures = textures, Build = Build };
    }

    // Settings both material kinds share: tiling, UV transform, alpha, depth buffer, sidedness, decals.
    private static void Common(BaseMaterialFile m, uint f1, uint f2, uint clamp, float uOffset, float vOffset, float uScale, float vScale, float alphaValue, (ushort Flags, byte Threshold)? alpha)
    {
        // Clamp modes: 0 clamp both, 1 clamp U wrap V, 2 wrap U clamp V, 3 wrap both.
        m.TileU = clamp is 2 or 3;
        m.TileV = clamp is 1 or 3;
        m.UOffset = uOffset;
        m.VOffset = vOffset;
        m.UScale = uScale;
        m.VScale = vScale;
        m.Alpha = alphaValue;
        m.ZBufferWrite = Bit(f2, 0);
        m.ZBufferTest = Bit(f1, 31);
        m.TwoSided = Bit(f2, 4);
        m.Decal = Bit(f1, 26);
        m.DecalNoFade = Bit(f1, 26) && Bit(f2, 3);
        m.AlphaBlendMode = BaseMaterialFile.AlphaBlendModeType.Unknown;
        if (alpha is { } a)
        {
            var blend = (a.Flags & 0x1) != 0;
            var source = (a.Flags >> 1) & 0xF;
            var destination = (a.Flags >> 5) & 0xF;
            m.AlphaBlendMode = !blend ? (source == 6 && destination == 7 ? BaseMaterialFile.AlphaBlendModeType.Unknown : BaseMaterialFile.AlphaBlendModeType.None)
                : source == 6 && destination == 0 ? BaseMaterialFile.AlphaBlendModeType.Additive
                : source == 4 && destination == 1 ? BaseMaterialFile.AlphaBlendModeType.Multiplicative
                : BaseMaterialFile.AlphaBlendModeType.Standard;
            m.AlphaTest = (a.Flags & 0x200) != 0;
            m.AlphaTestRef = a.Threshold;
        }
    }

    private static bool Bit(uint flags, int bit) => (flags & (1u << bit)) != 0;

    private static uint Rgb(float r, float g, float b)
    {
        static uint C(float v) => (uint)Math.Clamp((int)(v * 255f), 0, 255);
        return (C(r) << 16) | (C(g) << 8) | C(b);
    }

    private sealed class Reader(byte[] data, int position)
    {
        public int Position { get; private set; } = position;

        public void Skip(int count) => Position += count;
        public byte Byte() => data[Position++];

        public ushort UShort()
        {
            var v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Position));
            Position += 2;
            return v;
        }

        public uint UInt()
        {
            var v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(Position));
            Position += 4;
            return v;
        }

        public int Int() => (int)UInt();
        public float Float() => BitConverter.Int32BitsToSingle(Int());

        public string SizedString()
        {
            var length = (int)UInt();
            var text = Encoding.Latin1.GetString(data, Position, length);
            Position += length;
            return text;
        }

        public string ExportString()
        {
            var length = Byte();
            var text = Encoding.Latin1.GetString(data, Position, length).TrimEnd('\0');
            Position += length;
            return text;
        }
    }
}
